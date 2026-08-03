package combat

import "github.com/duycuong/rpg-mmo/gameserver/game"

// CalculateDamage computes damage dealt from attacker to defender.
// Minimum damage is 1.
func CalculateDamage(attacker, defender *game.Entity) int {
	dmg := attacker.Attack - defender.Defense
	if dmg < 1 {
		dmg = 1
	}
	return dmg
}
