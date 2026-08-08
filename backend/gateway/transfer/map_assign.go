package transfer

import (
	"context"
	"fmt"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/shared/jwt"
)

// AssignResult holds the result of a map assignment.
//
// Transport is the realtime transport the target game server speaks, taken
// from its registry entry. Empty means TCP (backward compatible with entries
// written before the transport field existed).
type AssignResult struct {
	ServerID   string
	ServerAddr string
	JoinToken  string
	Transport  string
}

// AssignMap finds an available server for the given map and generates a join
// token signed with the join-token secret.
func AssignMap(ctx context.Context, userID, mapID string, reg *registry.RegistryService, joinTokenSecret string) (AssignResult, error) {
	keys, err := jwt.ParseKeyring(joinTokenSecret)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign map: %w", err)
	}
	return AssignMapKeyring(ctx, userID, mapID, reg, keys)
}

// AssignMapKeyring is AssignMap with a pre-parsed join-token keyring, which is
// what the gateway uses on the hot path so the keyring is parsed once at
// start-up instead of on every EnterWorld.
func AssignMapKeyring(ctx context.Context, userID, mapID string, reg *registry.RegistryService, joinKeys jwt.Keyring) (AssignResult, error) {
	srv, err := reg.FindServer(ctx, mapID)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign map: %w", err)
	}

	token, err := GenerateJoinTokenKeyring(userID, srv.ServerID, joinKeys)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign map: %w", err)
	}

	return AssignResult{
		ServerID:   srv.ServerID,
		ServerAddr: srv.Addr,
		JoinToken:  token,
		Transport:  srv.Transport,
	}, nil
}
