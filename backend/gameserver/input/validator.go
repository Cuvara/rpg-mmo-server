package input

import (
	"math"
	"time"

	"github.com/duycuong/rpg-mmo/shared/errors"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/gameserver/game"
)

const (
	maxMovePerTick = float32(5.0)
	attackRange    = float32(3.0)
)

// ValidateMove checks if a movement input is valid (not too fast).
func ValidateMove(entity *game.Entity, input messages.InputMessage) error {
	dist := float32(math.Sqrt(float64(input.MoveX*input.MoveX + input.MoveY*input.MoveY)))
	limit := maxMovePerTick * entity.Speed
	if limit <= 0 {
		limit = maxMovePerTick
	}
	if dist > limit {
		return errors.Newf(errors.ErrSpeedHack, "move %.2f exceeds limit %.2f", dist, limit)
	}
	return nil
}

// ValidateAttack checks range and cooldown for an attack.
func ValidateAttack(attacker, target *game.Entity, now time.Time) error {
	if target == nil {
		return errors.New(errors.ErrNotFound, "attack target not found")
	}
	if target.Dead {
		return errors.New(errors.ErrInvalidInput, "target is dead")
	}
	dist := game.Distance(attacker.X, attacker.Y, target.X, target.Y)
	if dist > attackRange {
		return errors.Newf(errors.ErrOutOfRange, "target at %.2f, max range %.2f", dist, attackRange)
	}
	if now.Before(attacker.CooldownUntil) {
		return errors.New(errors.ErrCooldown, "attack on cooldown")
	}
	return nil
}
