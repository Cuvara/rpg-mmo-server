package messages

import (
	"bytes"
	"encoding/json"
	"errors"
	"math"
	"testing"
)

// bothEncodings is the table every codec test runs against, so a behaviour that
// only holds for one encoding cannot pass unnoticed.
var bothEncodings = []Encoding{EncodingJSON, EncodingProto}

// TestEncodingPrefixesCannotCollide pins the single assumption the whole
// dual-stack migration rests on.
//
// It sweeps EVERY type value the wire can carry (1..255), not just the currently
// declared constants, so adding a message type cannot silently escape the check
// and neither can renumbering the existing ones.
func TestEncodingPrefixesCannotCollide(t *testing.T) {
	for n := 1; n <= 255; n++ {
		msgType := MsgType(n)

		jsonEnv, err := NewEnvelopeAs(EncodingJSON, msgType, struct{}{})
		if err != nil {
			t.Fatalf("json envelope type %d: %v", n, err)
		}
		protoEnv, err := NewEnvelopeAs(EncodingProto, msgType, struct{}{})
		if err != nil {
			t.Fatalf("proto envelope type %d: %v", n, err)
		}

		jsonBody, err := EncodeBody(jsonEnv)
		if err != nil {
			t.Fatalf("encode json body: %v", err)
		}
		protoBody, err := EncodeBody(protoEnv)
		if err != nil {
			t.Fatalf("encode proto body: %v", err)
		}

		// No leading whitespace, ever: an encoder that pretty-printed or indented
		// would move the '{' off byte 0 and every frame would sniff as Protobuf.
		if jsonBody[0] != '{' {
			t.Fatalf("type %d: json body starts with %#x, want '{'", n, jsonBody[0])
		}
		if protoBody[0] != 0x08 {
			t.Fatalf("type %d: proto body starts with %#x, want 0x08 (field 1 varint tag)", n, protoBody[0])
		}
		if got := SniffEncoding(jsonBody); got != EncodingJSON {
			t.Fatalf("type %d: sniffed json body as %v", n, got)
		}
		if got := SniffEncoding(protoBody); got != EncodingProto {
			t.Fatalf("type %d: sniffed proto body as %v", n, got)
		}

		// And each must round-trip back to the type it went in as.
		for _, body := range [][]byte{jsonBody, protoBody} {
			got, err := DecodeBody(body)
			if err != nil {
				t.Fatalf("type %d: DecodeBody: %v", n, err)
			}
			if got.Type != msgType {
				t.Fatalf("type %d round-tripped as %d", n, got.Type)
			}
		}
	}
}

// TestMsgTypeZeroIsRejected guards the invariant at construction.
//
// proto3 elides a zero field 1, so a Type 0 envelope would encode WITHOUT the
// 0x08 prefix and be sniffed as the wrong encoding by the peer. The declared
// constants start at iota + 1, but nothing forces that to stay true, so the
// constraint is enforced rather than assumed.
func TestMsgTypeZeroIsRejected(t *testing.T) {
	for _, enc := range bothEncodings {
		if _, err := NewEnvelopeAs(enc, 0, struct{}{}); !errors.Is(err, ErrInvalidMsgType) {
			t.Errorf("%v: NewEnvelopeAs(type 0) err = %v, want ErrInvalidMsgType", enc, err)
		}
	}
	if _, err := NewEnvelope(0, struct{}{}); !errors.Is(err, ErrInvalidMsgType) {
		t.Errorf("NewEnvelope(type 0) err = %v, want ErrInvalidMsgType", err)
	}
}

// TestDecodeFailsClosedOnGarbage is the reason ErrInvalidMsgType exists.
//
// Sniffing narrows a body to one of two decoders; it cannot tell a real Protobuf
// envelope from arbitrary bytes that happen to BE valid Protobuf. A body
// beginning 0x12 is field 2 (payload), length-delimited — it parses cleanly and
// leaves Type at 0. Before the Type 0 guard that produced a typeless envelope
// and no error at all, i.e. a silent half-parse routed onwards as an unknown
// message. Every one of these must now be an error.
func TestDecodeFailsClosedOnGarbage(t *testing.T) {
	garbage := map[string][]byte{
		"payload field only (parses as valid proto!)": {0x12, 0x02, 0x41, 0x42},
		"zero field number":                           {0x00, 0x01},
		"ascii text":                                  []byte("not json!!"),
		"high bytes":                                  {0xFF, 0xFF, 0xFF},
		"truncated varint":                            {0x08},
		"json-ish but not an envelope":                []byte(`{"nope":1}`),
		"empty":                                       {},
	}
	for name, body := range garbage {
		if _, err := DecodeBody(body); err == nil {
			t.Errorf("DecodeBody(%s) succeeded; must fail closed", name)
		}
	}
}

func TestSnapshotRoundTripBothEncodings(t *testing.T) {
	want := SnapshotMessage{
		Tick:    math.MaxUint32 + 7, // exercises the 64-bit path
		AckTick: 12345,
		Full:    true,
		Entities: []EntitySnapshot{
			{ID: "player-1", Type: "player", X: -1.5, Y: 2.25, HP: 90, MaxHP: 100},
			{ID: "mob-2", Type: "mob", X: 0, Y: 0, HP: 0, MaxHP: 1},
		},
		Removed: []string{"gone-1", "gone-2"},
	}

	for _, enc := range bothEncodings {
		t.Run(enc.String(), func(t *testing.T) {
			env, err := NewEnvelopeAs(enc, MsgSnapshot, want)
			if err != nil {
				t.Fatalf("NewEnvelopeAs: %v", err)
			}

			frame, err := Encode(env)
			if err != nil {
				t.Fatalf("Encode: %v", err)
			}
			got, err := Decode(bytes.NewReader(frame))
			if err != nil {
				t.Fatalf("Decode: %v", err)
			}
			if got.Enc != enc {
				t.Errorf("decoded encoding = %v, want %v", got.Enc, enc)
			}
			if got.Type != MsgSnapshot {
				t.Errorf("decoded type = %d, want %d", got.Type, MsgSnapshot)
			}

			var back SnapshotMessage
			if err := got.UnmarshalPayload(&back); err != nil {
				t.Fatalf("UnmarshalPayload: %v", err)
			}
			assertSnapshotEqual(t, back, want)
		})
	}
}

// TestBothEncodingsAgree is the local half of the cross-language agreement the
// integration suite checks against the real C# server: whatever the encoding, a
// message must reconstruct identically.
func TestBothEncodingsAgree(t *testing.T) {
	want := SnapshotMessage{
		Tick: 42, AckTick: 41, Full: false,
		// Speed non-zero: a zero would round-trip identically through a codec that
		// dropped the field entirely, so this fixture would pass vacuously.
		Entities: []EntitySnapshot{{ID: "e1", Type: "player", X: 3.5, Y: -4.25, HP: 7, MaxHP: 8, Speed: 6.75}},
		Removed:  []string{"e9"},
	}

	decoded := make(map[Encoding]SnapshotMessage)
	for _, enc := range bothEncodings {
		env, err := NewEnvelopeAs(enc, MsgSnapshot, want)
		if err != nil {
			t.Fatalf("%v: %v", enc, err)
		}
		var got SnapshotMessage
		if err := env.UnmarshalPayload(&got); err != nil {
			t.Fatalf("%v: %v", enc, err)
		}
		decoded[enc] = got
	}
	assertSnapshotEqual(t, decoded[EncodingJSON], decoded[EncodingProto])
}

// TestJSONWireShapeUnchanged pins the legacy bytes. A pre-Protobuf client parses
// these, so any drift here is a silent compatibility break.
func TestJSONWireShapeUnchanged(t *testing.T) {
	env, err := NewEnvelope(MsgSnapshot, SnapshotMessage{
		Tick:     42,
		AckTick:  41,
		Full:     true,
		Entities: []EntitySnapshot{{ID: "p1", Type: "player", X: 10.5, Y: 20.25, HP: 90, MaxHP: 100, Speed: 5.5}},
		Removed:  []string{"gone"},
	})
	if err != nil {
		t.Fatalf("NewEnvelope: %v", err)
	}

	const wantPayload = `{"tick":42,"ack_tick":41,"full":true,` +
		// `speed` added 2026-08-14 (wire.proto field 9, rpg-mmo-server#91). It is
		// written unconditionally, like every other entity value field in this
		// struct — no `omitempty` — so the shape gains a key for every entity.
		// Additive and safe for a legacy reader: both codecs skip unknown keys.
		`"entities":[{"id":"p1","type":"player","x":10.5,"y":20.25,"hp":90,"max_hp":100,"speed":5.5}],` +
		`"removed":["gone"]}`
	if string(env.Payload) != wantPayload {
		t.Errorf("payload JSON drifted:\n got: %s\nwant: %s", env.Payload, wantPayload)
	}

	body, err := EncodeBody(env)
	if err != nil {
		t.Fatalf("EncodeBody: %v", err)
	}
	if want := `{"type":8,"payload":` + wantPayload + `}`; string(body) != want {
		t.Errorf("envelope JSON drifted:\n got: %s\nwant: %s", body, want)
	}
}

// TestEnvelopeEncIsNotSerialized guards against Enc leaking into the JSON, which
// would break every existing peer.
func TestEnvelopeEncIsNotSerialized(t *testing.T) {
	env, err := NewEnvelopeAs(EncodingJSON, MsgAuth, AuthRequest{Token: "t"})
	if err != nil {
		t.Fatalf("NewEnvelopeAs: %v", err)
	}
	raw, err := json.Marshal(env)
	if err != nil {
		t.Fatalf("Marshal: %v", err)
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(raw, &fields); err != nil {
		t.Fatalf("Unmarshal: %v", err)
	}
	if len(fields) != 2 {
		t.Errorf("envelope JSON has %d fields (%v), want exactly type+payload", len(fields), fields)
	}
	for _, k := range []string{"type", "payload"} {
		if _, ok := fields[k]; !ok {
			t.Errorf("envelope JSON missing %q", k)
		}
	}
}

// TestProtoIsSmallerThanJSON asserts the bandwidth claim rather than assuming it.
//
// The bar is 40%, not 80%, and that is a real limit rather than a soft
// assertion: with a realistic 15-character entity ID the ID string alone is
// ~15 of the ~40 bytes a protobuf entity occupies, and a string costs the same
// in both encodings. Protobuf removes the field names, the punctuation and the
// decimal float formatting; it cannot remove the identifiers. Interning entity
// IDs is what would move this number again, and that is deliberately out of
// scope here (see docs/DESIGN.md).
func TestProtoIsSmallerThanJSON(t *testing.T) {
	snap := SnapshotMessage{Tick: 12345, AckTick: 12344, Full: true}
	for i := 0; i < 50; i++ {
		snap.Entities = append(snap.Entities, EntitySnapshot{
			ID: "lt-000000000042", Type: "player",
			X: float32(100 + i), Y: float32(200 + i), HP: 100, MaxHP: 100,
		})
	}

	jsonEnv, _ := NewEnvelopeAs(EncodingJSON, MsgSnapshot, snap)
	protoEnv, _ := NewEnvelopeAs(EncodingProto, MsgSnapshot, snap)
	jsonBody, _ := EncodeBody(jsonEnv)
	protoBody, _ := EncodeBody(protoEnv)

	if saving := 1 - float64(len(protoBody))/float64(len(jsonBody)); saving < 0.40 {
		t.Errorf("expected protobuf to save >= 40%%, got %.1f%% (proto=%d json=%d)",
			saving*100, len(protoBody), len(jsonBody))
	}
	t.Logf("50-entity keyframe: json=%dB proto=%dB (%.1f%% smaller)",
		len(jsonBody), len(protoBody),
		100*float64(len(jsonBody)-len(protoBody))/float64(len(jsonBody)))
}

func TestEmptyPayloadMessagesRoundTrip(t *testing.T) {
	for _, enc := range bothEncodings {
		for _, mt := range []MsgType{MsgDisconnect, MsgResync} {
			env, err := NewEnvelopeAs(enc, mt, struct{}{})
			if err != nil {
				t.Fatalf("%v/%d: NewEnvelopeAs: %v", enc, mt, err)
			}
			frame, err := Encode(env)
			if err != nil {
				t.Fatalf("%v/%d: Encode: %v", enc, mt, err)
			}
			got, err := Decode(bytes.NewReader(frame))
			if err != nil {
				t.Fatalf("%v/%d: Decode: %v", enc, mt, err)
			}
			if got.Type != mt || got.Enc != enc {
				t.Errorf("%v/%d: round-tripped as type=%d enc=%v", enc, mt, got.Type, got.Enc)
			}
		}
	}
}

func TestReplyMatchesRequestEncoding(t *testing.T) {
	for _, enc := range bothEncodings {
		req, err := NewEnvelopeAs(enc, MsgAuth, AuthRequest{Token: "tok"})
		if err != nil {
			t.Fatalf("%v: %v", enc, err)
		}
		resp, err := req.Reply(MsgAuthResp, AuthResponse{OK: true, UserID: "u1"})
		if err != nil {
			t.Fatalf("%v: Reply: %v", enc, err)
		}
		if resp.Enc != enc {
			t.Errorf("Reply to a %v request produced %v", enc, resp.Enc)
		}
		var got AuthResponse
		if err := resp.UnmarshalPayload(&got); err != nil {
			t.Fatalf("%v: UnmarshalPayload: %v", enc, err)
		}
		if !got.OK || got.UserID != "u1" {
			t.Errorf("%v: round-trip lost data: %+v", enc, got)
		}
	}
}

func TestDecodeBodyRejectsEmpty(t *testing.T) {
	if _, err := DecodeBody(nil); err == nil {
		t.Error("DecodeBody(nil) succeeded; a zero-length body is never a valid envelope")
	}
}

func TestParseEncoding(t *testing.T) {
	tests := []struct {
		in      string
		want    Encoding
		wantErr bool
	}{
		{"", EncodingJSON, false},
		{"json", EncodingJSON, false},
		{"proto", EncodingProto, false},
		{"protobuf", EncodingProto, false},
		{"xml", 0, true},
	}
	for _, tt := range tests {
		got, err := ParseEncoding(tt.in)
		if (err != nil) != tt.wantErr {
			t.Errorf("ParseEncoding(%q) err = %v, wantErr %v", tt.in, err, tt.wantErr)
			continue
		}
		if err == nil && got != tt.want {
			t.Errorf("ParseEncoding(%q) = %v, want %v", tt.in, got, tt.want)
		}
	}
}

func assertSnapshotEqual(t *testing.T, got, want SnapshotMessage) {
	t.Helper()
	if got.Tick != want.Tick || got.AckTick != want.AckTick || got.Full != want.Full {
		t.Errorf("header mismatch: got tick=%d ack=%d full=%v, want tick=%d ack=%d full=%v",
			got.Tick, got.AckTick, got.Full, want.Tick, want.AckTick, want.Full)
	}
	if len(got.Entities) != len(want.Entities) {
		t.Fatalf("entities: got %d, want %d", len(got.Entities), len(want.Entities))
	}
	for i := range want.Entities {
		if got.Entities[i] != want.Entities[i] {
			t.Errorf("entity %d: got %+v, want %+v", i, got.Entities[i], want.Entities[i])
		}
	}
	if len(got.Removed) != len(want.Removed) {
		t.Fatalf("removed: got %v, want %v", got.Removed, want.Removed)
	}
	for i := range want.Removed {
		if got.Removed[i] != want.Removed[i] {
			t.Errorf("removed %d: got %q, want %q", i, got.Removed[i], want.Removed[i])
		}
	}
}

// TestSpeedZeroMeansNotSent pins the semantic that makes the `speed` field safe to
// add to a live protocol (wire.proto field 9, rpg-mmo-server#91).
//
// proto3 elides a zero float, so a sender that predates the field and an entity that
// genuinely cannot move are indistinguishable on the wire. That ambiguity is
// deliberate and is why receivers must treat non-positive as "no value" and fall back
// to a configured default. This test documents the wire behaviour the rule rests on:
// zero survives a round trip as zero, carrying no information either way.
func TestSpeedZeroMeansNotSent(t *testing.T) {
	for _, enc := range bothEncodings {
		want := SnapshotMessage{
			Tick:     1,
			Entities: []EntitySnapshot{{ID: "e1", Type: "player", Speed: 0}},
		}

		env, err := NewEnvelopeAs(enc, MsgSnapshot, want)
		if err != nil {
			t.Fatalf("%v: NewEnvelopeAs: %v", enc, err)
		}

		var got SnapshotMessage
		if err := env.UnmarshalPayload(&got); err != nil {
			t.Fatalf("%v: UnmarshalPayload: %v", enc, err)
		}

		if got.Entities[0].Speed != 0 {
			t.Errorf("%v: speed: got %v, want 0", enc, got.Entities[0].Speed)
		}
	}
}

// TestSpeedSurvivesBothEncodings guards the field against being dropped by one codec
// and not the other, which would show up only as prediction drift on whichever
// encoding the client happened to negotiate.
func TestSpeedSurvivesBothEncodings(t *testing.T) {
	const speed = 7.25

	for _, enc := range bothEncodings {
		want := SnapshotMessage{
			Tick:     1,
			Entities: []EntitySnapshot{{ID: "e1", Type: "player", Speed: speed}},
		}

		env, err := NewEnvelopeAs(enc, MsgSnapshot, want)
		if err != nil {
			t.Fatalf("%v: NewEnvelopeAs: %v", enc, err)
		}

		var got SnapshotMessage
		if err := env.UnmarshalPayload(&got); err != nil {
			t.Fatalf("%v: UnmarshalPayload: %v", enc, err)
		}

		if got.Entities[0].Speed != speed {
			t.Errorf("%v: speed: got %v, want %v", enc, got.Entities[0].Speed, speed)
		}
	}
}
