package jwt

import (
	"fmt"
	"strings"
	"time"
)

// Keyring is an ordered set of HS256 secrets that makes secret rotation
// non-disruptive.
//
// Rotation without a keyring means every live token signed with the old secret
// is rejected the moment the new secret is deployed — every player is logged
// out and every in-flight join token dies. With a keyring the operator deploys
// `JWT_SECRET=new,old`, waits for the old token TTL to elapse (SessionTTL for
// auth tokens, JoinTokenTTL for join tokens), then deploys `JWT_SECRET=new`.
//
// Rules:
//
//   - The FIRST secret signs. Everything issued after the deploy uses the new
//     secret immediately.
//   - EVERY secret verifies, tried in order. A token signed with any listed
//     secret is accepted, so the old population drains naturally.
//
// The verification cost is O(len(secrets)) HMAC-SHA256 computations in the
// worst case (~1µs each), which is why a keyring is meant to hold two secrets
// during a rotation window, not a permanent archive.
//
// The zero Keyring is unusable; build one with NewKeyring or ParseKeyring.
type Keyring struct {
	secrets []string
}

// KeyringSeparator splits the secret list in the JWT_SECRET /
// JOIN_TOKEN_SECRET environment variables.
const KeyringSeparator = ","

// ParseKeyring builds a Keyring from a comma-separated secret list, e.g.
// "current-secret,previous-secret". Whitespace around each entry is trimmed and
// empty entries are dropped, so "new, old" and "new,old" are equivalent and a
// trailing comma is harmless.
//
// An empty spec (or one with only empty entries) yields an error: a service
// that cannot verify anything must fail loudly at start-up rather than reject
// every client at runtime.
func ParseKeyring(spec string) (Keyring, error) {
	var secrets []string
	for _, part := range strings.Split(spec, KeyringSeparator) {
		if s := strings.TrimSpace(part); s != "" {
			secrets = append(secrets, s)
		}
	}
	if len(secrets) == 0 {
		return Keyring{}, fmt.Errorf("parse keyring: no secrets in %q", spec)
	}
	return Keyring{secrets: secrets}, nil
}

// NewKeyring builds a Keyring from explicit secrets, first one signing.
func NewKeyring(secrets ...string) (Keyring, error) {
	return ParseKeyring(strings.Join(secrets, KeyringSeparator))
}

// Len returns how many secrets the keyring holds. A value > 1 means a rotation
// is in progress and old tokens are still being accepted.
func (k Keyring) Len() int { return len(k.secrets) }

// Valid reports whether the keyring holds at least one secret.
func (k Keyring) Valid() bool { return len(k.secrets) > 0 }

// Signing returns the secret used to sign new tokens (the first entry), or the
// empty string for a zero Keyring.
func (k Keyring) Signing() string {
	if len(k.secrets) == 0 {
		return ""
	}
	return k.secrets[0]
}

// Sign issues a HS256 token for userID with the signing secret.
func (k Keyring) Sign(userID string, expiry time.Duration) (string, error) {
	return k.SignWithServer(userID, "", expiry)
}

// SignWithServer issues a HS256 token with an optional server ID claim, using
// the signing secret.
func (k Keyring) SignWithServer(userID, serverID string, expiry time.Duration) (string, error) {
	if !k.Valid() {
		return "", fmt.Errorf("sign: empty keyring")
	}
	return SignWithServer(userID, serverID, k.Signing(), expiry)
}

// Verify validates a token against every secret in the keyring, in order, and
// returns the claims of the first secret that accepts it.
//
// An expired token short-circuits: Verify returns the expiry error immediately
// instead of retrying the remaining secrets, because a valid signature with a
// dead expiry is never going to become valid under a different key.
func (k Keyring) Verify(token string) (Claims, error) {
	if !k.Valid() {
		return Claims{}, fmt.Errorf("verify: empty keyring")
	}
	var lastErr error
	for _, secret := range k.secrets {
		claims, err := Verify(token, secret)
		if err == nil {
			return claims, nil
		}
		// A signature that matched but whose exp has passed is a definitive
		// answer, not a "try the next key" answer.
		if !claims.IsZero() {
			return claims, err
		}
		lastErr = err
	}
	return Claims{}, fmt.Errorf("verify against %d key(s): %w", len(k.secrets), lastErr)
}

// IsZero reports whether the claims are empty, i.e. Verify failed before it
// could decode a payload.
func (c Claims) IsZero() bool {
	return c.UserID == "" && c.ServerID == "" && c.IssuedAt == 0 && c.ExpireAt == 0
}
