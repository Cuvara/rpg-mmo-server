package transfer

import (
	"fmt"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/jwt"
)

// GenerateJoinToken creates a signed join token for the user to present
// to the target game server.
func GenerateJoinToken(userID, serverID, secret string) (string, error) {
	token, err := jwt.SignWithServer(userID, serverID, secret, constants.JoinTokenTTL)
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
