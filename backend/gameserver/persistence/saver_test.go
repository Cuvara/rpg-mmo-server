package persistence

import (
	"context"
	"log/slog"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/gameserver/game"
)

func TestSaver_SaveAll(t *testing.T) {
	store := storage.NewMemoryPlayerStore()
	world := game.NewWorld()
	world.AddEntity(&game.Entity{
		ID: "u1", Type: "player", X: 10, Y: 20, HP: 80, MaxHP: 100,
	})

	logger := slog.Default()
	saver := NewSaver(store, world, "map_01", time.Second, logger)

	saver.SaveAll()

	state, err := store.LoadPlayer(context.Background(), "u1")
	if err != nil {
		t.Fatalf("LoadPlayer() error: %v", err)
	}
	if state.X != 10 || state.Y != 20 || state.HP != 80 {
		t.Errorf("saved state = %+v, want X=10,Y=20,HP=80", state)
	}
	if state.MapID != "map_01" {
		t.Errorf("MapID = %q, want %q", state.MapID, "map_01")
	}
}
