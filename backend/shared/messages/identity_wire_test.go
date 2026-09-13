package messages

import (
	"bytes"
	"testing"

	wirepb "github.com/duycuong/rpg-mmo/shared/proto/gen"
	"google.golang.org/protobuf/proto"
)

func key32() []byte {
	k := make([]byte, 32)
	for i := range k {
		k[i] = byte(0x40 + i)
	}
	return k
}

func sig64() []byte {
	s := make([]byte, 64)
	for i := range s {
		s[i] = byte(i)
	}
	return s
}

// Both encodings, because the gateway answers in whichever one the client spoke
// and a field plumbed into only one of them is a field that vanishes for half
// the fleet.
func TestIdentityFieldsSurviveBothEncodings(t *testing.T) {
	for _, enc := range []Encoding{EncodingProto, EncodingJSON} {
		t.Run(string(rune('A'+enc)), func(t *testing.T) {
			ew := EnterWorldResponse{
				ServerAddr: "10.0.0.1:9000", JoinToken: "tok", Transport: "tcp",
				ServerPublicKey: key32(),
			}
			env, err := NewEnvelopeAs(enc, MsgEnterWorldResp, ew)
			if err != nil {
				t.Fatal(err)
			}
			var back EnterWorldResponse
			if err := env.UnmarshalPayload(&back); err != nil {
				t.Fatal(err)
			}
			if !bytes.Equal(back.ServerPublicKey, key32()) {
				t.Fatalf("ServerPublicKey = %x, want %x", back.ServerPublicKey, key32())
			}

			hello := SealedServerHello{
				PublicKey: key32(), Binding: key32(), ServerSignature: sig64(),
			}
			env, err = NewEnvelopeAs(enc, MsgSealedServerHello, hello)
			if err != nil {
				t.Fatal(err)
			}
			var helloBack SealedServerHello
			if err := env.UnmarshalPayload(&helloBack); err != nil {
				t.Fatal(err)
			}
			if !bytes.Equal(helloBack.ServerSignature, sig64()) {
				t.Fatalf("ServerSignature = %x, want %x", helloBack.ServerSignature, sig64())
			}
			// The binding is NOT replaced by the signature. Both travel; ADR-25
			// decision 4 keeps field 2 meaning exactly what it meant, because one
			// field number with two meanings is how two versions come to disagree
			// silently about a byte.
			if !bytes.Equal(helloBack.Binding, key32()) {
				t.Fatal("the binding was lost when the signature was added")
			}
		})
	}
}

// The field NUMBERS, pinned. EnterWorldResponse field 5 is reserved forever
// (ADR-22 decision 6), so the identity key had to take a fresh one — and a
// future edit that "tidies" it back onto 5 would be read by an old peer as 32
// bytes of session key, the most expensive wrong answer this protocol can give.
func TestIdentityFieldNumbersAreTheOnesTheADRFixed(t *testing.T) {
	raw, err := proto.Marshal(&wirepb.EnterWorldResponse{ServerPublicKey: key32()})
	if err != nil {
		t.Fatal(err)
	}
	// Field 6, wire type 2 => tag byte (6<<3)|2 = 0x32, then length 32 = 0x20.
	if len(raw) < 2 || raw[0] != 0x32 || raw[1] != 0x20 {
		t.Fatalf("server_public_key is not field 6: first bytes %x", raw[:min(4, len(raw))])
	}

	raw, err = proto.Marshal(&wirepb.SealedServerHello{ServerSignature: sig64()})
	if err != nil {
		t.Fatal(err)
	}
	// Field 4, wire type 2 => (4<<3)|2 = 0x22, then length 64 = 0x40.
	if len(raw) < 2 || raw[0] != 0x22 || raw[1] != 0x40 {
		t.Fatalf("server_signature is not field 4: first bytes %x", raw[:min(4, len(raw))])
	}
}

// An old peer's bytes must still parse, which is what makes the
// gateway-and-server-first migration order safe (ADR-25 §5).
func TestAPeerThatSendsNeitherFieldStillParses(t *testing.T) {
	raw, err := proto.Marshal(&wirepb.EnterWorldResponse{
		ServerAddr: "10.0.0.1:9000", JoinToken: "tok",
	})
	if err != nil {
		t.Fatal(err)
	}
	var pb wirepb.EnterWorldResponse
	if err := proto.Unmarshal(raw, &pb); err != nil {
		t.Fatal(err)
	}
	if len(pb.ServerPublicKey) != 0 {
		t.Fatal("a key appeared from nowhere")
	}
	if pb.ServerAddr != "10.0.0.1:9000" {
		t.Fatal("the old fields did not survive")
	}
}
