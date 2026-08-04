package server

import (
	"log/slog"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/gameserver/game"
	"github.com/duycuong/rpg-mmo/gameserver/input"
)

func TestTickRunner_TickOnce_Movement(t *testing.T) {
	world := game.NewWorld()
	world.AddEntity(&game.Entity{
		ID: "p1", Type: "player", X: 0, Y: 0, HP: 100, MaxHP: 100, Speed: 1.0, Attack: 10, Defense: 5,
	})

	logger := slog.Default()
	handler := input.NewHandler(world, logger)
	conns := NewConnectionManager()
	tick := NewTickRunner(world, handler, conns, 10, logger)

	world.PushInput("p1", messages.InputMessage{MoveX: 2.0, MoveY: 1.0})
	tick.TickOnce()

	e := world.GetEntity("p1")
	if e.X != 2.0 || e.Y != 1.0 {
		t.Errorf("after tick, position = (%.1f, %.1f), want (2.0, 1.0)", e.X, e.Y)
	}

	if tick.CurrentTick() != 1 {
		t.Errorf("CurrentTick() = %d, want 1", tick.CurrentTick())
	}
}

func TestTickRunner_TickOnce_Attack(t *testing.T) {
	world := game.NewWorld()
	world.AddEntity(&game.Entity{
		ID: "p1", Type: "player", X: 0, Y: 0, HP: 100, MaxHP: 100, Speed: 1.0, Attack: 20, Defense: 5,
	})
	world.AddEntity(&game.Entity{
		ID: "mob1", Type: "mob", X: 1, Y: 0, HP: 50, MaxHP: 50, Attack: 5, Defense: 3,
	})

	logger := slog.Default()
	handler := input.NewHandler(world, logger)
	conns := NewConnectionManager()
	tick := NewTickRunner(world, handler, conns, 10, logger)

	world.PushInput("p1", messages.InputMessage{AttackTargetID: "mob1"})
	tick.TickOnce()

	mob := world.GetEntity("mob1")
	expectedHP := 50 - 17 // 20 - 3 = 17 damage
	if mob.HP != expectedHP {
		t.Errorf("mob HP = %d, want %d", mob.HP, expectedHP)
	}
}
