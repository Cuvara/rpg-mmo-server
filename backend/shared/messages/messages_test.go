package messages

import (
	"bytes"
	"testing"
)

func TestEnvelopeRoundTrip(t *testing.T) {
	input := InputMessage{Tick: 42, MoveX: 1.5, MoveY: -0.5}
	env, err := NewEnvelope(MsgInput, input)
	if err != nil {
		t.Fatalf("NewEnvelope() error: %v", err)
	}
	if env.Type != MsgInput {
		t.Errorf("Type = %d, want %d", env.Type, MsgInput)
	}

	var decoded InputMessage
	if err := env.UnmarshalPayload(&decoded); err != nil {
		t.Fatalf("UnmarshalPayload() error: %v", err)
	}
	if decoded.Tick != 42 || decoded.MoveX != 1.5 || decoded.MoveY != -0.5 {
		t.Errorf("decoded = %+v, want tick=42, moveX=1.5, moveY=-0.5", decoded)
	}
}

func TestCodecRoundTrip(t *testing.T) {
	auth := AuthRequest{Token: "abc123"}
	env, err := NewEnvelope(MsgAuth, auth)
	if err != nil {
		t.Fatalf("NewEnvelope() error: %v", err)
	}

	encoded, err := Encode(env)
	if err != nil {
		t.Fatalf("Encode() error: %v", err)
	}

	decoded, err := Decode(bytes.NewReader(encoded))
	if err != nil {
		t.Fatalf("Decode() error: %v", err)
	}

	if decoded.Type != MsgAuth {
		t.Errorf("Type = %d, want %d", decoded.Type, MsgAuth)
	}

	var decodedAuth AuthRequest
	if err := decoded.UnmarshalPayload(&decodedAuth); err != nil {
		t.Fatalf("UnmarshalPayload() error: %v", err)
	}
	if decodedAuth.Token != "abc123" {
		t.Errorf("Token = %q, want %q", decodedAuth.Token, "abc123")
	}
}

func TestDecode_TooLarge(t *testing.T) {
	// Create a message claiming to be > 1MB
	buf := make([]byte, 4)
	buf[0] = 0x00
	buf[1] = 0x20 // 2MB
	buf[2] = 0x00
	buf[3] = 0x00
	_, err := Decode(bytes.NewReader(buf))
	if err == nil {
		t.Error("Decode() should reject messages > 1MB")
	}
}

func TestSnapshotMessage(t *testing.T) {
	snap := SnapshotMessage{
		Tick: 100,
		Entities: []EntitySnapshot{
			{ID: "p1", Type: "player", X: 10, Y: 20, HP: 100, MaxHP: 100},
			{ID: "mob1", Type: "mob", X: 15, Y: 22, HP: 50, MaxHP: 80},
		},
	}

	env, err := NewEnvelope(MsgSnapshot, snap)
	if err != nil {
		t.Fatalf("NewEnvelope() error: %v", err)
	}

	var decoded SnapshotMessage
	if err := env.UnmarshalPayload(&decoded); err != nil {
		t.Fatalf("UnmarshalPayload() error: %v", err)
	}

	if len(decoded.Entities) != 2 {
		t.Fatalf("Entities count = %d, want 2", len(decoded.Entities))
	}
	if decoded.Entities[0].ID != "p1" {
		t.Errorf("Entity[0].ID = %q, want %q", decoded.Entities[0].ID, "p1")
	}
}

func TestPingPongRoundTrip(t *testing.T) {
	for _, enc := range []Encoding{EncodingJSON, EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) {
			ping := PingMessage{Timestamp: 1234567890}
			env, err := NewEnvelopeAs(enc, MsgPing, ping)
			if err != nil {
				t.Fatalf("NewEnvelopeAs(Ping): %v", err)
			}
			if env.Type != MsgPing {
				t.Errorf("Type = %d, want %d", env.Type, MsgPing)
			}

			frame, err := Encode(env)
			if err != nil {
				t.Fatalf("Encode: %v", err)
			}
			decoded, err := Decode(bytes.NewReader(frame))
			if err != nil {
				t.Fatalf("Decode: %v", err)
			}

			var got PingMessage
			if err := decoded.UnmarshalPayload(&got); err != nil {
				t.Fatalf("UnmarshalPayload(Ping): %v", err)
			}
			if got.Timestamp != 1234567890 {
				t.Errorf("Timestamp = %d, want 1234567890", got.Timestamp)
			}

			// Pong
			pong := PongMessage{Timestamp: 1234567890, ServerTime: 9999999999}
			env2, err := NewEnvelopeAs(enc, MsgPong, pong)
			if err != nil {
				t.Fatalf("NewEnvelopeAs(Pong): %v", err)
			}
			frame2, err := Encode(env2)
			if err != nil {
				t.Fatalf("Encode(Pong): %v", err)
			}
			decoded2, err := Decode(bytes.NewReader(frame2))
			if err != nil {
				t.Fatalf("Decode(Pong): %v", err)
			}
			var gotPong PongMessage
			if err := decoded2.UnmarshalPayload(&gotPong); err != nil {
				t.Fatalf("UnmarshalPayload(Pong): %v", err)
			}
			if gotPong.Timestamp != 1234567890 || gotPong.ServerTime != 9999999999 {
				t.Errorf("Pong = %+v, want timestamp=1234567890, server_time=9999999999", gotPong)
			}
		})
	}
}

func TestKickRoundTrip(t *testing.T) {
	for _, enc := range []Encoding{EncodingJSON, EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) {
			kick := KickMessage{Reason: "duplicate_login"}
			env, err := NewEnvelopeAs(enc, MsgKick, kick)
			if err != nil {
				t.Fatalf("NewEnvelopeAs(Kick): %v", err)
			}

			frame, err := Encode(env)
			if err != nil {
				t.Fatalf("Encode: %v", err)
			}
			decoded, err := Decode(bytes.NewReader(frame))
			if err != nil {
				t.Fatalf("Decode: %v", err)
			}

			var got KickMessage
			if err := decoded.UnmarshalPayload(&got); err != nil {
				t.Fatalf("UnmarshalPayload(Kick): %v", err)
			}
			if got.Reason != "duplicate_login" {
				t.Errorf("Reason = %q, want %q", got.Reason, "duplicate_login")
			}
		})
	}
}

func TestMsgTypeConstants(t *testing.T) {
	// Verify the explicit constants have the expected values.
	if MsgPing != 11 {
		t.Errorf("MsgPing = %d, want 11", MsgPing)
	}
	if MsgPong != 12 {
		t.Errorf("MsgPong = %d, want 12", MsgPong)
	}
	if MsgKick != 15 {
		t.Errorf("MsgKick = %d, want 15", MsgKick)
	}
}
