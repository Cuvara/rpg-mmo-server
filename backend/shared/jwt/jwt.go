package jwt

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

type header struct {
	Alg string `json:"alg"`
	Typ string `json:"typ"`
}

// ClockSkew is the tolerance applied when checking token expiration. A token
// whose exp is up to this many seconds in the past is still accepted, so small
// clock differences between the gateway and game server do not cause spurious
// rejections.
const ClockSkew = 5 * time.Second

// Claims holds the JWT payload.
type Claims struct {
	UserID   string `json:"sub"`
	ServerID string `json:"sid,omitempty"`
	Jti      string `json:"jti,omitempty"`
	IssuedAt int64  `json:"iat"`
	ExpireAt int64  `json:"exp"`
}

// IsExpired returns true if the token has expired, accounting for ClockSkew.
func (c Claims) IsExpired() bool {
	return time.Now().Add(-ClockSkew).Unix() > c.ExpireAt
}

var defaultHeader = header{Alg: "HS256", Typ: "JWT"}

// Sign creates a HS256 JWT for the given user ID.
func Sign(userID, secret string, expiry time.Duration) (string, error) {
	return SignWithServer(userID, "", secret, expiry)
}

// SignWithServer creates a HS256 JWT with an optional server ID claim.
// When serverID is non-empty (join tokens), a unique JTI claim is added
// for replay protection.
func SignWithServer(userID, serverID, secret string, expiry time.Duration) (string, error) {
	now := time.Now()
	claims := Claims{
		UserID:   userID,
		ServerID: serverID,
		IssuedAt: now.Unix(),
		ExpireAt: now.Add(expiry).Unix(),
	}
	if serverID != "" {
		jti, err := generateJTI()
		if err != nil {
			return "", fmt.Errorf("generate jti: %w", err)
		}
		claims.Jti = jti
	}

	hdrJSON, _ := json.Marshal(defaultHeader)
	clmJSON, err := json.Marshal(claims)
	if err != nil {
		return "", fmt.Errorf("marshal claims: %w", err)
	}

	hdrB64 := base64.RawURLEncoding.EncodeToString(hdrJSON)
	clmB64 := base64.RawURLEncoding.EncodeToString(clmJSON)
	sigInput := hdrB64 + "." + clmB64

	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(sigInput))
	sig := base64.RawURLEncoding.EncodeToString(mac.Sum(nil))

	return sigInput + "." + sig, nil
}

// verifyHeader decodes the JWT header segment and enforces the only algorithm
// and token type this package supports (HS256 / JWT). This rejects algorithm
// confusion attacks such as `"alg":"none"` before any signature work happens.
func verifyHeader(seg string) error {
	hdrJSON, err := base64.RawURLEncoding.DecodeString(seg)
	if err != nil {
		return fmt.Errorf("decode header: %w", err)
	}

	var h header
	if err := json.Unmarshal(hdrJSON, &h); err != nil {
		return fmt.Errorf("unmarshal header: %w", err)
	}

	if h.Alg != defaultHeader.Alg {
		return fmt.Errorf("unsupported alg %q, want %q", h.Alg, defaultHeader.Alg)
	}
	if h.Typ != defaultHeader.Typ {
		return fmt.Errorf("unsupported typ %q, want %q", h.Typ, defaultHeader.Typ)
	}
	return nil
}

// Verify validates a HS256 JWT and returns its claims.
func Verify(token, secret string) (Claims, error) {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return Claims{}, fmt.Errorf("invalid token format")
	}

	if err := verifyHeader(parts[0]); err != nil {
		return Claims{}, fmt.Errorf("invalid header: %w", err)
	}

	sigInput := parts[0] + "." + parts[1]
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(sigInput))
	expectedSig := base64.RawURLEncoding.EncodeToString(mac.Sum(nil))

	if !hmac.Equal([]byte(parts[2]), []byte(expectedSig)) {
		return Claims{}, fmt.Errorf("invalid signature")
	}

	clmJSON, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return Claims{}, fmt.Errorf("decode claims: %w", err)
	}

	var claims Claims
	if err := json.Unmarshal(clmJSON, &claims); err != nil {
		return Claims{}, fmt.Errorf("unmarshal claims: %w", err)
	}

	if claims.IsExpired() {
		return claims, fmt.Errorf("token expired")
	}

	return claims, nil
}

// generateJTI returns a UUID v4 string for use as a unique token identifier.
func generateJTI() (string, error) {
	var buf [16]byte
	if _, err := rand.Read(buf[:]); err != nil {
		return "", err
	}
	// Set version 4 and variant bits per RFC 4122.
	buf[6] = (buf[6] & 0x0f) | 0x40
	buf[8] = (buf[8] & 0x3f) | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x",
		buf[0:4], buf[4:6], buf[6:8], buf[8:10], buf[10:16]), nil
}
