package input

import (
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/errors"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/gameserver/game"
)

func TestValidateMove_OK(t *testing.T) {
	e := &game.Entity{Speed: 1.0}
	input := messages.InputMessage{MoveX: 2.0, MoveY: 2.0}
	if err := ValidateMove(e, input); err != nil {
		t.Errorf("ValidateMove() unexpected error: %v", err)
	}
}

func TestValidateMove_TooFast(t *testing.T) {
	e := &game.Entity{Speed: 1.0}
	input := messages.InputMessage{MoveX: 10.0, MoveY: 10.0}
	err := ValidateMove(e, input)
	if err == nil {
		t.Fatal("ValidateMove() should reject too-fast movement")
	}
	if !errors.Is(err, errors.ErrSpeedHack) {
		t.Errorf("error code = %v, want ErrSpeedHack", err)
	}
}

func TestValidateAttack_OK(t *testing.T) {
	attacker := &game.Entity{X: 0, Y: 0}
	target := &game.Entity{X: 2, Y: 0, HP: 50}
	if err := ValidateAttack(attacker, target, time.Now()); err != nil {
		t.Errorf("ValidateAttack() unexpected error: %v", err)
	}
}

func TestValidateAttack_OutOfRange(t *testing.T) {
	attacker := &game.Entity{X: 0, Y: 0}
	target := &game.Entity{X: 10, Y: 10, HP: 50}
	err := ValidateAttack(attacker, target, time.Now())
	if !errors.Is(err, errors.ErrOutOfRange) {
		t.Errorf("expected ErrOutOfRange, got %v", err)
	}
}

func TestValidateAttack_Cooldown(t *testing.T) {
	attacker := &game.Entity{X: 0, Y: 0, CooldownUntil: time.Now().Add(time.Second)}
	target := &game.Entity{X: 1, Y: 0, HP: 50}
	err := ValidateAttack(attacker, target, time.Now())
	if !errors.Is(err, errors.ErrCooldown) {
		t.Errorf("expected ErrCooldown, got %v", err)
	}
}

func TestValidateAttack_DeadTarget(t *testing.T) {
	attacker := &game.Entity{X: 0, Y: 0}
	target := &game.Entity{X: 1, Y: 0, Dead: true}
	err := ValidateAttack(attacker, target, time.Now())
	if !errors.Is(err, errors.ErrInvalidInput) {
		t.Errorf("expected ErrInvalidInput, got %v", err)
	}
}

func TestValidateAttack_NilTarget(t *testing.T) {
	attacker := &game.Entity{X: 0, Y: 0}
	err := ValidateAttack(attacker, nil, time.Now())
	if !errors.Is(err, errors.ErrNotFound) {
		t.Errorf("expected ErrNotFound, got %v", err)
	}
}
