package transfer

import (
	"fmt"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/jwt"
)

// GenerateJoinToken creates a signed join token for the user to present
// to the target game server.
//
// The secret here is the JOIN_TOKEN_SECRET keyring, which is deliberately NOT
// the JWT_SECRET used for client auth: a join token is presented to a game
// server pod, so a compromised pod must not be able to forge Nakama-issued
// auth tokens. See backend/docs/ARCHITECTURE-DECISIONS.md.
func GenerateJoinToken(userID, serverID, secret string) (string, error) {
	token, err := jwt.SignWithServer(userID, serverID, secret, constants.JoinTokenTTL)
	if err != nil {
		return "", fmt.Errorf("generate join token: %w", err)
	}
	return token, nil
}

// GenerateJoinTokenKeyring signs a join token with the keyring's current
// (first) secret.
func GenerateJoinTokenKeyring(userID, serverID string, keys jwt.Keyring) (string, error) {
	token, err := keys.SignWithServer(userID, serverID, constants.JoinTokenTTL)
	if err != nil {
		return "", fmt.Errorf("generate join token: %w", err)
	}
	return token, nil
}

// ValidateJoinToken verifies a join token and extracts the userID and serverID.
func ValidateJoinToken(token, secret string) (userID, serverID string, err error) {
	claims, err := jwt.Verify(token, secret)
	if err != nil {
		return "", "", fmt.Errorf("validate join token: %w", err)
	}
	return claims.UserID, claims.ServerID, nil
}

// ValidateJoinTokenKeyring verifies a join token against every secret in the
// keyring, so a rotation does not invalidate tokens already handed to clients.
func ValidateJoinTokenKeyring(token string, keys jwt.Keyring) (userID, serverID string, err error) {
	claims, err := keys.Verify(token)
	if err != nil {
		return "", "", fmt.Errorf("validate join token: %w", err)
	}
	return claims.UserID, claims.ServerID, nil
}
