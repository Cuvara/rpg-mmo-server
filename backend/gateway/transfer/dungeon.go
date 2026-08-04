package transfer

import (
	"context"

	gameerrors "github.com/duycuong/rpg-mmo/shared/errors"
)

// DungeonTransfer handles transferring a party to a dungeon instance.
type DungeonTransfer interface {
	// Transfer moves a party to a dungeon instance.
	Transfer(ctx context.Context, partyID, dungeonID string) (AssignResult, error)
}

// StubDungeonTransfer is a placeholder that always returns ErrNotImplemented.
type StubDungeonTransfer struct{}

// Transfer returns ErrNotImplemented.
func (s *StubDungeonTransfer) Transfer(_ context.Context, _, _ string) (AssignResult, error) {
	return AssignResult{}, gameerrors.New(gameerrors.ErrNotImplemented, "dungeon transfer not implemented")
}
