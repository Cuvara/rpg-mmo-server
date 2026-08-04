package transfer

import (
	"context"
	"fmt"

	"github.com/duycuong/rpg-mmo/gateway/registry"
)

// AssignResult holds the result of a map assignment.
//
// Transport is the realtime transport the target game server speaks, taken
// from its registry entry. Empty means TCP (backward compatible with entries
// written before the transport field existed).
type AssignResult struct {
	ServerAddr string
	JoinToken  string
	Transport  string
}

// AssignMap finds an available server for the given map and generates a join token.
func AssignMap(ctx context.Context, userID, mapID string, reg *registry.RegistryService, jwtSecret string) (AssignResult, error) {
	srv, err := reg.FindServer(ctx, mapID)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign map: %w", err)
	}

	token, err := GenerateJoinToken(userID, srv.ServerID, jwtSecret)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign map: %w", err)
	}

	return AssignResult{
		ServerAddr: srv.Addr,
		JoinToken:  token,
		Transport:  srv.Transport,
	}, nil
}
