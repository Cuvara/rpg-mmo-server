package session

import (
	"context"
	"errors"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

func TestGameSessionsIsolation(t *testing.T) {
	ctx := context.Background()
	for _, tc := range []struct{ name, createdGame, missingGame, key string }{
		{"non-default", "foo", constants.DefaultGameID, "session:foo:u1"},
		{"default", constants.DefaultGameID, "foo", "session:u1"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			mgr := NewSessionManager(storage.NewMemorySessionStore())
			key, err := mgr.CreateGameSession(ctx, tc.createdGame, "u1")
			if err != nil || key != tc.key {
				t.Fatalf("CreateGameSession = %q, %v; want %q", key, err, tc.key)
			}
			if _, err := mgr.GetGameSession(ctx, tc.missingGame, "u1"); !errors.Is(err, storage.ErrNotFound) {
				t.Fatalf("other game error = %v, want ErrNotFound", err)
			}
			if got, err := mgr.ValidateSession(ctx, key); err != nil || got != "u1" {
				t.Errorf("ValidateSession = %q, %v", got, err)
			}
		})
	}
	mgr := NewSessionManager(storage.NewMemorySessionStore())
	for _, game := range []string{constants.DefaultGameID, "foo"} {
		if _, err := mgr.CreateGameSession(ctx, game, "u1"); err != nil {
			t.Fatal(err)
		}
	}
	if err := mgr.UpdateGameSession(ctx, "foo", "u1", func(sd *SessionData) { sd.ServerID = "server-foo" }); err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct{ game, want string }{{constants.DefaultGameID, ""}, {"foo", "server-foo"}} {
		sd, err := mgr.GetGameSession(ctx, tc.game, "u1")
		if err != nil || sd.ServerID != tc.want {
			t.Errorf("GetGameSession(%q) = %+v, %v", tc.game, sd, err)
		}
	}
}

func TestValidateSessionKeySuffix(t *testing.T) {
	ctx := context.Background()
	for _, tc := range []struct{ key, want string }{
		{"session:foo:u1", "u1"}, {"session:u1", "u1"}, {"session:cuvara:u1", "cuvara:u1"},
		{"session:Foo:u1", "Foo:u1"}, {"session:gs:u1", "gs:u1"}, {"session:foo:", "foo:"},
	} {
		t.Run(tc.key, func(t *testing.T) {
			store := storage.NewMemorySessionStore()
			if err := store.Set(ctx, tc.key, []byte(`{"created_at":1}`), constants.SessionTTL); err != nil {
				t.Fatal(err)
			}
			got, err := NewSessionManager(store).ValidateSession(ctx, tc.key)
			if err != nil || got != tc.want {
				t.Errorf("ValidateSession = %q, %v; want %q", got, err, tc.want)
			}
		})
	}
}
