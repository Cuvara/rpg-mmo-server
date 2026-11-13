// Package gameid validates the identity formats defined by ADR-33's Terms and
// game-qualified server-id invariant (D2, Option S1).
package gameid

import (
	"strings"

	"github.com/duycuong/rpg-mmo/shared/constants"
)

// ValidGameID reports whether id matches ^[a-z][a-z0-9]{1,23}$ using ASCII bytes.
func ValidGameID(id string) bool {
	if len(id) < 2 || len(id) > 24 || id[0] < 'a' || id[0] > 'z' {
		return false
	}
	for i := 1; i < len(id); i++ {
		c := id[i]
		if !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9') {
			return false
		}
	}
	return true
}

// ValidMapID reports whether id matches ^[A-Za-z0-9_-]{1,64}$ using ASCII bytes.
func ValidMapID(id string) bool {
	if len(id) < 1 || len(id) > 64 {
		return false
	}
	for i := 0; i < len(id); i++ {
		c := id[i]
		if !(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' ||
			c >= '0' && c <= '9' || c == '_' || c == '-') {
			return false
		}
	}
	return true
}

// IsDefaultGame applies ADR-33's rule that absent means the default game.
func IsDefaultGame(game string) bool {
	return game == "" || game == constants.DefaultGameID
}

// ValidServerID preserves every non-empty id for the default game. Other games
// require a valid game id and a <game>- prefix followed by a non-empty suffix;
// the suffix has no additional format restrictions.
func ValidServerID(game, id string) bool {
	if game != "" && !ValidGameID(game) {
		return false
	}
	if IsDefaultGame(game) {
		return id != ""
	}
	return strings.HasPrefix(id, game+"-") && len(id) > len(game)+1
}
