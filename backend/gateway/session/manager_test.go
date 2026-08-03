package session

import (
	"context"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

func TestSessionManager_CreateAndValidate(t *testing.T) {
	store := storage.NewMemorySessionStore()
	mgr := NewSessionManager(store)
	ctx := context.Background()

	key, err := mgr.CreateSession(ctx, "user1")
	if err != nil {
		t.Fatalf("CreateSession() error: %v", err)
	}
	if key == "" {
		t.Fatal("CreateSession() returned empty key")
	}

	userID, err := mgr.ValidateSession(ctx, key)
	if err != nil {
		t.Fatalf("ValidateSession() error: %v", err)
	}
	if userID != "user1" {
		t.Errorf("userID = %q, want %q", userID, "user1")
	}
}

func TestSessionManager_ValidateNonExistent(t *testing.T) {
	store := storage.NewMemorySessionStore()
	mgr := NewSessionManager(store)
	ctx := context.Background()

	_, err := mgr.ValidateSession(ctx, "session:nonexistent")
	if err == nil {
		t.Error("ValidateSession() should fail for non-existent session")
	}
}

func TestSessionManager_Destroy(t *testing.T) {
	store := storage.NewMemorySessionStore()
	mgr := NewSessionManager(store)
	ctx := context.Background()

	key, err := mgr.CreateSession(ctx, "user2")
	if err != nil {
		t.Fatalf("CreateSession() error: %v", err)
	}

	if err := mgr.DestroySession(ctx, key); err != nil {
		t.Fatalf("DestroySession() error: %v", err)
	}

	_, err = mgr.ValidateSession(ctx, key)
	if err == nil {
		t.Error("ValidateSession() should fail after destroy")
	}
}
