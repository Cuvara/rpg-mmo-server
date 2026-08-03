package combat

import "github.com/duycuong/rpg-mmo/gameserver/game"

// HandleDeath marks an entity as dead if HP <= 0.
// Returns true if the entity died this call.
func HandleDeath(entity *game.Entity) bool {
	if entity.Dead || entity.HP > 0 {
		return false
	}
	entity.Dead = true
	entity.HP = 0
	return true
}
