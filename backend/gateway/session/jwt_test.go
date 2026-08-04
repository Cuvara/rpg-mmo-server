package session

import (
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
)

func TestVerifyClientJWT_Valid(t *testing.T) {
	secret := "test-secret"
	token, err := jwt.Sign("user123", secret, 1*time.Hour)
	if err != nil {
		t.Fatalf("Sign() error: %v", err)
	}

	userID, err := VerifyClientJWT(token, secret)
	if err != nil {
		t.Fatalf("VerifyClientJWT() error: %v", err)
	}
	if userID != "user123" {
		t.Errorf("userID = %q, want %q", userID, "user123")
	}
}

func TestVerifyClientJWT_InvalidToken(t *testing.T) {
	_, err := VerifyClientJWT("not-a-valid-token", "secret")
	if err == nil {
		t.Error("VerifyClientJWT() should fail on invalid token")
	}
}

func TestVerifyClientJWT_WrongSecret(t *testing.T) {
	token, err := jwt.Sign("user123", "secret-a", 1*time.Hour)
	if err != nil {
		t.Fatalf("Sign() error: %v", err)
	}

	_, err = VerifyClientJWT(token, "secret-b")
	if err == nil {
		t.Error("VerifyClientJWT() should fail with wrong secret")
	}
}

func TestVerifyClientJWT_Expired(t *testing.T) {
	// Sign with a negative expiry to create an already-expired token.
	token, err := jwt.Sign("user123", "secret", -1*time.Second)
	if err != nil {
		t.Fatalf("Sign() error: %v", err)
	}

	_, err = VerifyClientJWT(token, "secret")
	if err == nil {
		t.Error("VerifyClientJWT() should fail on expired token")
	}
}
