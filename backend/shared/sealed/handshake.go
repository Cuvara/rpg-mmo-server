package sealed

import (
	"errors"
	"fmt"
)

// PublicKeySize is the length of an X25519 public key.
const PublicKeySize = 32

// BindingSize is the length of the handshake binding tag (HMAC-SHA256).
const BindingSize = 32

// TranscriptLabel is the domain-separation label mixed into every handshake
// transcript. Part of the wire contract, byte for byte, exactly like
// KcpCrypto's HKDF string: two peers that disagree about it derive different
// bindings and the handshake fails with no indication of why.
const TranscriptLabel = "cuvara/sealed-handshake/v1"

var (
	// ErrBadPublicKey reports a public key of the wrong length.
	ErrBadPublicKey = errors.New("sealed: public key must be 32 bytes")
	// ErrBadBinding reports a binding tag of the wrong length.
	ErrBadBinding = errors.New("sealed: binding must be 32 bytes")
	// ErrBadJTI reports a missing join-token id.
	ErrBadJTI = errors.New("sealed: handshake needs the join token's jti")
)

// ClientHello is the client's first frame on the gameplay hop. It is sent in
// the CLEAR — there is no key yet — immediately after the join token.
type ClientHello struct {
	// Ephemeral X25519 public key. Fresh per connection: reusing one across
	// sessions would forfeit the forward secrecy that is the entire reason
	// ADR-22 supersedes the derived-key scheme.
	Public [PublicKeySize]byte
}

// ServerHello is the server's reply, also in the clear.
type ServerHello struct {
	// Ephemeral X25519 public key, fresh per connection.
	Public [PublicKeySize]byte

	// Binding proves the sender holds JOIN_TOKEN_SECRET-derived material for
	// THIS session. See the package-level note below on why it is not the join
	// token itself.
	Binding [BindingSize]byte
}

// Transcript builds the bytes both peers authenticate.
//
// Layout, all fixed-width and unambiguous:
//
//	label || 0x00 || jti || 0x00 || clientPublic (32) || serverPublic (32)
//
// The NUL separators matter. Without them, a transcript is a concatenation
// whose pieces can be re-split: a jti ending in one byte of what should be the
// next field produces the same bytes as a different (jti, key) pair, and a MAC
// over it authenticates both readings equally. That is a real attack on
// length-ambiguous transcripts, not a theoretical one, and it costs two bytes
// to remove.
//
// # Why the binding is over the transcript, and not the join token
//
// The join token is readable by anyone on the wire — its claims are base64, not
// encrypted. So "present the token" proves nothing to an eavesdropper's victim:
// an attacker replays what they read. What an attacker cannot do is compute a
// MAC keyed by material derived from JOIN_TOKEN_SECRET, which they do not hold.
//
// Binding that MAC to the two EPHEMERAL PUBLIC KEYS is what defeats the
// man-in-the-middle. An attacker who substitutes their own key changes the
// transcript, so the binding they can replay no longer verifies. Without the
// public keys in the transcript, a replayed binding would authenticate the
// attacker's exchange just as well as the real one, and the MITM would be
// clean and undetectable.
func Transcript(jti string, clientPublic, serverPublic [PublicKeySize]byte) ([]byte, error) {
	if jti == "" {
		return nil, ErrBadJTI
	}
	out := make([]byte, 0, len(TranscriptLabel)+1+len(jti)+1+2*PublicKeySize)
	out = append(out, TranscriptLabel...)
	out = append(out, 0x00)
	out = append(out, jti...)
	out = append(out, 0x00)
	out = append(out, clientPublic[:]...)
	out = append(out, serverPublic[:]...)
	return out, nil
}

// TranscriptSigner computes and verifies the handshake binding.
//
// No implementation here, deliberately: the MAC is a cryptographic primitive
// and ADR-22 has not settled the library. The shape is fixed so the call sites,
// the transcript and the tests can exist now.
//
// Verify MUST compare in constant time. A byte-by-byte comparison leaks the
// position of the first mismatch through timing, which is enough to forge a tag
// one byte at a time against a peer that will keep answering.
type TranscriptSigner interface {
	// Sign returns the binding for a transcript, keyed by material derived from
	// the join-token secret and the session's jti.
	Sign(transcript []byte) ([BindingSize]byte, error)
	// Verify reports whether binding is valid for transcript.
	Verify(transcript []byte, binding [BindingSize]byte) error
}

// ValidateClientHello checks the shape of a received hello. It cannot check
// anything cryptographic — that is the binding's job, one message later.
func ValidateClientHello(public []byte) (ClientHello, error) {
	if len(public) != PublicKeySize {
		return ClientHello{}, fmt.Errorf("%w: got %d", ErrBadPublicKey, len(public))
	}
	var h ClientHello
	copy(h.Public[:], public)
	return h, nil
}
