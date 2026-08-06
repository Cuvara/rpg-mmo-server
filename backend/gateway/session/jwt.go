package session

import (
	"fmt"

	"github.com/duycuong/rpg-mmo/shared/jwt"
)

// VerifyClientJWT validates a client auth token against a single secret and
// returns the user ID.
//
// Deprecated in favour of VerifyClientJWTKeyring, which supports secret
// rotation. Kept because it is the shape every existing test and caller uses,
// and because a single-secret deployment is still valid.
func VerifyClientJWT(token, secret string) (string, error) {
	claims, err := jwt.Verify(token, secret)
	if err != nil {
		return "", err
	}
	return claims.UserID, nil
}

// VerifyClientJWTKeyring validates a client auth token against every secret in
// the keyring (current first, then the previous ones kept for the rotation
// window) and returns the user ID.
func VerifyClientJWTKeyring(token string, keys jwt.Keyring) (string, error) {
	claims, err := keys.Verify(token)
	if err != nil {
		return "", fmt.Errorf("verify client jwt: %w", err)
	}
	return claims.UserID, nil
}
