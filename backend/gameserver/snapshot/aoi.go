package snapshot

import "github.com/duycuong/rpg-mmo/gameserver/game"

const defaultAOIRadius = float32(50.0)

// GetNearbyEntities returns value copies of entities within radius of the
// center point, taken under the world lock (safe to use without holding it).
func GetNearbyEntities(world *game.World, cx, cy, radius float32) []game.Entity {
	if radius <= 0 {
		radius = defaultAOIRadius
	}
	return world.GetEntitiesInRange(cx, cy, radius)
}
