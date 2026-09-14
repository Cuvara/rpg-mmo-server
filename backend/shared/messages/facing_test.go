package messages

import (
	"math"
	"strings"
	"testing"
)

// The whole point of the biased encoding is that zero cannot be confused with a
// real direction. These tests pin that property, the round-trip fidelity, and the
// receiver rule that depends on both.

// The single most important property: no representable angle encodes to 0.
// If this ever fails, "facing east" and "not sent" become the same bytes and the
// field inherits exactly the ambiguity it was designed to avoid.
func TestNoRealAngleEncodesToNotSent(t *testing.T) {
	// Sweep the whole turn far more finely than the encoding's own resolution,
	// plus the boundaries that rounding is most likely to break.
	for i := 0; i <= 200000; i++ {
		angle := float32(float64(i) / 200000 * 2 * math.Pi)
		if got := FacingBradFromRadians(angle); got == FacingNotSent {
			t.Fatalf("angle %v encoded to FacingNotSent — 'east' and 'not sent' have collided", angle)
		}
	}

	// Negative and multi-turn angles must wrap, not escape the range.
	for _, angle := range []float32{
		0, -0, math.Pi, -math.Pi, 2 * math.Pi, -2 * math.Pi,
		10 * math.Pi, -10 * math.Pi, 0.0001, -0.0001,
	} {
		got := FacingBradFromRadians(angle)
		if got == FacingNotSent {
			t.Errorf("angle %v encoded to FacingNotSent", angle)
		}
		if got > FacingBradSteps {
			t.Errorf("angle %v encoded to %d, above the %d-step range", angle, got, FacingBradSteps)
		}
	}
}

// Due east is 0.0 radians — the exact value that would be elided as a float.
// It must encode to 1, the first real value.
func TestDueEastIsOneNotZero(t *testing.T) {
	if got := FacingBradFromRadians(0); got != 1 {
		t.Fatalf("0 radians encoded to %d, want 1 (the +1 bias is what reserves 0)", got)
	}

	rad, ok := RadiansFromFacingBrad(1)
	if !ok {
		t.Fatal("wire value 1 must decode as a real facing")
	}
	if rad != 0 {
		t.Errorf("wire value 1 decoded to %v, want 0 radians (due east)", rad)
	}
}

// Round-trip within the encoding's own resolution.
func TestFacingRoundTripsWithinOneStep(t *testing.T) {
	// One step is 2*Pi/65536; allow one step of error plus a little float slack.
	tolerance := 2 * math.Pi / FacingBradSteps * 1.5

	for i := 0; i < 3600; i++ {
		want := float64(i) / 3600 * 2 * math.Pi

		brad := FacingBradFromRadians(float32(want))
		got, ok := RadiansFromFacingBrad(brad)
		if !ok {
			t.Fatalf("angle %v did not round-trip: decoded as not-sent", want)
		}

		diff := math.Abs(float64(got) - want)
		// Wrap-around: an angle just under a full turn may decode near 0.
		if diff > math.Pi {
			diff = 2*math.Pi - diff
		}
		if diff > tolerance {
			t.Fatalf("angle %v round-tripped to %v (diff %v > tolerance %v)", want, got, diff, tolerance)
		}
	}
}

func TestFacingWrapsRatherThanClamping(t *testing.T) {
	tests := []struct {
		name  string
		angle float32
		same  float32
	}{
		{"a full turn is east", 2 * math.Pi, 0},
		{"a negative quarter turn is three quarters", float32(-math.Pi / 2), float32(3 * math.Pi / 2)},
		{"three turns plus a bit", float32(6*math.Pi + 1), 1},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			a := FacingBradFromRadians(tt.angle)
			b := FacingBradFromRadians(tt.same)
			// Allow one step of rounding difference between the two routes.
			diff := int64(a) - int64(b)
			if diff < 0 {
				diff = -diff
			}
			if diff > 1 && diff < FacingBradSteps-1 {
				t.Errorf("%v encoded to %d but %v encoded to %d — wrapping is wrong",
					tt.angle, a, tt.same, b)
			}
		})
	}
}

// A NaN is not a direction. Shipping one would put an unrenderable value on the
// wire; absent is the honest answer.
func TestNonFiniteFacingIsNotSent(t *testing.T) {
	for _, angle := range []float32{
		float32(math.NaN()),
		float32(math.Inf(1)),
		float32(math.Inf(-1)),
	} {
		if got := FacingBradFromRadians(angle); got != FacingNotSent {
			t.Errorf("non-finite angle %v encoded to %d, want FacingNotSent", angle, got)
		}
	}
}

// The receiver rule. Zero is "no value", and the decoder must say so rather than
// handing back a plausible-looking 0 radians.
func TestZeroDecodesAsNotSentNotEast(t *testing.T) {
	rad, ok := RadiansFromFacingBrad(FacingNotSent)
	if ok {
		t.Fatal("wire value 0 must decode as 'not sent', not as a facing")
	}
	if rad != 0 {
		t.Errorf("a not-sent facing should report 0, got %v", rad)
	}
}

// An out-of-range value is refused rather than aliased onto a wrong direction: a
// wrong facing is much harder to notice than an absent one.
func TestOutOfRangeFacingIsRefused(t *testing.T) {
	for _, brad := range []uint32{FacingBradSteps + 2, 100000, math.MaxUint32} {
		if _, ok := RadiansFromFacingBrad(brad); ok {
			t.Errorf("wire value %d is out of range and must be refused", brad)
		}
	}
	// The top of the legal range must still be accepted.
	if _, ok := RadiansFromFacingBrad(FacingBradSteps); !ok {
		t.Errorf("wire value %d is the last legal value and must decode", FacingBradSteps)
	}
}

// Idle must not be zero, or "standing still" and "no action reported" collide.
func TestActionIdleIsNotZero(t *testing.T) {
	if ActionUnspecified != 0 {
		t.Fatalf("ActionUnspecified must be 0 (proto3 elides it), got %d", ActionUnspecified)
	}
	if ActionIdle == 0 {
		t.Fatal("ActionIdle must not be 0, or 'idle' and 'not sent' become the same bytes")
	}
	if ActionIdle != 1 {
		t.Errorf("ActionIdle = %d, want 1 (frozen wire value)", ActionIdle)
	}

	// Frozen numbering — these are on the wire and must never be renumbered.
	for _, tt := range []struct {
		action EntityAction
		want   uint32
	}{
		{ActionUnspecified, 0},
		{ActionIdle, 1},
		{ActionMoving, 2},
		{ActionAttacking, 3},
		{ActionDead, 4},
	} {
		if uint32(tt.action) != tt.want {
			t.Errorf("%v = %d, want %d — wire values are frozen", tt.action, uint32(tt.action), tt.want)
		}
	}
}

// An action this build does not know belongs to a newer peer. Printing it as
// "idle" would turn "I do not know" into a confident wrong answer.
func TestUnknownActionIsNotRenderedAsAKnownOne(t *testing.T) {
	got := EntityAction(99).String()
	if !strings.Contains(got, "99") {
		t.Errorf("unknown action rendered as %q, want the numeric value preserved", got)
	}
}

// Both fields must survive both encodings, and — critically — a zero must stay a
// zero rather than being invented into a default anywhere in the pipeline.
func TestFacingAndActionRoundTripBothEncodings(t *testing.T) {
	for _, enc := range bothEncodings {
		t.Run(enc.String(), func(t *testing.T) {
			want := SnapshotMessage{
				Tick: 7,
				Full: true,
				Entities: []EntitySnapshot{{
					ID:         "p1",
					Type:       "player",
					X:          1.5,
					Y:          -2.25,
					HP:         90,
					MaxHP:      100,
					Speed:      5,
					FacingBrad: FacingBradFromRadians(math.Pi / 2),
					Action:     ActionAttacking,
				}},
			}

			var got SnapshotMessage
			roundTrip(t, enc, MsgSnapshot, want, &got)

			e := got.Entities[0]
			if e.FacingBrad != want.Entities[0].FacingBrad {
				t.Errorf("facing_brad = %d, want %d", e.FacingBrad, want.Entities[0].FacingBrad)
			}
			if e.Action != ActionAttacking {
				t.Errorf("action = %v, want attacking", e.Action)
			}

			rad, ok := RadiansFromFacingBrad(e.FacingBrad)
			if !ok {
				t.Fatal("a facing that was sent must decode as sent")
			}
			if math.Abs(float64(rad)-math.Pi/2) > 0.001 {
				t.Errorf("facing decoded to %v, want ~%v", rad, math.Pi/2)
			}
		})
	}
}

// An entity from a server that predates these fields must arrive as "not sent"
// in both, not as east-facing and idle.
func TestAbsentFacingAndActionStayAbsent(t *testing.T) {
	for _, enc := range bothEncodings {
		t.Run(enc.String(), func(t *testing.T) {
			old := SnapshotMessage{
				Tick: 7,
				Full: true,
				Entities: []EntitySnapshot{{
					ID: "p1", Type: "player", HP: 10, MaxHP: 10, Speed: 5,
				}},
			}

			var got SnapshotMessage
			roundTrip(t, enc, MsgSnapshot, old, &got)

			e := got.Entities[0]
			if e.FacingBrad != FacingNotSent {
				t.Errorf("facing_brad = %d, want FacingNotSent", e.FacingBrad)
			}
			if e.Action != ActionUnspecified {
				t.Errorf("action = %v, want unspecified (NOT idle)", e.Action)
			}
			if _, ok := RadiansFromFacingBrad(e.FacingBrad); ok {
				t.Error("an absent facing must not decode as a real direction")
			}
		})
	}
}

// The JSON encoding must omit both when unset, so the two encodings spell
// "not sent" the same way instead of JSON inventing an explicit zero.
func TestJSONOmitsUnsetFacingAndAction(t *testing.T) {
	env, err := NewEnvelopeAs(EncodingJSON, MsgSnapshot, SnapshotMessage{
		Tick:     1,
		Entities: []EntitySnapshot{{ID: "p1", Type: "player", Speed: 1}},
	})
	if err != nil {
		t.Fatalf("NewEnvelopeAs: %v", err)
	}

	body := string(env.Payload)
	if strings.Contains(body, "facing_brad") {
		t.Errorf("unset facing must be omitted from JSON, got %s", body)
	}
	if strings.Contains(body, "action") {
		t.Errorf("unset action must be omitted from JSON, got %s", body)
	}
}
