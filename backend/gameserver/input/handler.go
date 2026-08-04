package input

import (
	"log/slog"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/gameserver/combat"
	"github.com/duycuong/rpg-mmo/gameserver/game"
)

const attackCooldown = 500 * time.Millisecond

// DeathFunc is called when an entity dies. killer may be nil (environment kill).
// It runs inside the tick loop, so implementations must not block.
type DeathFunc func(victim, killer *game.Entity)

// Handler processes incoming input messages against the world.
type Handler struct {
	world   *game.World
	logger  *slog.Logger
	onDeath DeathFunc
}

// NewHandler creates an input handler.
func NewHandler(world *game.World, logger *slog.Logger) *Handler {
	return &Handler{world: world, logger: logger}
}

// SetDeathHandler registers a callback invoked whenever an entity dies as a
// result of processed input. Passing nil disables the hook.
func (h *Handler) SetDeathHandler(fn DeathFunc) {
	h.onDeath = fn
}

// ProcessInput validates and applies a player's input to the world. It takes
// the world write lock for the whole operation; batch callers (the tick loop)
// should prefer ProcessInputLocked inside a single World.Update.
func (h *Handler) ProcessInput(userID string, input messages.InputMessage) {
	h.world.Update(func(get func(string) *game.Entity) {
		h.ProcessInputLocked(get, userID, input)
	})
}

// ProcessInputLocked applies one input using the unlocked getter provided by
// World.Update. Must only be called from inside a World.Update closure.
func (h *Handler) ProcessInputLocked(get func(string) *game.Entity, userID string, input messages.InputMessage) {
	entity := get(userID)
	if entity == nil || entity.Dead {
		return
	}

	// Track the client tick for acknowledgement. Monotonic: stale/replayed
	// frames never lower the acked tick.
	if input.Tick > entity.LastInputTick {
		entity.LastInputTick = input.Tick
	}

	// Movement
	if input.MoveX != 0 || input.MoveY != 0 {
		if err := ValidateMove(entity, input); err != nil {
			h.logger.Debug("move rejected", "user", userID, "err", err)
			return
		}
		entity.X += input.MoveX
		entity.Y += input.MoveY
	}

	// Attack
	if input.AttackTargetID != "" {
		target := get(input.AttackTargetID)
		now := time.Now()
		if err := ValidateAttack(entity, target, now); err != nil {
			h.logger.Debug("attack rejected", "user", userID, "err", err)
			return
		}
		dmg := combat.CalculateDamage(entity, target)
		target.HP -= dmg
		entity.CooldownUntil = now.Add(attackCooldown)
		h.logger.Debug("attack hit", "attacker", userID, "target", input.AttackTargetID, "dmg", dmg, "targetHP", target.HP)

		if combat.HandleDeath(target) && h.onDeath != nil {
			h.onDeath(target, entity)
		}
	}
}
