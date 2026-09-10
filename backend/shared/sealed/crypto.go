package sealed

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"fmt"
	"io"

	"golang.org/x/crypto/chacha20poly1305"
	"golang.org/x/crypto/curve25519"
	"golang.org/x/crypto/hkdf"
)

// The primitives, per ADR-22: ChaCha20-Poly1305 (RFC 8439), X25519 (RFC 7748)
// and HKDF-SHA256 (RFC 5869).
//
// Nothing here implements a cipher, a MAC or a curve. Every primitive comes
// from golang.org/x/crypto, which is already a dependency of five modules in
// this repo. Writing any of them by hand is how a subtly wrong implementation
// ships: it round-trips against itself perfectly and reports nothing, which is
// exactly why the tests use published RFC vectors and not only round-trips.

// KeySize is the ChaCha20-Poly1305 key length.
const KeySize = chacha20poly1305.KeySize

// Domain-separation labels for the two derivations. Part of the wire contract
// byte for byte: two peers that disagree derive different keys, no session
// forms, and nothing says why.
const (
	// keyLabelC2S derives the key protecting client-to-server frames.
	keyLabelC2S = "cuvara/sealed-key/c2s/v1"
	// keyLabelS2C derives the key protecting server-to-client frames.
	keyLabelS2C = "cuvara/sealed-key/s2c/v1"
	// bindingLabel derives the key that authenticates the handshake transcript.
	bindingLabel = "cuvara/sealed-binding/v1"
)

// chachaAEAD adapts x/crypto's ChaCha20-Poly1305 to the AEAD interface.
type chachaAEAD struct {
	inner interface {
		NonceSize() int
		Overhead() int
		Seal(dst, nonce, plaintext, aad []byte) []byte
		Open(dst, nonce, ciphertext, aad []byte) ([]byte, error)
	}
}

func (c chachaAEAD) NonceSize() int { return c.inner.NonceSize() }
func (c chachaAEAD) Overhead() int  { return c.inner.Overhead() }
func (c chachaAEAD) Seal(dst, nonce, plaintext, aad []byte) []byte {
	return c.inner.Seal(dst, nonce, plaintext, aad)
}
func (c chachaAEAD) Open(dst, nonce, ciphertext, aad []byte) ([]byte, error) {
	return c.inner.Open(dst, nonce, ciphertext, aad)
}

// NewAEAD builds the AEAD for one direction from a 32-byte key.
func NewAEAD(key []byte) (AEAD, error) {
	if len(key) != KeySize {
		return nil, fmt.Errorf("sealed: key must be %d bytes, got %d", KeySize, len(key))
	}
	a, err := chacha20poly1305.New(key)
	if err != nil {
		return nil, fmt.Errorf("sealed: new aead: %w", err)
	}
	return chachaAEAD{inner: a}, nil
}

// --- X25519 ---

// ErrLowOrderPoint reports a peer public key that produces an all-zero shared
// secret.
//
// curve25519.X25519 already returns an error for the known low-order points,
// and this wraps it rather than reimplementing the check. Accepting such a key
// would let an attacker force a shared secret both sides agree on and the
// attacker knows, which is a complete break dressed as a successful handshake.
var ErrLowOrderPoint = errors.New("sealed: peer public key is a low-order point")

// KeyPair is an ephemeral X25519 key pair. Ephemeral per connection: reusing
// one across sessions forfeits the forward secrecy that is the whole reason
// ADR-22 supersedes the derived-key scheme.
type KeyPair struct {
	private [32]byte
	// Public is safe to send in the clear; that is what it is for.
	Public [PublicKeySize]byte
}

// GenerateKeyPair produces a fresh ephemeral pair from the system CSPRNG.
func GenerateKeyPair() (*KeyPair, error) {
	kp := &KeyPair{}
	if _, err := io.ReadFull(rand.Reader, kp.private[:]); err != nil {
		return nil, fmt.Errorf("sealed: generate key: %w", err)
	}
	pub, err := curve25519.X25519(kp.private[:], curve25519.Basepoint)
	if err != nil {
		return nil, fmt.Errorf("sealed: derive public key: %w", err)
	}
	copy(kp.Public[:], pub)
	return kp, nil
}

// SharedSecret computes the X25519 shared secret with a peer's public key.
func (k *KeyPair) SharedSecret(peerPublic [PublicKeySize]byte) ([]byte, error) {
	secret, err := curve25519.X25519(k.private[:], peerPublic[:])
	if err != nil {
		return nil, ErrLowOrderPoint
	}
	return secret, nil
}

// --- key schedule ---

// DirectionKeys are the two one-direction AEAD keys for a session.
//
// Two keys, not one, and this is the load-bearing reason the nonce can be a
// bare counter: the client's sequence 7 and the server's sequence 7 are
// encrypted under different keys, so the (key, nonce) pair never repeats
// across directions. Collapsing these into one key breaks the scheme.
type DirectionKeys struct {
	ClientToServer [KeySize]byte
	ServerToClient [KeySize]byte
}

// DeriveKeys expands the X25519 shared secret into the two direction keys,
// salted by the handshake transcript.
//
// Salting with the transcript binds the keys to the exact exchange that
// produced them: the jti, both ephemeral publics and the protocol label. Two
// runs that agreed on a shared secret but disagreed about anything else in the
// transcript derive different keys and simply fail, rather than proceeding on a
// half-agreed view of what was negotiated.
func DeriveKeys(sharedSecret, transcript []byte) (DirectionKeys, error) {
	var out DirectionKeys
	if len(sharedSecret) == 0 {
		return out, errors.New("sealed: empty shared secret")
	}
	if err := expand(sharedSecret, transcript, keyLabelC2S, out.ClientToServer[:]); err != nil {
		return out, err
	}
	if err := expand(sharedSecret, transcript, keyLabelS2C, out.ServerToClient[:]); err != nil {
		return out, err
	}
	return out, nil
}

func expand(secret, salt []byte, label string, dst []byte) error {
	r := hkdf.New(sha256.New, secret, salt, []byte(label))
	if _, err := io.ReadFull(r, dst); err != nil {
		return fmt.Errorf("sealed: hkdf %s: %w", label, err)
	}
	return nil
}

// --- handshake binding ---

// hmacSigner authenticates a transcript with HMAC-SHA256 under a key derived
// from the join-token secret.
type hmacSigner struct{ key [32]byte }

// NewTranscriptSigner derives the binding key from the join-token secret and
// the session's jti, and returns a signer over it.
//
// This is the proof-of-possession ADR-22 requires. The join token itself proves
// nothing — its claims are base64 and an attacker replays what they read —
// whereas this key cannot be computed without JOIN_TOKEN_SECRET.
func NewTranscriptSigner(joinTokenSecret, jti string) (TranscriptSigner, error) {
	if joinTokenSecret == "" {
		return nil, errors.New("sealed: no join-token secret")
	}
	if jti == "" {
		return nil, ErrBadJTI
	}
	s := &hmacSigner{}
	if err := expand([]byte(joinTokenSecret), []byte(jti), bindingLabel, s.key[:]); err != nil {
		return nil, err
	}
	return s, nil
}

// Sign implements TranscriptSigner.
func (h *hmacSigner) Sign(transcript []byte) ([BindingSize]byte, error) {
	m := hmac.New(sha256.New, h.key[:])
	m.Write(transcript)
	var out [BindingSize]byte
	copy(out[:], m.Sum(nil))
	return out, nil
}

// ErrBadBindingTag reports a binding that did not verify.
var ErrBadBindingTag = errors.New("sealed: handshake binding did not verify")

// Verify implements TranscriptSigner.
//
// hmac.Equal is a constant-time comparison. A plain == over the byte arrays
// would leak the position of the first mismatch through timing, which is enough
// to forge a tag one byte at a time against a peer that keeps answering.
func (h *hmacSigner) Verify(transcript []byte, binding [BindingSize]byte) error {
	want, err := h.Sign(transcript)
	if err != nil {
		return err
	}
	if !hmac.Equal(want[:], binding[:]) {
		return ErrBadBindingTag
	}
	return nil
}

// derivePublicForTest exposes public-key derivation to the RFC 7748 vector test
// without widening the API. Test-only by name and by intent.
func (k *KeyPair) derivePublicForTest() ([]byte, error) {
	return curve25519.X25519(k.private[:], curve25519.Basepoint)
}
