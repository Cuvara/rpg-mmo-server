package game

import "time"

// Entity represents any object in the game world.
type Entity struct {
	ID            string
	Type          string // "player", "npc", "mob"
	X, Y          float32
	HP, MaxHP     int
	Attack        int
	Defense       int
	Speed         float32
	Dead          bool
	CooldownUntil time.Time
}
