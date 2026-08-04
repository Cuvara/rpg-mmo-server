package session

import (
	"context"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"
)

func TestSessionKey(t *testing.T) {
	if got, want := SessionKey("user1"), constants.SessionKeyPrefix+"user1"; got != want {
		t.Errorf("SessionKey() = %q, want %q", got, want)
	}
}

// newManagers returns one SessionManager per store implementation.
func newManagers(t *testing.T) map[string]*SessionManager {
	t.Helper()
	mr := miniredis.RunT(t)
	rs := redisstore.NewSessionStore(mr.Addr(), "")
	t.Cleanup(func() { rs.Close() })
	return map[string]*SessionManager{
		"memory": NewSessionManager(storage.NewMemorySessionStore()),
		"redis":  NewSessionManager(rs),
	}
}

func TestSessionManager_Refresh(t *testing.T) {
	tests := []struct {
		name    string
		create  bool
		wantErr bool
	}{
		{name: "existing session", create: true},
		{name: "missing session", create: false, wantErr: true},
	}

	ctx := context.Background()
	for storeName, mgr := range newManagers(t) {
		for _, tt := range tests {
			t.Run(storeName+"/"+tt.name, func(t *testing.T) {
				userID := storeName + tt.name
				key := SessionKey(userID)
				if tt.create {
					if _, err := mgr.CreateSession(ctx, userID); err != nil {
						t.Fatalf("CreateSession: %v", err)
					}
				}
				err := mgr.RefreshSession(ctx, key)
				if tt.wantErr {
					if err == nil {
						t.Error("RefreshSession() should fail for a missing session")
					}
					return
				}
				if err != nil {
					t.Fatalf("RefreshSession() error: %v", err)
				}
				if _, err := mgr.ValidateSession(ctx, key); err != nil {
					t.Errorf("session should still be valid: %v", err)
				}
			})
		}
	}
}

func TestSessionManager_RefreshExtendsTTL(t *testing.T) {
	mr := miniredis.RunT(t)
	store := redisstore.NewSessionStore(mr.Addr(), "")
	defer store.Close()

	mgr := NewSessionManager(store)
	ctx := context.Background()
	if _, err := mgr.CreateSession(ctx, "user1"); err != nil {
		t.Fatalf("CreateSession: %v", err)
	}

	key := SessionKey("user1")
	mr.FastForward(30 * time.Minute)
	before := mr.TTL(key)

	if err := mgr.RefreshSession(ctx, key); err != nil {
		t.Fatalf("RefreshSession: %v", err)
	}
	if after := mr.TTL(key); after <= before {
		t.Errorf("TTL not extended: before=%v after=%v", before, after)
	}
}
