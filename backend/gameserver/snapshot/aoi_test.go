package snapshot

import (
	"testing"

	"github.com/duycuong/rpg-mmo/gameserver/game"
)

func TestGetNearbyEntities(t *testing.T) {
	w := game.NewWorld()
	w.AddEntity(&game.Entity{ID: "a", X: 0, Y: 0})
	w.AddEntity(&game.Entity{ID: "b", X: 5, Y: 0})
	w.AddEntity(&game.Entity{ID: "c", X: 100, Y: 100})

	nearby := GetNearbyEntities(w, 0, 0, 10)
	if len(nearby) != 2 {
		t.Errorf("GetNearbyEntities() = %d, want 2", len(nearby))
	}
}

func TestGetNearbyEntities_DefaultRadius(t *testing.T) {
	w := game.NewWorld()
	w.AddEntity(&game.Entity{ID: "a", X: 0, Y: 0})
	w.AddEntity(&game.Entity{ID: "b", X: 40, Y: 0})

	nearby := GetNearbyEntities(w, 0, 0, 0)
	if len(nearby) != 2 {
		t.Errorf("with default radius, got %d, want 2", len(nearby))
	}
}
