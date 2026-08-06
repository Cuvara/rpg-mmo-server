package messages

import (
	"encoding/json"
	"testing"
)

func ent(id string, x, y float32, hp int) EntitySnapshot {
	return EntitySnapshot{ID: id, Type: "player", X: x, Y: y, HP: hp, MaxHP: 100}
}

func TestSnapshotState_Apply(t *testing.T) {
	tests := []struct {
		name     string
		msgs     []SnapshotMessage
		wantIDs  []string
		wantTick uint64
		wantAck  uint64
	}{
		{
			name: "keyframe seeds state",
			msgs: []SnapshotMessage{
				{Tick: 1, Full: true, AckTick: 3, Entities: []EntitySnapshot{ent("a", 1, 1, 100), ent("b", 2, 2, 100)}},
			},
			wantIDs: []string{"a", "b"}, wantTick: 1, wantAck: 3,
		},
		{
			name: "delta upserts changed entity only",
			msgs: []SnapshotMessage{
				{Tick: 1, Full: true, Entities: []EntitySnapshot{ent("a", 1, 1, 100), ent("b", 2, 2, 100)}},
				{Tick: 2, AckTick: 5, Entities: []EntitySnapshot{ent("a", 9, 9, 80)}},
			},
			wantIDs: []string{"a", "b"}, wantTick: 2, wantAck: 5,
		},
		{
			name: "removed deletes entity",
			msgs: []SnapshotMessage{
				{Tick: 1, Full: true, Entities: []EntitySnapshot{ent("a", 1, 1, 100), ent("b", 2, 2, 100)}},
				{Tick: 2, Removed: []string{"b"}},
			},
			wantIDs: []string{"a"}, wantTick: 2,
		},
		{
			name: "keyframe replaces stale entities",
			msgs: []SnapshotMessage{
				{Tick: 1, Full: true, Entities: []EntitySnapshot{ent("a", 1, 1, 100), ent("b", 2, 2, 100)}},
				{Tick: 2, Full: true, Entities: []EntitySnapshot{ent("c", 3, 3, 100)}},
			},
			wantIDs: []string{"c"}, wantTick: 2,
		},
		{
			name: "ack is monotonic and tick never regresses",
			msgs: []SnapshotMessage{
				{Tick: 7, Full: true, AckTick: 9, Entities: []EntitySnapshot{ent("a", 1, 1, 100)}},
				{Tick: 3, AckTick: 0},
			},
			wantIDs: []string{"a"}, wantTick: 7, wantAck: 9,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			s := NewSnapshotState()
			for _, m := range tt.msgs {
				s.Apply(m)
			}
			if s.Len() != len(tt.wantIDs) {
				t.Fatalf("entity count = %d, want %d (%v)", s.Len(), len(tt.wantIDs), s.Entities)
			}
			for _, id := range tt.wantIDs {
				if _, ok := s.Get(id); !ok {
					t.Errorf("entity %q missing", id)
				}
			}
			if s.Tick != tt.wantTick {
				t.Errorf("tick = %d, want %d", s.Tick, tt.wantTick)
			}
			if s.AckTick != tt.wantAck {
				t.Errorf("ack = %d, want %d", s.AckTick, tt.wantAck)
			}
		})
	}
}

func TestSnapshotState_DeltaReconstructsFullState(t *testing.T) {
	// Keyframe + deltas must land on exactly the same map as the equivalent
	// keyframe-only stream.
	full := NewSnapshotState()
	delta := NewSnapshotState()

	base := []EntitySnapshot{ent("a", 0, 0, 100), ent("b", 5, 5, 100), ent("c", 9, 9, 100)}
	full.Apply(SnapshotMessage{Tick: 1, Full: true, Entities: base})
	delta.Apply(SnapshotMessage{Tick: 1, Full: true, Entities: base})

	for tick := uint64(2); tick <= 20; tick++ {
		moved := ent("a", float32(tick), 0, 100-int(tick))
		full.Apply(SnapshotMessage{Tick: tick, Full: true, AckTick: tick,
			Entities: []EntitySnapshot{moved, ent("b", 5, 5, 100), ent("c", 9, 9, 100)}})
		delta.Apply(SnapshotMessage{Tick: tick, AckTick: tick, Entities: []EntitySnapshot{moved}})
	}

	// c leaves AOI on the last tick.
	full.Apply(SnapshotMessage{Tick: 21, Full: true, Entities: []EntitySnapshot{
		ent("a", 20, 0, 80), ent("b", 5, 5, 100)}})
	delta.Apply(SnapshotMessage{Tick: 21, Removed: []string{"c"}})

	if len(full.Entities) != len(delta.Entities) {
		t.Fatalf("size mismatch: full=%d delta=%d", len(full.Entities), len(delta.Entities))
	}
	for id, want := range full.Entities {
		got, ok := delta.Entities[id]
		if !ok {
			t.Fatalf("delta stream lost entity %q", id)
		}
		if got != want {
			t.Errorf("entity %q: delta=%+v want %+v", id, got, want)
		}
	}
	if full.Tick != delta.Tick || full.AckTick != delta.AckTick {
		t.Errorf("tick/ack mismatch: full=(%d,%d) delta=(%d,%d)",
			full.Tick, full.AckTick, delta.Tick, delta.AckTick)
	}
}

func TestSnapshotMessage_OmitsNewFieldsWhenDefault(t *testing.T) {
	// Backward compatibility: a plain full-state snapshot with no ack must
	// serialize exactly as it did before delta support existed.
	b, err := json.Marshal(SnapshotMessage{Tick: 4, Entities: []EntitySnapshot{}})
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	got := string(b)
	want := `{"tick":4,"entities":[]}`
	if got != want {
		t.Errorf("marshal = %s, want %s", got, want)
	}
}

func TestSnapshotMessage_RoundTripsNewFields(t *testing.T) {
	in := SnapshotMessage{
		Tick: 12, AckTick: 7, Full: true,
		Entities: []EntitySnapshot{ent("a", 1, 2, 90)},
		Removed:  []string{"z"},
	}
	b, err := json.Marshal(in)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	var out SnapshotMessage
	if err := json.Unmarshal(b, &out); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if out.AckTick != 7 || !out.Full || len(out.Removed) != 1 || out.Removed[0] != "z" {
		t.Errorf("round trip lost fields: %+v (json %s)", out, b)
	}
}
