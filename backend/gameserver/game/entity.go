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
	// LastInputTick is the highest client-supplied tick number accepted for this
	// entity. Used for input acknowledgement / client-side reconciliation.
	// Not yet carried on the wire: messages.SnapshotMessage has no ack field.
	LastInputTick uint64
}
