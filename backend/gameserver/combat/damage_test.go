package combat

import (
	"testing"

	"github.com/duycuong/rpg-mmo/gameserver/game"
)

func TestCalculateDamage(t *testing.T) {
	tests := []struct {
		name    string
		atk     int
		def     int
		wantDmg int
	}{
		{"normal", 20, 5, 15},
		{"high defense", 10, 10, 1},
		{"over defense", 5, 20, 1},
		{"zero defense", 10, 0, 10},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			attacker := &game.Entity{Attack: tt.atk}
			defender := &game.Entity{Defense: tt.def}
			dmg := CalculateDamage(attacker, defender)
			if dmg != tt.wantDmg {
				t.Errorf("CalculateDamage() = %d, want %d", dmg, tt.wantDmg)
			}
		})
	}
}

func TestHandleDeath(t *testing.T) {
	e := &game.Entity{HP: 0, Dead: false}
	died := HandleDeath(e)
	if !died {
		t.Error("HandleDeath() should return true for HP=0")
	}
	if !e.Dead {
		t.Error("entity should be marked dead")
	}

	died = HandleDeath(e)
	if died {
		t.Error("HandleDeath() should return false if already dead")
	}
}
