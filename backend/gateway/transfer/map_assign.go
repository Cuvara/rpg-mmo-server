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
	// JTI is the join token's jti claim, extracted so the gateway can record
	// it on the session without re-parsing the token. The duplicate-login kick
	// uses it to name exactly which game-server connection a supersede event
	// targets (session.SessionData.JoinTokenJTI).
	JTI string
	SessionKey []byte
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
//
// Order matters: the token is minted only *after* FindServer has resolved a
// server that is actually registered — for an already-live server that is
// immediate, and for a freshly allocated one FindServer blocks until the pod
// self-registers (registry.ErrServerStarting if it never does). Join tokens are
// single-use, pinned to one server id and live only constants.JoinTokenTTL, so
// minting one for a server that is still booting would burn the client's only
// token on an address that is not answering. Every field below therefore comes
// from the entry FindServer returned, never from an allocation response.
func AssignMapKeyring(ctx context.Context, userID, mapID string, reg *registry.RegistryService, joinKeys jwt.Keyring) (AssignResult, error) {
	srv, err := reg.FindServer(ctx, mapID)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign map: %w", err)
	}

	token, err := GenerateJoinTokenKeyring(userID, srv.ServerID, joinKeys)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign map: %w", err)
	}

	// Read the jti back out of the token we just minted (SignWithServer
	// generates it internally). Verifying our own fresh token cannot fail
	// unless the keyring is broken, in which case the token is unusable anyway.
	claims, err := joinKeys.Verify(token)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign map: read back jti: %w", err)
	}

	result := AssignResult{
		ServerID:   srv.ServerID,
		ServerAddr: srv.Addr,
		JoinToken:  token,
		Transport:  srv.Transport,
		JTI:        claims.Jti,
	}
	if srv.Transport == "kcp" {
		sk, err := GenerateSessionKey()
		if err != nil {
			return AssignResult{}, fmt.Errorf("assign map: %w", err)
		}
		result.SessionKey = sk
	}
	return result, nil
}
