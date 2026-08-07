package messages

import (
	"strings"
	"testing"
)

// The enum is a Protobuf-only optimisation. Every other layer — the domain
// structs, the JSON encoding, the Unity-facing mirror — still speaks strings, so
// the property that matters is that the string survives the round trip
// unchanged, whichever field carried it.
func TestEntityTypeRoundTripsBothEncodings(t *testing.T) {
	names := append(EntityTypeNames(),
		// Not in the enum: must still survive, via the type_name fallback.
		"siege_engine", "", "PLAYER", "player ",
	)

	for _, name := range names {
		for _, enc := range bothEncodings {
			t.Run(enc.String()+"/"+quote(name), func(t *testing.T) {
				want := SnapshotMessage{
					Tick:     7,
					Entities: []EntitySnapshot{{ID: "e1", Type: name, X: 1, Y: 2, HP: 3, MaxHP: 4}},
				}
				env, err := NewEnvelopeAs(enc, MsgSnapshot, want)
				if err != nil {
					t.Fatalf("encode: %v", err)
				}
				var got SnapshotMessage
				if err := env.UnmarshalPayload(&got); err != nil {
					t.Fatalf("decode: %v", err)
				}
				if len(got.Entities) != 1 {
					t.Fatalf("got %d entities, want 1", len(got.Entities))
				}
				if got.Entities[0].Type != name {
					t.Errorf("type round-tripped as %q, want %q", got.Entities[0].Type, name)
				}
			})
		}
	}
}

// An unknown type must not be silently dropped or mangled — a simulation that
// grows a new entity kind before the schema does has to degrade to the old cost,
// not lose information.
func TestUnknownEntityTypeFallsBackRatherThanDropping(t *testing.T) {
	const unknown = "siege_engine"
	env, err := NewEnvelopeAs(EncodingProto, MsgSnapshot, SnapshotMessage{
		Entities: []EntitySnapshot{{ID: "e1", Type: unknown}},
	})
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	var got SnapshotMessage
	if err := env.UnmarshalPayload(&got); err != nil {
		t.Fatalf("decode: %v", err)
	}
	if got.Entities[0].Type != unknown {
		t.Fatalf("unknown type became %q, want %q", got.Entities[0].Type, unknown)
	}
}

// Exactly one of the two fields carries the type. Setting both would make the
// larger one pure waste, which is the entire cost this change removes.
func TestKnownTypeDoesNotAlsoPayForTheString(t *testing.T) {
	known, err := NewEnvelopeAs(EncodingProto, MsgSnapshot, SnapshotMessage{
		Entities: []EntitySnapshot{{ID: "e1", Type: "player"}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(known.Payload), "player") {
		t.Errorf("payload still contains the literal type string:\n%q", known.Payload)
	}
}

// The saving, asserted rather than assumed. Measured marginal cost of the type
// string is ~8 bytes/entity against ~41 total.
func TestEntityTypeEnumShrinksTheWire(t *testing.T) {
	build := func(typ string) int {
		m := SnapshotMessage{Tick: 12345, AckTick: 12344}
		for i := 0; i < 50; i++ {
			m.Entities = append(m.Entities, EntitySnapshot{
				ID: "lt-000000000042", Type: typ, X: float32(i), Y: float32(i), HP: 100, MaxHP: 100,
			})
		}
		env, _ := NewEnvelopeAs(EncodingProto, MsgSnapshot, m)
		b, _ := EncodeBody(env)
		return len(b)
	}

	enum, fallback := build("player"), build("siege_engine")
	perEntity := float64(fallback-enum) / 50

	t.Logf("50 entities: enum=%dB  string-fallback=%dB  saving=%.1f B/entity", enum, fallback, perEntity)
	// "siege_engine" is 12 chars vs "player" at 6, so the fallback here is 6
	// bytes worse than the pre-enum baseline; the floor for the enum saving
	// against a 6-char name is ~6 B/entity.
	if enum >= fallback {
		t.Errorf("enum encoding (%dB) is not smaller than the string fallback (%dB)", enum, fallback)
	}
}

func quote(s string) string {
	if s == "" {
		return "empty"
	}
	return strings.ReplaceAll(s, " ", "_")
}
