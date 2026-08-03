package snapshot

import "github.com/duycuong/rpg-mmo/gameserver/game"

const defaultAOIRadius = float32(50.0)

// GetNearbyEntities returns entities within radius of the center point.
func GetNearbyEntities(world *game.World, cx, cy, radius float32) []*game.Entity {
	if radius <= 0 {
		radius = defaultAOIRadius
	}
	return world.GetEntitiesInRange(cx, cy, radius)
}
