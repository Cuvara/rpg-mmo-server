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
	if err := UnmarshalPayload(env.Payload, &decoded); err != nil {
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
	if err := UnmarshalPayload(decoded.Payload, &decodedAuth); err != nil {
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
	if err := UnmarshalPayload(env.Payload, &decoded); err != nil {
		t.Fatalf("UnmarshalPayload() error: %v", err)
	}

	if len(decoded.Entities) != 2 {
		t.Fatalf("Entities count = %d, want 2", len(decoded.Entities))
	}
	if decoded.Entities[0].ID != "p1" {
		t.Errorf("Entity[0].ID = %q, want %q", decoded.Entities[0].ID, "p1")
	}
}
