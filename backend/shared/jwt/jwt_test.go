package jwt

import (
	"testing"
	"time"
)

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
