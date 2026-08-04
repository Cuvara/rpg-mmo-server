package input

import (
	"log/slog"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/gameserver/game"
)

func TestProcessInput_TracksLastInputTick(t *testing.T) {
	tests := []struct {
		name  string
		ticks []uint64
		want  uint64
	}{
		{name: "increasing", ticks: []uint64{1, 2, 3}, want: 3},
		{name: "out of order ignored", ticks: []uint64{5, 2, 4}, want: 5},
		{name: "zero tick", ticks: []uint64{0}, want: 0},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			world := game.NewWorld()
			world.AddEntity(&game.Entity{ID: "p1", Type: "player", HP: 100, MaxHP: 100, Speed: 1})
			h := NewHandler(world, slog.Default())

			for _, tick := range tc.ticks {
				h.ProcessInput("p1", messages.InputMessage{Tick: tick, MoveX: 0.1})
			}
			if got := world.LastInputTick("p1"); got != tc.want {
				t.Errorf("LastInputTick = %d, want %d", got, tc.want)
			}
		})
	}
}

func TestProcessInput_DeathHookFires(t *testing.T) {
	world := game.NewWorld()
	world.AddEntity(&game.Entity{ID: "p1", Type: "player", X: 0, Y: 0, HP: 100, MaxHP: 100, Attack: 100, Defense: 5, Speed: 1})
	world.AddEntity(&game.Entity{ID: "m1", Type: "mob", X: 1, Y: 0, HP: 5, MaxHP: 5, Attack: 1, Defense: 1})

	h := NewHandler(world, slog.Default())
	var victimID, killerID string
	h.SetDeathHandler(func(victim, killer *game.Entity) {
		victimID = victim.ID
		if killer != nil {
			killerID = killer.ID
		}
	})

	h.ProcessInput("p1", messages.InputMessage{Tick: 1, AttackTargetID: "m1"})

	if victimID != "m1" || killerID != "p1" {
		t.Errorf("death hook got victim=%q killer=%q, want m1/p1", victimID, killerID)
	}
}
