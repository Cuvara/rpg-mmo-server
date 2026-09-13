package sealed

import (
	"crypto/ed25519"
	"encoding/base64"
	"errors"
	"fmt"
)

// IdentityLabel is the domain-separation label for the game server's identity
// signature. Part of the wire contract, byte for byte, exactly like
// TranscriptLabel: two peers that disagree about it produce signatures neither
// can verify, and the failure names nothing.
//
// It is DIFFERENT from TranscriptLabel on purpose. The two inputs must not be
// confusable — a signature over one must never verify as a signature over the
// other — and a distinct label is what guarantees that without depending on the
// lengths of the pieces.
const IdentityLabel = "cuvara/sealed-identity/v1"

// IdentityKeySize is the length of an Ed25519 public key.
const IdentityKeySize = ed25519.PublicKeySize // 32

// IdentitySignatureSize is the length of an Ed25519 signature.
const IdentitySignatureSize = ed25519.SignatureSize // 64

// Errors from the identity half of the handshake.
var (
	// ErrBadIdentityKey reports an identity public key of the wrong length.
	ErrBadIdentityKey = errors.New("sealed: identity key must be 32 bytes")
	// ErrBadIdentitySignature reports a signature of the wrong length.
	ErrBadIdentitySignature = errors.New("sealed: identity signature must be 64 bytes")
	// ErrIdentityNotVerified reports a signature that did not verify under the
	// identity key the client was given.
	//
	// THIS IS A REFUSAL, not a diagnostic. It means the peer on the gameplay hop
	// does not hold the private half of the key the gateway named — either a man
	// in the middle, or two sides that disagree about the protocol. Neither is a
	// session worth having, and ADR-22 decision 3 forbids continuing in the
	// weaker mode.
	ErrIdentityNotVerified = errors.New("sealed: server identity signature did not verify")
	// ErrIdentityMissing reports that a client which requires identity was given
	// no signature to check.
	ErrIdentityMissing = errors.New("sealed: server sent no identity signature")
)

// IdentityInput builds the bytes the server signs with its per-pod Ed25519 key:
//
//	IdentityLabel || 0x00 || transcript || 0x00 || identityPublic (32)
//
// # The transcript is NOT modified, and that is the point
//
// `transcript` is exactly the bytes Transcript returns, unchanged. The HMAC
// binding and DeriveKeys keep reading those same bytes, so ADR-22's
// cross-implementation vectors stay valid and this signature gets vectors of
// its own instead of invalidating theirs. Wrapping rather than extending is the
// only way to add an authenticated statement to a transcript that other things
// already depend on byte for byte.
//
// # Why the identity key is inside the signed input
//
// A signature that does not name its signer authenticates a statement ABOUT a
// key rather than a key. Without identityPublic here, a signature made by pod A
// over a transcript is a valid signature over the same transcript for anyone
// claiming to be pod B — the verifier would be checking "someone signed this
// exchange", which is not the question. With it, the signature says "the holder
// of THIS key signed THIS exchange", which is.
//
// The NUL separators are load-bearing for the reason Transcript already gives:
// without them the pieces can be re-split, and a signature over the
// concatenation authenticates every reading of it equally.
func IdentityInput(transcript, identityPublic []byte) ([]byte, error) {
	if len(transcript) == 0 {
		return nil, errors.New("sealed: empty transcript")
	}
	if len(identityPublic) != IdentityKeySize {
		return nil, fmt.Errorf("%w: got %d", ErrBadIdentityKey, len(identityPublic))
	}
	out := make([]byte, 0, len(IdentityLabel)+1+len(transcript)+1+IdentityKeySize)
	out = append(out, IdentityLabel...)
	out = append(out, 0x00)
	out = append(out, transcript...)
	out = append(out, 0x00)
	out = append(out, identityPublic...)
	return out, nil
}

// SignIdentity signs a transcript with an Ed25519 private key.
//
// Used by test harnesses and vector generation in Go; the production signer is
// the C# game server (GameServer.Net.Sealed.ServerIdentity), which must produce
// byte-identical signatures — Ed25519 is deterministic, so "byte-identical" is
// a testable claim rather than an aspiration, and interop_test.go tests it.
func SignIdentity(priv ed25519.PrivateKey, transcript []byte) ([]byte, error) {
	if len(priv) != ed25519.PrivateKeySize {
		return nil, fmt.Errorf("sealed: identity private key must be %d bytes, got %d",
			ed25519.PrivateKeySize, len(priv))
	}
	input, err := IdentityInput(transcript, priv.Public().(ed25519.PublicKey))
	if err != nil {
		return nil, err
	}
	return ed25519.Sign(priv, input), nil
}

// VerifyIdentity checks a server identity signature over a transcript.
//
// It returns ErrIdentityNotVerified — never a bool — because every caller of
// this must refuse the session on failure, and an error is harder to drop on
// the floor than a false.
func VerifyIdentity(identityPublic, transcript, signature []byte) error {
	if len(identityPublic) != IdentityKeySize {
		return fmt.Errorf("%w: got %d", ErrBadIdentityKey, len(identityPublic))
	}
	if len(signature) != IdentitySignatureSize {
		return fmt.Errorf("%w: got %d", ErrBadIdentitySignature, len(signature))
	}
	input, err := IdentityInput(transcript, identityPublic)
	if err != nil {
		return err
	}
	if !ed25519.Verify(ed25519.PublicKey(identityPublic), input, signature) {
		return ErrIdentityNotVerified
	}
	return nil
}

// EncodeIdentityKey renders a public key for the Redis registry entry.
//
// Standard base64 WITH padding. The encoding is part of the cross-language
// contract — the C# game server writes this field and the Go gateway reads it
// with no translation layer in between — so it is fixed here and mirrored in
// GameServer.Net.Sealed.ServerIdentity.PublicKeyBase64. Base64 rather than raw
// bytes because the surrounding hash fields are all human-readable in
// `redis-cli`, and an operator reading one entry should not hit a field that
// terminal-garbles the rest of the line.
func EncodeIdentityKey(key []byte) string {
	if len(key) == 0 {
		return ""
	}
	return base64.StdEncoding.EncodeToString(key)
}

// DecodeIdentityKey parses a registry entry's identity key.
//
// An empty string decodes to nil with no error: that is a game server older
// than ADR-25, or an entry written before the field existed, and it must not
// fail a join for a client that does not require identity. A MALFORMED value is
// an error, because it means something wrote garbage into the registry and
// silently treating that as "no key" would hide it.
func DecodeIdentityKey(s string) ([]byte, error) {
	if s == "" {
		return nil, nil
	}
	raw, err := base64.StdEncoding.DecodeString(s)
	if err != nil {
		return nil, fmt.Errorf("sealed: decode identity key: %w", err)
	}
	if len(raw) != IdentityKeySize {
		return nil, fmt.Errorf("%w: got %d", ErrBadIdentityKey, len(raw))
	}
	return raw, nil
}
