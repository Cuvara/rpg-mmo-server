package auth

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"

	"github.com/duycuong/rpg-mmo/nakama/character"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/heroiclabs/nakama-common/runtime"
)

// RPCGatewayToken is the RPC id clients call to obtain a realtime session token.
const RPCGatewayToken = "gateway_token"

// GatewayTokenRequest is the (optional) payload of the gateway_token RPC.
type GatewayTokenRequest struct {
	// ServerID optionally pins the token to a specific game server instance.
	// Empty means "any server" and the claim is omitted.
	ServerID string `json:"server_id,omitempty"`

	// CharacterID optionally selects the roster character this realtime
	// session plays (ADR-31). The RPC checks the caller owns it and puts it in
	// the token as the `cid` claim; the gateway copies that claim into the join
	// token. Empty means the account's default character and omits the claim,
	// which is exactly what every protocol 2 client gets.
	CharacterID string `json:"character_id,omitempty"`
}

// GatewayTokenResponse is the payload returned by the gateway_token RPC.
type GatewayTokenResponse struct {
	Token     string `json:"token"`
	UserID    string `json:"user_id"`
	ExpiresIn int64  `json:"expires_in"`
	// CharacterID echoes the `cid` claim the token carries; omitted for the
	// default character.
	CharacterID string `json:"character_id,omitempty"`
}

// IssueGatewayToken signs a realtime token for userID using the shared HS256
// JWT implementation, so the Gateway can verify it locally without a roundtrip.
func IssueGatewayToken(userID, serverID string, cfg Config) (GatewayTokenResponse, error) {
	return IssueGatewayTokenForCharacter(userID, serverID, "", cfg)
}

// IssueGatewayTokenForCharacter is IssueGatewayToken with the `cid` claim
// (ADR-31). It does NOT check ownership: the caller must have done so (see
// GatewayTokenRPC). An empty characterID omits the claim.
func IssueGatewayTokenForCharacter(userID, serverID, characterID string, cfg Config) (GatewayTokenResponse, error) {
	if userID == "" {
		return GatewayTokenResponse{}, fmt.Errorf("issue gateway token: empty user id")
	}
	// JWT_SECRET may be a rotation list ("current,previous"); Nakama is the
	// issuer, so it must sign with the CURRENT secret only. Verifying the whole
	// list is the gateway's job.
	keys, err := jwt.ParseKeyring(cfg.JWTSecret)
	if err != nil {
		return GatewayTokenResponse{}, fmt.Errorf("issue gateway token: %w", err)
	}
	token, err := keys.SignWithCharacter(userID, serverID, characterID, cfg.TokenTTL)
	if err != nil {
		return GatewayTokenResponse{}, fmt.Errorf("issue gateway token: %w", err)
	}
	return GatewayTokenResponse{
		Token:       token,
		UserID:      userID,
		ExpiresIn:   int64(cfg.TokenTTL.Seconds()),
		CharacterID: characterID,
	}, nil
}

// GatewayTokenRPC is the Nakama RPC handler for RPCGatewayToken. It requires an
// authenticated caller and returns a JWT accepted by the Gateway.
func GatewayTokenRPC(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	// A nil NakamaModule must stay a nil character.Store, not a typed nil.
	var roster character.Store
	if nk != nil {
		roster = nk
	}
	return gatewayToken(ctx, logger, roster, payload)
}

// gatewayToken is GatewayTokenRPC over the narrow roster store, so tests can
// drive the ownership check without a full NakamaModule.
func gatewayToken(ctx context.Context, logger runtime.Logger, roster character.Store, payload string) (string, error) {
	userID, ok := ctx.Value(runtime.RUNTIME_CTX_USER_ID).(string)
	if !ok || userID == "" {
		return "", ErrUnauthenticated
	}

	// Rate limit before doing any work, and key on the authenticated user id
	// rather than an IP: the caller is already authenticated here, and a shared
	// carrier NAT would otherwise collapse thousands of players into one bucket.
	if !allowGatewayToken(userID) {
		logger.Warn("gateway_token rate limited: user %s", userID)
		return "", ErrRateLimited
	}

	var req GatewayTokenRequest
	if payload != "" {
		if err := json.Unmarshal([]byte(payload), &req); err != nil {
			return "", ErrInvalidPayload
		}
	}

	// ADR-31: a selected character must be the caller's. Checked here, at the
	// only place the claim is minted, so the gateway and game server can trust
	// `cid` without a roundtrip. "Not yours" and "does not exist" both answer
	// character.ErrCharacterNotFound, so this is not an oracle for other
	// players' character ids.
	if req.CharacterID != "" {
		if roster == nil {
			logger.Error("gateway_token: no storage to check character %s for %s", req.CharacterID, userID)
			return "", ErrInternal
		}
		owns, err := character.Owns(ctx, roster, userID, req.CharacterID)
		if err != nil {
			logger.Error("gateway_token: %v", err)
			return "", ErrInternal
		}
		if !owns {
			logger.Warn("gateway_token: user %s asked for character %s it does not own", userID, req.CharacterID)
			return "", character.ErrCharacterNotFound
		}
	}

	resp, err := IssueGatewayTokenForCharacter(userID, req.ServerID, req.CharacterID, LoadConfig(ctx))
	if err != nil {
		logger.Error("gateway_token: %v", err)
		return "", ErrInternal
	}

	out, err := json.Marshal(resp)
	if err != nil {
		logger.Error("gateway_token marshal: %v", err)
		return "", ErrInternal
	}
	return string(out), nil
}
