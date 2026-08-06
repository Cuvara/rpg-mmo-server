package auth

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"

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
}

// GatewayTokenResponse is the payload returned by the gateway_token RPC.
type GatewayTokenResponse struct {
	Token     string `json:"token"`
	UserID    string `json:"user_id"`
	ExpiresIn int64  `json:"expires_in"`
}

// IssueGatewayToken signs a realtime token for userID using the shared HS256
// JWT implementation, so the Gateway can verify it locally without a roundtrip.
func IssueGatewayToken(userID, serverID string, cfg Config) (GatewayTokenResponse, error) {
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
	token, err := keys.SignWithServer(userID, serverID, cfg.TokenTTL)
	if err != nil {
		return GatewayTokenResponse{}, fmt.Errorf("issue gateway token: %w", err)
	}
	return GatewayTokenResponse{
		Token:     token,
		UserID:    userID,
		ExpiresIn: int64(cfg.TokenTTL.Seconds()),
	}, nil
}

// GatewayTokenRPC is the Nakama RPC handler for RPCGatewayToken. It requires an
// authenticated caller and returns a JWT accepted by the Gateway.
func GatewayTokenRPC(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, payload string) (string, error) {
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

	resp, err := IssueGatewayToken(userID, req.ServerID, LoadConfig(ctx))
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
