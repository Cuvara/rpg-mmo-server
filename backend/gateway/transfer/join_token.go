package transfer

import (
	"fmt"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/gameid"
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
// (first) secret. serverID is mandatory: a join token without a target server
// could be replayed against any game-server pod.
func GenerateJoinTokenKeyring(userID, serverID string, keys jwt.Keyring) (string, error) {
	return GenerateJoinTokenCharacter(userID, serverID, "", constants.DefaultGameID, keys)
}

// GenerateJoinTokenCharacter is GenerateJoinTokenKeyring with the character the
// token plays (`cid`, ADR-31). characterID is copied from the gateway token
// Nakama minted after checking ownership; the gateway never chooses it. Empty
// omits the claim, which the game server reads as the default character.
// gameID is copied from the gateway session; the caller supplies that game's
// join keyring. The default game omits gid to preserve its existing claim set.
func GenerateJoinTokenCharacter(userID, serverID, characterID, gameID string, keys jwt.Keyring) (string, error) {
	if serverID == "" {
		return "", fmt.Errorf("generate join token: serverID is required")
	}
	token, err := keys.SignWithGame(userID, serverID, characterID, gameID, constants.JoinTokenTTL)
	if err != nil {
		return "", fmt.Errorf("generate join token: %w", err)
	}
	return token, nil
}

// JoinKeyrings holds the join-token signing keys for each configured game.
// A missing non-default game never falls back to the cuvara keyring.
// The zero value has no keys and refuses every game.
type JoinKeyrings struct {
	games map[string]jwt.Keyring
}

// NewJoinKeyrings copies the per-game keyrings and installs defaultKeys for
// cuvara. defaultKeys remains authoritative if games also contains cuvara.
// Missing games are allowed so entry can be refused until their keys exist.
func NewJoinKeyrings(defaultKeys jwt.Keyring, games map[string]jwt.Keyring) (JoinKeyrings, error) {
	if !defaultKeys.Valid() {
		return JoinKeyrings{}, fmt.Errorf("join keyrings: default game has no keys")
	}
	keys := make(map[string]jwt.Keyring, len(games)+1)
	for game, ring := range games {
		if !gameid.ValidGameID(game) {
			return JoinKeyrings{}, fmt.Errorf("join keyrings: invalid game ID %q", game)
		}
		if !ring.Valid() {
			return JoinKeyrings{}, fmt.Errorf("join keyrings: game %s has no keys", game)
		}
		keys[game] = ring
	}
	keys[constants.DefaultGameID] = defaultKeys
	return JoinKeyrings{games: keys}, nil
}

// For returns only the requested game's keyring. Empty means cuvara.
func (k JoinKeyrings) For(gameID string) (jwt.Keyring, bool) {
	keys, ok := k.games[constants.GameOrDefault(gameID)]
	return keys, ok
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
