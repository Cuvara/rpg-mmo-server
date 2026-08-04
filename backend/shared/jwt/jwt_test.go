package jwt

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"strings"
	"testing"
	"time"
)

// forgeToken builds a token with an arbitrary raw header segment, signed with
// the given secret so that only the header validation can reject it.
func forgeToken(rawHeaderJSON, secret string) string {
	hdrB64 := base64.RawURLEncoding.EncodeToString([]byte(rawHeaderJSON))
	clmB64 := base64.RawURLEncoding.EncodeToString(
		[]byte(`{"sub":"user1","iat":0,"exp":99999999999}`))
	sigInput := hdrB64 + "." + clmB64
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(sigInput))
	return sigInput + "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
}

func TestSignAndVerify(t *testing.T) {
	secret := "test-secret"
	token, err := Sign("user123", secret, time.Hour)
	if err != nil {
		t.Fatalf("Sign() error: %v", err)
	}
	if token == "" {
		t.Fatal("Sign() returned empty token")
	}

	claims, err := Verify(token, secret)
	if err != nil {
		t.Fatalf("Verify() error: %v", err)
	}
	if claims.UserID != "user123" {
		t.Errorf("UserID = %q, want %q", claims.UserID, "user123")
	}
}

func TestSignWithServer(t *testing.T) {
	secret := "test-secret"
	token, err := SignWithServer("user1", "server-abc", secret, time.Hour)
	if err != nil {
		t.Fatalf("SignWithServer() error: %v", err)
	}

	claims, err := Verify(token, secret)
	if err != nil {
		t.Fatalf("Verify() error: %v", err)
	}
	if claims.ServerID != "server-abc" {
		t.Errorf("ServerID = %q, want %q", claims.ServerID, "server-abc")
	}
}

func TestVerify_WrongSecret(t *testing.T) {
	token, _ := Sign("user1", "secret-a", time.Hour)
	_, err := Verify(token, "secret-b")
	if err == nil {
		t.Error("Verify() should fail with wrong secret")
	}
}

func TestVerify_Expired(t *testing.T) {
	token, _ := Sign("user1", "secret", -time.Hour) // already expired
	_, err := Verify(token, "secret")
	if err == nil {
		t.Error("Verify() should fail with expired token")
	}
}

func TestVerify_BadFormat(t *testing.T) {
	_, err := Verify("not.a.valid.token.at.all", "secret")
	if err == nil {
		t.Error("Verify() should fail with bad format")
	}
}

func TestVerify_HeaderValidation(t *testing.T) {
	const secret = "test-secret"

	tests := []struct {
		name   string
		header string
	}{
		{"alg none", `{"alg":"none","typ":"JWT"}`},
		{"alg empty", `{"typ":"JWT"}`},
		{"wrong alg HS512", `{"alg":"HS512","typ":"JWT"}`},
		{"asymmetric alg RS256", `{"alg":"RS256","typ":"JWT"}`},
		{"lowercase alg", `{"alg":"hs256","typ":"JWT"}`},
		{"wrong typ", `{"alg":"HS256","typ":"JWE"}`},
		{"missing typ", `{"alg":"HS256"}`},
		{"header not json", `not-json`},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			// Signed with the real secret: only header validation can reject it.
			token := forgeToken(tt.header, secret)
			if _, err := Verify(token, secret); err == nil {
				t.Errorf("Verify() should reject header %s", tt.header)
			}
		})
	}
}

func TestVerify_TamperedHeader(t *testing.T) {
	const secret = "test-secret"
	token, err := Sign("user1", secret, time.Hour)
	if err != nil {
		t.Fatalf("Sign() error: %v", err)
	}

	parts := strings.Split(token, ".")
	parts[0] = base64.RawURLEncoding.EncodeToString([]byte(`{"alg":"none","typ":"JWT"}`))
	if _, err := Verify(strings.Join(parts, "."), secret); err == nil {
		t.Error("Verify() should reject a token with a swapped header")
	}

	// Non-base64 header segment.
	if _, err := Verify("!!!."+parts[1]+"."+parts[2], secret); err == nil {
		t.Error("Verify() should reject a non-base64 header")
	}
}

func TestVerify_ValidHeaderStillPasses(t *testing.T) {
	const secret = "test-secret"
	token := forgeToken(`{"alg":"HS256","typ":"JWT"}`, secret)
	if _, err := Verify(token, secret); err != nil {
		t.Errorf("Verify() rejected a valid HS256/JWT header: %v", err)
	}
}
