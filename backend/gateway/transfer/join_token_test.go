package transfer

import (
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
)

func TestJoinToken_RoundTrip(t *testing.T) {
	secret := "test-secret"
	token, err := GenerateJoinToken("user1", "srv1", secret)
	if err != nil {
		t.Fatalf("GenerateJoinToken() error: %v", err)
	}

	userID, serverID, err := ValidateJoinToken(token, secret)
	if err != nil {
		t.Fatalf("ValidateJoinToken() error: %v", err)
	}
	if userID != "user1" {
		t.Errorf("userID = %q, want %q", userID, "user1")
	}
	if serverID != "srv1" {
		t.Errorf("serverID = %q, want %q", serverID, "srv1")
	}
}

func TestJoinToken_InvalidToken(t *testing.T) {
	_, _, err := ValidateJoinToken("garbage", "secret")
	if err == nil {
		t.Error("ValidateJoinToken() should fail on invalid token")
	}
}

func TestJoinToken_Expired(t *testing.T) {
	secret := "test-secret"
	// Create a token expired beyond the 5s clock skew tolerance.
	token, err := jwt.SignWithServer("user1", "srv1", secret, -10*time.Second)
	if err != nil {
		t.Fatalf("SignWithServer() error: %v", err)
	}

	_, _, err = ValidateJoinToken(token, secret)
	if err == nil {
		t.Error("ValidateJoinToken() should fail on expired token")
	}
}

func TestGenerateJoinTokenKeyring_EmptyServerID(t *testing.T) {
	keys, _ := jwt.NewKeyring("test-secret")
	_, err := GenerateJoinTokenKeyring("user1", "", keys)
	if err == nil {
		t.Error("GenerateJoinTokenKeyring() should fail with empty serverID")
	}
}

func TestJoinToken_WrongSecret(t *testing.T) {
	token, err := GenerateJoinToken("user1", "srv1", "secret-a")
	if err != nil {
		t.Fatalf("GenerateJoinToken() error: %v", err)
	}

	_, _, err = ValidateJoinToken(token, "secret-b")
	if err == nil {
		t.Error("ValidateJoinToken() should fail with wrong secret")
	}
}
