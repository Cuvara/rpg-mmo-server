package load

import (
	"bytes"
	"math"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/messages"
)

func TestDecodeCountedReportsWireBytes(t *testing.T) {
	env, err := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 1})
	if err != nil {
		t.Fatal(err)
	}
	raw, err := messages.Encode(env)
	if err != nil {
		t.Fatal(err)
	}

	got, n, err := decodeCounted(bytes.NewReader(raw))
	if err != nil {
		t.Fatalf("decodeCounted: %v", err)
	}
	if got.Type != messages.MsgInput {
		t.Errorf("Type = %v, want MsgInput", got.Type)
	}
	// The byte count must include the 4-byte length prefix: it is a throughput
	// measurement of the wire, not of the payload.
	if n != len(raw) {
		t.Errorf("n = %d, want %d (full frame including the length prefix)", n, len(raw))
	}
}

func TestDecodeCountedRejectsOversizedFrame(t *testing.T) {
	// Length prefix claiming 2MB, above the 1MB cap shared/messages enforces.
	frame := []byte{0x00, 0x20, 0x00, 0x00}
	if _, _, err := decodeCounted(bytes.NewReader(frame)); err == nil {
		t.Error("expected an error for an oversized frame")
	}
}

func TestDecodeCountedTruncated(t *testing.T) {
	if _, _, err := decodeCounted(bytes.NewReader([]byte{0, 0})); err == nil {
		t.Error("expected an error for a truncated length prefix")
	}
}

func TestMovementVector(t *testing.T) {
	tests := []struct {
		name       string
		mode       string
		idx        int
		players    int
		wantX      float32
		wantY      float32
		exactMatch bool
	}{
		{"still is a zero vector", MovementStill, 0, 10, 0, 0, true},
		{"cluster moves along +X", MovementCluster, 3, 10, 1, 0, true},
		{"spread player 0 heads +X", MovementSpread, 0, 10, 1, 0, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p := &player{cfg: Config{Movement: tt.mode, Players: tt.players}, idx: tt.idx}
			x, y := p.movementVector()
			if tt.exactMatch {
				if x != tt.wantX || y != tt.wantY {
					t.Errorf("got (%v, %v), want (%v, %v)", x, y, tt.wantX, tt.wantY)
				}
				return
			}
			if math.Abs(float64(x-tt.wantX)) > 1e-6 || math.Abs(float64(y-tt.wantY)) > 1e-6 {
				t.Errorf("got (%v, %v), want (%v, %v)", x, y, tt.wantX, tt.wantY)
			}
		})
	}
}

// Every spread heading must be a unit vector inside the +X/+Y quadrant, so no
// player is clamped against the map's zero edge (which would silently turn a
// moving player into a still one and corrupt the movement-mode experiment).
func TestMovementSpreadStaysUnitAndPositive(t *testing.T) {
	const n = 64
	for i := 0; i < n; i++ {
		p := &player{cfg: Config{Movement: MovementSpread, Players: n}, idx: i}
		x, y := p.movementVector()
		if x < 0 || y < 0 {
			t.Errorf("player %d heading (%v, %v) leaves the +X/+Y quadrant", i, x, y)
		}
		mag := math.Hypot(float64(x), float64(y))
		if math.Abs(mag-1) > 1e-6 {
			t.Errorf("player %d heading magnitude = %v, want 1", i, mag)
		}
		// Above GameConstants.MaxInputMagnitude (1.5) the server drops the input.
		if mag > 1.5 {
			t.Errorf("player %d heading magnitude %v would be rejected by the server", i, mag)
		}
	}
}

// A single-player run must not divide by zero when computing spread angles.
func TestMovementSpreadSinglePlayer(t *testing.T) {
	p := &player{cfg: Config{Movement: MovementSpread, Players: 1}, idx: 0}
	x, y := p.movementVector()
	if math.IsNaN(float64(x)) || math.IsNaN(float64(y)) {
		t.Errorf("got NaN heading (%v, %v)", x, y)
	}
}

func TestDrainPending(t *testing.T) {
	ch := make(chan pendingInput, 4)
	ch <- pendingInput{tick: 1}
	ch <- pendingInput{tick: 2}
	got := drainPending(nil, ch)
	if len(got) != 2 || got[0].tick != 1 || got[1].tick != 2 {
		t.Errorf("drainPending = %v, want ticks 1,2 in order", got)
	}
	// Draining an empty channel must not block.
	if got := drainPending(got, ch); len(got) != 2 {
		t.Errorf("drainPending on empty = %v, want unchanged", got)
	}
}
