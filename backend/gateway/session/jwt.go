package session

import (
	"github.com/duycuong/rpg-mmo/shared/jwt"
)

// VerifyClientJWT validates a client auth token and returns the user ID.
func VerifyClientJWT(token, secret string) (string, error) {
	claims, err := jwt.Verify(token, secret)
	if err != nil {
		return "", err
	}
	return claims.UserID, nil
}
