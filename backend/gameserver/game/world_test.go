package game

import (
	"testing"

	"github.com/duycuong/rpg-mmo/shared/messages"
)

func TestWorld_AddRemoveEntity(t *testing.T) {
	w := NewWorld()

	e := &Entity{ID: "p1", Type: "player", X: 10, Y: 20, HP: 100, MaxHP: 100}
	w.AddEntity(e)

	if w.EntityCount() != 1 {
		t.Errorf("EntityCount() = %d, want 1", w.EntityCount())
	}

	got := w.GetEntity("p1")
	if got == nil || got.X != 10 {
		t.Errorf("GetEntity() = %+v, want X=10", got)
	}

	w.RemoveEntity("p1")
	if w.EntityCount() != 0 {
		t.Errorf("after remove, EntityCount() = %d, want 0", w.EntityCount())
	}
}

func TestWorld_GetEntitiesInRange(t *testing.T) {
	w := NewWorld()
	w.AddEntity(&Entity{ID: "a", X: 0, Y: 0})
	w.AddEntity(&Entity{ID: "b", X: 5, Y: 0})
	w.AddEntity(&Entity{ID: "c", X: 100, Y: 100})

	nearby := w.GetEntitiesInRange(0, 0, 10)
	if len(nearby) != 2 {
		t.Errorf("GetEntitiesInRange() = %d entities, want 2", len(nearby))
	}
}

func TestWorld_PushDrainInputs(t *testing.T) {
	w := NewWorld()

	w.PushInput("u1", messages.InputMessage{MoveX: 1, MoveY: 0})
	w.PushInput("u2", messages.InputMessage{MoveX: 0, MoveY: 1})

	inputs := w.DrainInputs()
	if len(inputs) != 2 {
		t.Fatalf("DrainInputs() = %d, want 2", len(inputs))
	}

	inputs = w.DrainInputs()
	if len(inputs) != 0 {
		t.Errorf("second DrainInputs() = %d, want 0", len(inputs))
	}
}

func TestWorld_PlayerEntities(t *testing.T) {
	w := NewWorld()
	w.AddEntity(&Entity{ID: "p1", Type: "player"})
	w.AddEntity(&Entity{ID: "mob1", Type: "mob"})
	w.AddEntity(&Entity{ID: "p2", Type: "player"})

	players := w.PlayerEntities()
	if len(players) != 2 {
		t.Errorf("PlayerEntities() = %d, want 2", len(players))
	}
}
