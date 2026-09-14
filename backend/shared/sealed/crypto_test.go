package sealed

import (
	"bytes"
	"encoding/hex"
	"errors"
	"testing"
)

func mustHex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatalf("bad hex in test: %v", err)
	}
	return b
}

// RFC 8439 §2.8.2 — the published AEAD_CHACHA20_POLY1305 test vector.
//
// A round-trip proves an implementation agrees with itself, which a subtly
// wrong one also does, silently. Only a published vector proves it agrees with
// everyone else — and this suite exists because that distinction has already
// cost this project a wrong answer at least once.
func TestChaCha20Poly1305_RFC8439Vector(t *testing.T) {
	key := mustHex(t, "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f")
	nonce := mustHex(t, "070000004041424344454647")
	aad := mustHex(t, "50515253c0c1c2c3c4c5c6c7")
	plaintext := []byte("Ladies and Gentlemen of the class of '99: If I could offer you " +
		"only one tip for the future, sunscreen would be it.")

	wantCiphertext := mustHex(t,
		"d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6"+
			"3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36"+
			"92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc"+
			"3ff4def08e4b7a9de576d26586cec64b6116")
	wantTag := mustHex(t, "1ae10b594f09e26a7e902ecbd0600691")

	aead, err := NewAEAD(key)
	if err != nil {
		t.Fatal(err)
	}

	got := aead.Seal(nil, nonce, plaintext, aad)
	want := append(append([]byte{}, wantCiphertext...), wantTag...)
	if !bytes.Equal(got, want) {
		t.Fatalf("RFC 8439 §2.8.2 ciphertext+tag mismatch\n got: %x\nwant: %x", got, want)
	}

	back, err := aead.Open(nil, nonce, got, aad)
	if err != nil {
		t.Fatalf("open of our own sealed vector failed: %v", err)
	}
	if !bytes.Equal(back, plaintext) {
		t.Fatal("round trip of the RFC vector did not return the plaintext")
	}
}

// Tampering must be detected — with the ciphertext, with the tag, and with the
// additional data. The AAD case is the one an implementation can get wrong
// while every round-trip still passes, because nothing tests AAD unless you
// tamper with it specifically.
func TestChaCha20Poly1305_RejectsTampering(t *testing.T) {
	key := make([]byte, KeySize)
	nonce := make([]byte, NonceSize)
	aead, err := NewAEAD(key)
	if err != nil {
		t.Fatal(err)
	}
	aad := []byte("header")
	sealed := aead.Seal(nil, nonce, []byte("payload"), aad)

	cases := map[string]func() ([]byte, []byte){
		"ciphertext bit flipped": func() ([]byte, []byte) {
			c := append([]byte(nil), sealed...)
			c[0] ^= 1
			return c, aad
		},
		"tag bit flipped": func() ([]byte, []byte) {
			c := append([]byte(nil), sealed...)
			c[len(c)-1] ^= 1
			return c, aad
		},
		"additional data changed": func() ([]byte, []byte) {
			return append([]byte(nil), sealed...), []byte("heade!")
		},
		"truncated": func() ([]byte, []byte) {
			return sealed[:len(sealed)-1], aad
		},
	}

	for name, mutate := range cases {
		ciphertext, additional := mutate()
		if _, err := aead.Open(nil, nonce, ciphertext, additional); err == nil {
			t.Errorf("%s: Open accepted a tampered frame", name)
		}
	}
}

// RFC 7748 §6.1 — the published X25519 Diffie-Hellman vector.
func TestX25519_RFC7748Vector(t *testing.T) {
	// Alice and Bob's published private keys, and the shared secret they must
	// both reach.
	alicePriv := mustHex(t, "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")
	bobPriv := mustHex(t, "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb")
	alicePub := mustHex(t, "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")
	bobPub := mustHex(t, "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f")
	wantShared := mustHex(t, "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742")

	var alice, bob KeyPair
	copy(alice.private[:], alicePriv)
	copy(bob.private[:], bobPriv)

	// The public keys the implementation derives must match the published ones.
	derivedAlice, err := (&KeyPair{private: alice.private}).derivePublicForTest()
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(derivedAlice, alicePub) {
		t.Errorf("Alice public\n got: %x\nwant: %x", derivedAlice, alicePub)
	}

	copy(alice.Public[:], alicePub)
	copy(bob.Public[:], bobPub)

	fromAlice, err := alice.SharedSecret(bob.Public)
	if err != nil {
		t.Fatal(err)
	}
	fromBob, err := bob.SharedSecret(alice.Public)
	if err != nil {
		t.Fatal(err)
	}

	if !bytes.Equal(fromAlice, wantShared) {
		t.Errorf("shared secret (Alice)\n got: %x\nwant: %x", fromAlice, wantShared)
	}
	if !bytes.Equal(fromBob, wantShared) {
		t.Errorf("shared secret (Bob)\n got: %x\nwant: %x", fromBob, wantShared)
	}
}

// A low-order peer key forces a shared secret the attacker knows and both sides
// agree on — a complete break that looks like a successful handshake. It must
// be refused, not merely noticed.
func TestX25519_RefusesLowOrderPoints(t *testing.T) {
	kp, err := GenerateKeyPair()
	if err != nil {
		t.Fatal(err)
	}
	var zero [PublicKeySize]byte
	if _, err := kp.SharedSecret(zero); !errors.Is(err, ErrLowOrderPoint) {
		t.Errorf("all-zero peer key: err = %v, want ErrLowOrderPoint", err)
	}
}

func TestX25519_FreshKeysAgree(t *testing.T) {
	a, err := GenerateKeyPair()
	if err != nil {
		t.Fatal(err)
	}
	b, err := GenerateKeyPair()
	if err != nil {
		t.Fatal(err)
	}
	if a.Public == b.Public {
		t.Fatal("two generated key pairs share a public key; the RNG is not working")
	}

	sa, err := a.SharedSecret(b.Public)
	if err != nil {
		t.Fatal(err)
	}
	sb, err := b.SharedSecret(a.Public)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(sa, sb) {
		t.Fatal("the two ends derived different shared secrets")
	}
}

// RFC 5869 A.1 — the published HKDF-SHA256 vector, checked through the same
// expand() the key schedule uses.
func TestHKDF_RFC5869Vector(t *testing.T) {
	ikm := mustHex(t, "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b")
	salt := mustHex(t, "000102030405060708090a0b0c")
	info := mustHex(t, "f0f1f2f3f4f5f6f7f8f9")
	want := mustHex(t,
		"3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf"+
			"34007208d5b887185865")

	got := make([]byte, len(want))
	if err := expand(ikm, salt, string(info), got); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(got, want) {
		t.Errorf("RFC 5869 A.1\n got: %x\nwant: %x", got, want)
	}
}

// --- key schedule ---

// The two directions must get DIFFERENT keys. This is what makes the bare
// counter nonce safe: the client's sequence 7 and the server's sequence 7 are
// encrypted under different keys, so the (key, nonce) pair never repeats.
// If this test ever fails, the nonce scheme is broken, not just this function.
func TestDirectionKeysDiffer(t *testing.T) {
	keys, err := DeriveKeys([]byte("shared secret"), []byte("transcript"))
	if err != nil {
		t.Fatal(err)
	}
	if keys.ClientToServer == keys.ServerToClient {
		t.Fatal("both directions derived the same key; the counter nonce is now unsafe")
	}
	var zero [KeySize]byte
	if keys.ClientToServer == zero || keys.ServerToClient == zero {
		t.Fatal("a derived key is all zeroes")
	}
}

// Keys are bound to the transcript, so two exchanges that agreed on a secret
// but disagreed about anything else simply fail rather than proceeding on a
// half-agreed view of what was negotiated.
func TestKeysAreBoundToTheTranscript(t *testing.T) {
	a, err := DeriveKeys([]byte("secret"), []byte("transcript one"))
	if err != nil {
		t.Fatal(err)
	}
	b, err := DeriveKeys([]byte("secret"), []byte("transcript two"))
	if err != nil {
		t.Fatal(err)
	}
	if a.ClientToServer == b.ClientToServer {
		t.Fatal("the derived key did not change with the transcript")
	}
}

// --- binding ---

func TestBindingVerifies(t *testing.T) {
	signer, err := NewTranscriptSigner("join-secret", "jti-1")
	if err != nil {
		t.Fatal(err)
	}
	var cpub, spub [PublicKeySize]byte
	cpub[0], spub[0] = 1, 2
	transcript, err := Transcript("jti-1", cpub, spub)
	if err != nil {
		t.Fatal(err)
	}

	binding, err := signer.Sign(transcript)
	if err != nil {
		t.Fatal(err)
	}
	if err := signer.Verify(transcript, binding); err != nil {
		t.Fatalf("a freshly signed transcript did not verify: %v", err)
	}

	// The MITM case: an attacker substitutes its own ephemeral key and replays
	// the binding it read. The transcript changes, so the binding must fail.
	var attacker [PublicKeySize]byte
	attacker[0] = 0xAA
	substituted, err := Transcript("jti-1", attacker, spub)
	if err != nil {
		t.Fatal(err)
	}
	if err := signer.Verify(substituted, binding); !errors.Is(err, ErrBadBindingTag) {
		t.Fatal("a replayed binding verified against a substituted ephemeral key; " +
			"this is exactly the man-in-the-middle the binding exists to stop")
	}
}

// An attacker who does not hold JOIN_TOKEN_SECRET cannot produce a binding,
// even knowing the jti and both public keys — all of which travel in the clear.
func TestBindingRequiresTheSecret(t *testing.T) {
	real, err := NewTranscriptSigner("the-real-secret", "jti-1")
	if err != nil {
		t.Fatal(err)
	}
	attacker, err := NewTranscriptSigner("a-guess", "jti-1")
	if err != nil {
		t.Fatal(err)
	}
	var cpub, spub [PublicKeySize]byte
	transcript, err := Transcript("jti-1", cpub, spub)
	if err != nil {
		t.Fatal(err)
	}

	forged, err := attacker.Sign(transcript)
	if err != nil {
		t.Fatal(err)
	}
	if err := real.Verify(transcript, forged); !errors.Is(err, ErrBadBindingTag) {
		t.Fatal("a binding forged without the join-token secret verified")
	}
}

// The binding key is per session: the same transcript under a different jti
// must not produce the same tag.
func TestBindingIsPerSession(t *testing.T) {
	one, _ := NewTranscriptSigner("secret", "jti-1")
	two, _ := NewTranscriptSigner("secret", "jti-2")
	transcript := []byte("same bytes")

	a, err := one.Sign(transcript)
	if err != nil {
		t.Fatal(err)
	}
	b, err := two.Sign(transcript)
	if err != nil {
		t.Fatal(err)
	}
	if a == b {
		t.Fatal("two sessions produced the same binding; the key is not per-session")
	}
}
