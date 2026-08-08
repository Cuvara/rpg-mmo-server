package transfer

import (
	"context"

	gameerrors "github.com/duycuong/rpg-mmo/shared/errors"
)

// Map transfer (MsgTransferMap / MsgTransferMapResp, types 13/14) is
// client-driven and handled entirely by the game server: the client sends
// MsgTransferMap to its current game server, which saves state, responds,
// removes the entity, and closes the connection. The client then sends a
// fresh MsgEnterWorld to the gateway (the existing flow), and the gateway
// assigns a new map server and mints a join token. No gateway code change
// is required for map transfer.
//
// Types 11/12 (Ping/Pong) and 15 (Kick) are reserved for protocol and
// session workers respectively.

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
