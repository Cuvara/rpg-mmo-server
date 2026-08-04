package jwt

import (
	"crypto/hmac"
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

// Claims holds the JWT payload.
type Claims struct {
	UserID   string `json:"sub"`
	ServerID string `json:"sid,omitempty"`
	IssuedAt int64  `json:"iat"`
	ExpireAt int64  `json:"exp"`
}

// IsExpired returns true if the token has expired.
func (c Claims) IsExpired() bool {
	return time.Now().Unix() > c.ExpireAt
}

var defaultHeader = header{Alg: "HS256", Typ: "JWT"}

// Sign creates a HS256 JWT for the given user ID.
func Sign(userID, secret string, expiry time.Duration) (string, error) {
	return SignWithServer(userID, "", secret, expiry)
}

// SignWithServer creates a HS256 JWT with an optional server ID claim.
func SignWithServer(userID, serverID, secret string, expiry time.Duration) (string, error) {
	now := time.Now()
	claims := Claims{
		UserID:   userID,
		ServerID: serverID,
		IssuedAt: now.Unix(),
		ExpireAt: now.Add(expiry).Unix(),
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
