package transfer

import (
	"context"
	"fmt"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/sealed"
	"github.com/duycuong/rpg-mmo/shared/storage"
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

	// ServerPublicKey is the target server's Ed25519 identity public key, 32 raw
	// bytes decoded from its registry entry (ADR-25). The gateway forwards it
	// verbatim in EnterWorldResponse so the client can check the signature the
	// game server puts in SealedServerHello.
	//
	// EMPTY IS NOT AN ERROR and must never fail an assignment. A server older
	// than ADR-25 publishes no key, and so does one whose entry carries a
	// malformed value; in both cases the join proceeds and a client that
	// requires identity refuses it at the sealed handshake, which is where that
	// decision belongs. Failing the assignment instead would take a whole map
	// offline for a registry-encoding bug.
	ServerPublicKey []byte
}

// identityKeyOf decodes a registry entry's identity key for delivery to the
// client, returning nil for anything it cannot use.
//
// The error is deliberately swallowed HERE rather than propagated. See
// AssignResult.ServerPublicKey: the caller's alternative to an empty key is a
// failed join, and a client that cares refuses one hop later with a message
// that actually names encryption. The gateway logs the absence on the
// enter-world line, so it is visible without being fatal.
func identityKeyOf(info storage.ServerInfo) []byte {
	key, err := sealed.DecodeIdentityKey(info.IdentityKey)
	if err != nil {
		return nil
	}
	return key
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

	return AssignResult{
		ServerID:        srv.ServerID,
		ServerAddr:      srv.Addr,
		JoinToken:       token,
		Transport:       srv.Transport,
		JTI:             claims.Jti,
		ServerPublicKey: identityKeyOf(srv),
	}, nil
}
