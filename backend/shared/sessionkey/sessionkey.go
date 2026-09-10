// Package sessionkey derives and carries the per-session key that encrypts one
// client's gameplay hop.
//
// # What this replaces
//
// Transport encryption used ONE pre-shared key: the same value in every client
// binary and every server. Extracting it from a single client decrypted every
// player's traffic for ever, and rotating it meant redeploying everything at
// once. The key here is per join.
//
// # The derivation, and why nothing is transmitted to the server
//
//	key = HKDF-SHA256(ikm  = JOIN_TOKEN_SECRET,
//	                  salt = join token's jti claim,
//	                  info = "cuvara/session-key/v1",
//	                  L    = 32)
//
// The gateway computes it when it mints the join token and returns it to the
// client in EnterWorldResponse. The game server computes the SAME value from the
// secret it already holds and the jti it reads out of the token it already
// verifies — so the key never crosses the gameplay hop, is never stored, and is
// never put in the token.
//
// Rooting session keys in JOIN_TOKEN_SECRET adds no new class of failure:
// anyone holding that secret can already mint a join token for any user, which
// is total compromise. The info string is the domain separation that keeps
// derivation from interacting with signing, and it is byte-identical across
// implementations — the same discipline KcpCrypto's HKDF string already follows.
//
// # Limitation, stated here because it is easy to overstate what this buys
//
// The client cannot derive the key (it has no secret), so the key must travel
// gateway -> client, and the gateway hop is the SAME transport stack as the
// gameplay hop — plaintext TCP by default. In the default configuration an
// eavesdropper on the gateway hop reads the key and can decrypt that session.
// This turns "compromise one binary, decrypt everyone for ever" into "eavesdrop
// the gateway hop, decrypt one session". Strictly better; not end-to-end
// confidentiality. Closing it requires encrypting the gateway hop too.
package sessionkey

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"log/slog"

	"golang.org/x/crypto/hkdf"
)

// Size is the derived key length in bytes.
const Size = 32

// Info is the HKDF domain-separation string. It is part of the wire contract:
// every implementation must use these exact bytes or the two ends derive
// different keys and no session forms. Never "improve" it.
const Info = "cuvara/session-key/v1"

// Redacted is what a Key renders as anywhere a string is expected.
const Redacted = "[redacted session key]"

// ErrNoSecret is returned when there is no join-token secret to derive from.
var ErrNoSecret = errors.New("sessionkey: no join-token secret configured")

// ErrNoJTI is returned when the join token carries no jti claim, which means it
// was not minted as a join token and there is no per-session salt to use.
var ErrNoJTI = errors.New("sessionkey: join token has no jti claim")

// Key is a derived per-session key.
//
// It deliberately makes itself hard to leak: String, GoString, LogValue and
// MarshalJSON all render Redacted, so the key does not appear through fmt, slog,
// %#v, or any struct that happens to get JSON-encoded — which is the realistic
// way a secret escapes, not a deliberate log call.
type Key struct {
	b []byte
}

// String implements fmt.Stringer with a redacted form.
func (k Key) String() string { return Redacted }

// GoString implements fmt.GoStringer, so %#v does not dump the bytes either.
func (k Key) GoString() string { return Redacted }

// LogValue implements slog.LogValuer. Without it slog would reflect over the
// struct and print the field.
func (k Key) LogValue() slog.Value { return slog.StringValue(Redacted) }

// MarshalJSON renders the redacted string, so a Key reachable from any
// JSON-encoded value cannot leak — /status being the surface most likely to grow
// a field by accident.
func (k Key) MarshalJSON() ([]byte, error) { return []byte(`"` + Redacted + `"`), nil }

// IsZero reports whether no key was derived.
func (k Key) IsZero() bool { return len(k.b) == 0 }

// Bytes returns a copy of the key material. The copy is deliberate: a caller
// that mutated a shared slice would silently change the key for everyone
// holding it.
func (k Key) Bytes() []byte {
	if len(k.b) == 0 {
		return nil
	}
	out := make([]byte, len(k.b))
	copy(out, k.b)
	return out
}

// Hex returns the key as lowercase hex. For tests and cross-implementation
// vectors only — never log this.
func (k Key) Hex() string { return hex.EncodeToString(k.b) }

// FromBytes wraps existing key material, for a receiver that was handed a key
// rather than deriving one.
func FromBytes(b []byte) Key {
	if len(b) == 0 {
		return Key{}
	}
	out := make([]byte, len(b))
	copy(out, b)
	return Key{b: out}
}

// Derive computes the per-session key from the join-token secret and the
// token's jti claim.
//
// secret may be a comma-separated rotation list ("current,previous"), in which
// case the FIRST entry is used — the same one Sign uses to mint, so both ends
// agree. A verifier that accepted an older key would have to try each in turn,
// which is a downgrade surface for no benefit: the jti is fresh per join, so
// there is never an old session key worth accepting.
func Derive(secret, jti string) (Key, error) {
	current := firstKey(secret)
	if current == "" {
		return Key{}, ErrNoSecret
	}
	if jti == "" {
		return Key{}, ErrNoJTI
	}

	r := hkdf.New(sha256.New, []byte(current), []byte(jti), []byte(Info))
	out := make([]byte, Size)
	if _, err := io.ReadFull(r, out); err != nil {
		return Key{}, fmt.Errorf("sessionkey: derive: %w", err)
	}
	return Key{b: out}, nil
}

// firstKey takes the active entry from a comma-separated rotation list.
func firstKey(secret string) string {
	for i := 0; i < len(secret); i++ {
		if secret[i] == ',' {
			return trimSpace(secret[:i])
		}
	}
	return trimSpace(secret)
}

func trimSpace(s string) string {
	start, end := 0, len(s)
	for start < end && (s[start] == ' ' || s[start] == '\t' || s[start] == '\n' || s[start] == '\r') {
		start++
	}
	for end > start && (s[end-1] == ' ' || s[end-1] == '\t' || s[end-1] == '\n' || s[end-1] == '\r') {
		end--
	}
	return s[start:end]
}
