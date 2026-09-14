package sealed

import (
	"bytes"
	"crypto/ed25519"
	"encoding/base64"
	"errors"
	"strings"
	"testing"
)

func testIdentity(t *testing.T) (ed25519.PublicKey, ed25519.PrivateKey) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(nil)
	if err != nil {
		t.Fatal(err)
	}
	return pub, priv
}

func testTranscript(t *testing.T) []byte {
	t.Helper()
	var c, s [PublicKeySize]byte
	for i := range c {
		c[i] = byte(i)
		s[i] = byte(0xA0 + i)
	}
	tr, err := Transcript("identity-test-jti", c, s)
	if err != nil {
		t.Fatal(err)
	}
	return tr
}

// The positive case exists only so the negatives below mean something: a
// verifier that rejects everything passes every negative test there is.
func TestIdentitySignatureVerifies(t *testing.T) {
	pub, priv := testIdentity(t)
	tr := testTranscript(t)

	sig, err := SignIdentity(priv, tr)
	if err != nil {
		t.Fatal(err)
	}
	if len(sig) != IdentitySignatureSize {
		t.Fatalf("signature is %d bytes, want %d", len(sig), IdentitySignatureSize)
	}
	if err := VerifyIdentity(pub, tr, sig); err != nil {
		t.Fatalf("a genuine signature did not verify: %v", err)
	}
}

// THE TEST THIS MECHANISM EXISTS FOR. Every way of being wrong must be rejected,
// and they are listed individually rather than as one "bad input" case because
// each is a different attacker: a forger, a man in the middle rewriting the
// exchange, and someone swapping in a pod whose key they do hold.
func TestIdentityVerifyRejectsEveryForgery(t *testing.T) {
	pub, priv := testIdentity(t)
	otherPub, otherPriv := testIdentity(t)
	tr := testTranscript(t)

	good, err := SignIdentity(priv, tr)
	if err != nil {
		t.Fatal(err)
	}

	// A signature made over a DIFFERENT transcript. This is the man in the
	// middle: he substitutes his own ephemeral key, which changes the transcript,
	// and can only replay the signature he read off the wire.
	var c, s [PublicKeySize]byte
	s[0] = 0xFF
	tampered, err := Transcript("identity-test-jti", c, s)
	if err != nil {
		t.Fatal(err)
	}

	// A signature by someone else's key over the RIGHT transcript. This is the
	// attacker who runs a real pod of his own: he can sign anything he likes,
	// just not as us.
	wrongSigner, err := SignIdentity(otherPriv, tr)
	if err != nil {
		t.Fatal(err)
	}

	// A signature that is genuine but whose signed input names a different
	// identity. This is why the identity key is INSIDE the signed input: without
	// it, "someone signed this exchange" would pass for "this key signed this
	// exchange".
	crossKeyInput, err := IdentityInput(tr, otherPub)
	if err != nil {
		t.Fatal(err)
	}
	crossKey := ed25519.Sign(priv, crossKeyInput)

	flipped := bytes.Clone(good)
	flipped[0] ^= 0x01
	lastFlipped := bytes.Clone(good)
	lastFlipped[len(lastFlipped)-1] ^= 0x80

	cases := []struct {
		name       string
		key        []byte
		transcript []byte
		sig        []byte
		want       error
	}{
		{"tampered transcript", pub, tampered, good, ErrIdentityNotVerified},
		{"signed by another identity", pub, tr, wrongSigner, ErrIdentityNotVerified},
		{"genuine signature naming another key", pub, tr, crossKey, ErrIdentityNotVerified},
		{"first byte flipped", pub, tr, flipped, ErrIdentityNotVerified},
		{"last byte flipped", pub, tr, lastFlipped, ErrIdentityNotVerified},
		{"all-zero signature", pub, tr, make([]byte, IdentitySignatureSize), ErrIdentityNotVerified},
		{"verified against the wrong key", otherPub, tr, good, ErrIdentityNotVerified},
		{"signature one byte short", pub, tr, good[:len(good)-1], ErrBadIdentitySignature},
		{"no signature at all", pub, tr, nil, ErrBadIdentitySignature},
		{"key one byte short", pub[:len(pub)-1], tr, good, ErrBadIdentityKey},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			err := VerifyIdentity(tc.key, tc.transcript, tc.sig)
			if !errors.Is(err, tc.want) {
				t.Fatalf("err = %v, want %v — a verifier that accepts this is "+
					"indistinguishable from one that accepts everything", err, tc.want)
			}
		})
	}
}

// The two labels must not be confusable. If IdentityInput were a bare
// concatenation, a signature over one domain could be replayed into the other.
func TestIdentityInputIsDomainSeparatedAndWrapsTheTranscriptUnCHANGED(t *testing.T) {
	pub, _ := testIdentity(t)
	tr := testTranscript(t)

	input, err := IdentityInput(tr, pub)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(string(input), IdentityLabel+"\x00") {
		t.Fatalf("input does not begin with the identity label and a NUL")
	}
	if IdentityLabel == TranscriptLabel {
		t.Fatal("the identity and transcript labels are the same; the two signed " +
			"inputs would be confusable")
	}
	// The transcript appears verbatim, which is the ADR-25 decision-3 promise:
	// DeriveKeys and the HMAC binding keep reading exactly these bytes, so
	// ADR-22's cross-implementation vectors stay valid.
	want := append(append([]byte(IdentityLabel), 0x00), tr...)
	want = append(append(want, 0x00), pub...)
	if !bytes.Equal(input, want) {
		t.Fatalf("IdentityInput is not label || 0x00 || transcript || 0x00 || key")
	}
	if !bytes.Contains(input, tr) {
		t.Fatal("the transcript was modified on its way into the signed input")
	}
}

func TestIdentityInputRejectsBadShapes(t *testing.T) {
	pub, _ := testIdentity(t)
	if _, err := IdentityInput(nil, pub); err == nil {
		t.Error("an empty transcript was accepted")
	}
	if _, err := IdentityInput(testTranscript(t), pub[:31]); !errors.Is(err, ErrBadIdentityKey) {
		t.Errorf("err = %v, want ErrBadIdentityKey", err)
	}
}

// The registry encoding is a cross-language contract with the C# game server, so
// it is pinned rather than left to whatever base64 variant a future edit picks.
func TestIdentityKeyEncodingRoundTripsAndIsStandardBase64(t *testing.T) {
	pub, _ := testIdentity(t)

	encoded := EncodeIdentityKey(pub)
	if encoded != base64.StdEncoding.EncodeToString(pub) {
		t.Fatal("EncodeIdentityKey is not standard padded base64")
	}
	if strings.ContainsAny(encoded, "-_") {
		t.Fatal("URL-safe base64: the C# side writes the standard alphabet")
	}

	back, err := DecodeIdentityKey(encoded)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(back, pub) {
		t.Fatal("round trip changed the key")
	}
}

func TestIdentityKeyDecodeTreatsEmptyAsAbsentAndGarbageAsAnError(t *testing.T) {
	// Empty is a pre-ADR-25 server, not a fault: failing here would take a whole
	// map offline for an old pod.
	got, err := DecodeIdentityKey("")
	if err != nil || got != nil {
		t.Fatalf("DecodeIdentityKey(\"\") = %v, %v; want nil, nil", got, err)
	}
	// Malformed is a fault, and laundering it into "no key" would hide whatever
	// wrote garbage into the registry.
	if _, err := DecodeIdentityKey("not base64 !!"); err == nil {
		t.Error("malformed base64 was accepted")
	}
	if _, err := DecodeIdentityKey(base64.StdEncoding.EncodeToString(make([]byte, 31))); !errors.Is(err, ErrBadIdentityKey) {
		t.Errorf("a 31-byte key was accepted")
	}
}

// Ed25519 is deterministic, which is what makes "the C# server and the Go client
// agree byte for byte" a testable claim. This pins the Go half of the vector;
// the C# half asserts the same bytes in ServerIdentityTests.
func TestIdentitySignatureIsDeterministicForAFixedKey(t *testing.T) {
	seed := make([]byte, ed25519.SeedSize)
	for i := range seed {
		seed[i] = byte(i)
	}
	priv := ed25519.NewKeyFromSeed(seed)
	tr := testTranscript(t)

	first, err := SignIdentity(priv, tr)
	if err != nil {
		t.Fatal(err)
	}
	second, err := SignIdentity(priv, tr)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(first, second) {
		t.Fatal("Ed25519 signatures differ across calls for one key and one input; " +
			"cross-implementation vectors would be impossible")
	}
	t.Logf("vector: key=%s sig=%s",
		base64.StdEncoding.EncodeToString(priv.Public().(ed25519.PublicKey)),
		base64.StdEncoding.EncodeToString(first))
}
