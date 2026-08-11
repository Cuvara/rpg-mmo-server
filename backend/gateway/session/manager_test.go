package session

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

func TestSessionManager_CreateAndValidate(t *testing.T) {
	store := storage.NewMemorySessionStore()
	mgr := NewSessionManager(store, "gw-test")
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
	mgr := NewSessionManager(store, "gw-test")
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

func TestSessionManager_CreateSessionStoresJSON(t *testing.T) {
	store := storage.NewMemorySessionStore()
	mgr := NewSessionManager(store, "gw-alpha")
	ctx := context.Background()

	_, err := mgr.CreateSession(ctx, "user1")
	if err != nil {
		t.Fatalf("CreateSession() error: %v", err)
	}

	raw, err := store.Get(ctx, SessionKey("user1"))
	if err != nil {
		t.Fatalf("store.Get() error: %v", err)
	}
	var sd SessionData
	if err := json.Unmarshal(raw, &sd); err != nil {
		t.Fatalf("unmarshal session data: %v", err)
	}
	if sd.GatewayID != "gw-alpha" {
		t.Errorf("GatewayID = %q, want %q", sd.GatewayID, "gw-alpha")
	}
	if sd.CreatedAt == 0 {
		t.Error("CreatedAt should be set")
	}
	if sd.LastActivity == 0 {
		t.Error("LastActivity should be set")
	}
	if sd.ServerID != "" {
		t.Errorf("ServerID should be empty initially, got %q", sd.ServerID)
	}
}

func TestSessionManager_GetSession(t *testing.T) {
	tests := []struct {
		name    string
		create  bool
		wantErr bool
	}{
		{name: "existing session", create: true},
		{name: "missing session", create: false, wantErr: true},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			store := storage.NewMemorySessionStore()
			mgr := NewSessionManager(store, "gw-0")
			ctx := context.Background()

			userID := "user-" + tt.name
			if tt.create {
				if _, err := mgr.CreateSession(ctx, userID); err != nil {
					t.Fatalf("CreateSession: %v", err)
				}
			}
			sd, err := mgr.GetSession(ctx, userID)
			if tt.wantErr {
				if err == nil {
					t.Error("GetSession() should fail for a missing session")
				}
				return
			}
			if err != nil {
				t.Fatalf("GetSession() error: %v", err)
			}
			if sd.GatewayID != "gw-0" {
				t.Errorf("GatewayID = %q, want %q", sd.GatewayID, "gw-0")
			}
		})
	}
}

func TestSessionManager_UpdateSession(t *testing.T) {
	store := storage.NewMemorySessionStore()
	mgr := NewSessionManager(store, "gw-0")
	ctx := context.Background()

	if _, err := mgr.CreateSession(ctx, "user1"); err != nil {
		t.Fatalf("CreateSession: %v", err)
	}

	err := mgr.UpdateSession(ctx, "user1", func(sd *SessionData) {
		sd.ServerID = "srv-42"
		sd.MapID = "map_forest"
	})
	if err != nil {
		t.Fatalf("UpdateSession() error: %v", err)
	}

	sd, err := mgr.GetSession(ctx, "user1")
	if err != nil {
		t.Fatalf("GetSession() error: %v", err)
	}
	if sd.ServerID != "srv-42" {
		t.Errorf("ServerID = %q, want %q", sd.ServerID, "srv-42")
	}
	if sd.MapID != "map_forest" {
		t.Errorf("MapID = %q, want %q", sd.MapID, "map_forest")
	}
	if sd.GatewayID != "gw-0" {
		t.Errorf("GatewayID should be preserved, got %q", sd.GatewayID)
	}
}

func TestSessionManager_UpdateSession_Missing(t *testing.T) {
	store := storage.NewMemorySessionStore()
	mgr := NewSessionManager(store, "gw-0")
	ctx := context.Background()

	err := mgr.UpdateSession(ctx, "nonexistent", func(sd *SessionData) {
		sd.ServerID = "srv-1"
	})
	if err == nil {
		t.Error("UpdateSession() should fail for a missing session")
	}
}

func TestSessionManager_GatewayID(t *testing.T) {
	tests := []struct {
		name string
		gid  string
		want string
	}{
		{name: "explicit", gid: "gw-prod-1", want: "gw-prod-1"},
		{name: "empty", gid: "", want: ""},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var mgr *SessionManager
			if tt.gid != "" {
				mgr = NewSessionManager(storage.NewMemorySessionStore(), tt.gid)
			} else {
				mgr = NewSessionManager(storage.NewMemorySessionStore())
			}
			if got := mgr.GatewayID(); got != tt.want {
				t.Errorf("GatewayID() = %q, want %q", got, tt.want)
			}
		})
	}
}
