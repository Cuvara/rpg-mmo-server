package sealed

import (
	"bytes"
	"encoding/hex"
	"testing"
)

// Cross-implementation vector: one complete handshake and one sealed frame,
// fixed end to end. The identical constants are asserted by
// GameServer.Tests/Net/SealedInteropTests.cs.
//
// # Why a shared vector and not two round-trips
//
// Each side round-tripping against itself proves only that it agrees with
// itself, which a subtly wrong implementation also does — silently. These
// values pin the bytes BETWEEN the implementations: the derived keys, the
// transcript, the binding tag and a complete sealed frame. If Go and C# ever
// disagree about any of them the failure in production is not an error, it is a
// handshake that never completes and a session that never forms, with nothing
// anywhere naming the cause.
//
// Every value here is derived, not chosen: the two private keys are RFC 7748
// §6.1's, and everything else follows from them and the fixed jti.
const (
	interopClientPrivate = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"
	interopServerPrivate = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"
	interopJTI           = "interop-jti-0001"
	interopJoinSecret    = "interop-join-secret"
	interopPlaintext     = "interop payload"
	interopSequence      = 7

	interopClientPublic = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"
	interopServerPublic = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"
	interopShared       = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"
	interopKeyC2S       = "9b5cb56cd4dcc26b2cd3a89c34a79feddeac1bee943566cf7bc24e80d76d221e"
	interopKeyS2C       = "54e46a0de4f5e86296813a4dc8e62829cde095d88db19195d061c4611623e31b"
	interopBinding      = "f0788e11400bd7a65954cef957c685f3ff1afebb052964e8b0dc9ad7f3bef2bc"
	interopFrame        = "c1010000000000000007f599297035b016c0f6ecef9fe5c4d1879637da13c7c3" +
		"9676f6f435aed71bd0"
)

func interopKeyPair(t *testing.T, privateHex string) *KeyPair {
	t.Helper()
	kp := &KeyPair{}
	priv, err := hex.DecodeString(privateHex)
	if err != nil {
		t.Fatal(err)
	}
	copy(kp.private[:], priv)
	pub, err := kp.derivePublicForTest()
	if err != nil {
		t.Fatal(err)
	}
	copy(kp.Public[:], pub)
	return kp
}

// The whole handshake, value by value, so a divergence names the step that
// diverged instead of only the end result.
func TestInteropHandshakeVector(t *testing.T) {
	client := interopKeyPair(t, interopClientPrivate)
	server := interopKeyPair(t, interopServerPrivate)

	if got := hex.EncodeToString(client.Public[:]); got != interopClientPublic {
		t.Errorf("client public\n got: %s\nwant: %s", got, interopClientPublic)
	}
	if got := hex.EncodeToString(server.Public[:]); got != interopServerPublic {
		t.Errorf("server public\n got: %s\nwant: %s", got, interopServerPublic)
	}

	shared, err := client.SharedSecret(server.Public)
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(shared); got != interopShared {
		t.Errorf("shared secret\n got: %s\nwant: %s", got, interopShared)
	}

	transcript, err := Transcript(interopJTI, client.Public, server.Public)
	if err != nil {
		t.Fatal(err)
	}

	keys, err := DeriveKeys(shared, transcript)
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(keys.ClientToServer[:]); got != interopKeyC2S {
		t.Errorf("c2s key\n got: %s\nwant: %s", got, interopKeyC2S)
	}
	if got := hex.EncodeToString(keys.ServerToClient[:]); got != interopKeyS2C {
		t.Errorf("s2c key\n got: %s\nwant: %s", got, interopKeyS2C)
	}

	signer, err := NewTranscriptSigner(interopJoinSecret, interopJTI)
	if err != nil {
		t.Fatal(err)
	}
	binding, err := signer.Sign(transcript)
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(binding[:]); got != interopBinding {
		t.Errorf("binding\n got: %s\nwant: %s", got, interopBinding)
	}

	// And the server, deriving from its own side, must reach the same secret —
	// which is the property the whole exchange exists for.
	fromServer, err := server.SharedSecret(client.Public)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(fromServer, shared) {
		t.Fatal("the two ends derived different shared secrets")
	}
}

// GO SEALS: the frame this produces is the exact byte string the C# test opens.
func TestInteropFrameSealedByGo(t *testing.T) {
	keys := interopKeys(t)

	aead, err := NewAEAD(keys.ClientToServer[:])
	if err != nil {
		t.Fatal(err)
	}
	header := AppendHeader(nil, Header{Version: Version, Sequence: interopSequence})
	nonce := Nonce(interopSequence)
	frame := aead.Seal(append([]byte{}, header...), nonce[:], []byte(interopPlaintext), header)

	if got := hex.EncodeToString(frame); got != interopFrame {
		t.Fatalf("sealed frame\n got: %s\nwant: %s\n"+
			"Both implementations must move together or no session forms.", got, interopFrame)
	}
}

// GO OPENS what C# sealed. Because the construction is deterministic in
// (key, nonce, aad, plaintext), the frame C# produces for these inputs is this
// same byte string — so opening it here is the reverse direction of the check
// above, and a divergence in either implementation fails one of the two.
func TestInteropFrameOpenedByGo(t *testing.T) {
	keys := interopKeys(t)

	frame, err := hex.DecodeString(interopFrame)
	if err != nil {
		t.Fatal(err)
	}
	aead, err := NewAEAD(keys.ClientToServer[:])
	if err != nil {
		t.Fatal(err)
	}
	validator := NewStrictMonotonic()
	session, err := NewSession(aead, validator, TransportGuarantees{OrderedDelivery: true})
	if err != nil {
		t.Fatal(err)
	}

	plaintext, err := session.Open(frame)
	if err != nil {
		t.Fatalf("Go could not open the interop frame: %v", err)
	}
	if string(plaintext) != interopPlaintext {
		t.Fatalf("plaintext = %q, want %q", plaintext, interopPlaintext)
	}
	if validator.Highest() != interopSequence {
		t.Errorf("sequence = %d, want %d", validator.Highest(), interopSequence)
	}
}

func interopKeys(t *testing.T) DirectionKeys {
	t.Helper()
	client := interopKeyPair(t, interopClientPrivate)
	server := interopKeyPair(t, interopServerPrivate)

	shared, err := client.SharedSecret(server.Public)
	if err != nil {
		t.Fatal(err)
	}
	transcript, err := Transcript(interopJTI, client.Public, server.Public)
	if err != nil {
		t.Fatal(err)
	}
	keys, err := DeriveKeys(shared, transcript)
	if err != nil {
		t.Fatal(err)
	}
	return keys
}
