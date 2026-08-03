package input

import (
	"log/slog"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/gameserver/combat"
	"github.com/duycuong/rpg-mmo/gameserver/game"
)

const attackCooldown = 500 * time.Millisecond

// Handler processes incoming input messages against the world.
type Handler struct {
	world  *game.World
	logger *slog.Logger
}

// NewHandler creates an input handler.
func NewHandler(world *game.World, logger *slog.Logger) *Handler {
	return &Handler{world: world, logger: logger}
}

// ProcessInput validates and applies a player's input to the world.
func (h *Handler) ProcessInput(userID string, input messages.InputMessage) {
	entity := h.world.GetEntity(userID)
	if entity == nil || entity.Dead {
		return
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
		target := h.world.GetEntity(input.AttackTargetID)
		now := time.Now()
		if err := ValidateAttack(entity, target, now); err != nil {
			h.logger.Debug("attack rejected", "user", userID, "err", err)
			return
		}
		dmg := combat.CalculateDamage(entity, target)
		target.HP -= dmg
		entity.CooldownUntil = now.Add(attackCooldown)
		h.logger.Debug("attack hit", "attacker", userID, "target", input.AttackTargetID, "dmg", dmg, "targetHP", target.HP)

		combat.HandleDeath(target)
	}
}
