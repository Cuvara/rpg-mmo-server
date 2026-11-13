package session

import (
	"errors"
	"fmt"
	"strings"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/gameid"
)

// EnvGames names the configured game allow-list.
const EnvGames = "GAMES"

// ErrInvalidGames identifies an invalid game allow-list.
var ErrInvalidGames = errors.New("invalid games configuration")

// Games is an ordered set of configured game IDs. Its zero value allows the default game.
type Games struct {
	ids []string
	set map[string]bool
}

// DefaultGames allows only the default game.
func DefaultGames() Games {
	return Games{ids: []string{constants.DefaultGameID}, set: map[string]bool{constants.DefaultGameID: true}}
}

// IDs returns the configured IDs in order as a copy.
func (g Games) IDs() []string {
	if g.set == nil {
		g = DefaultGames()
	}
	return append([]string(nil), g.ids...)
}

// Allowed reports whether game is an exact member of the configured set.
func (g Games) Allowed(game string) bool {
	if g.set == nil {
		return game == constants.DefaultGameID
	}
	return g.set[game]
}

// ParseGames parses the comma-separated game allow-list.
func ParseGames(raw string) (Games, error) {
	if strings.TrimSpace(raw) == "" {
		return DefaultGames(), nil
	}
	games := Games{ids: make([]string, 0), set: make(map[string]bool)}
	for _, entry := range strings.Split(raw, ",") {
		game := strings.TrimSpace(entry)
		if !gameid.ValidGameID(game) {
			return Games{}, fmt.Errorf("%w: invalid %s entry %q", ErrInvalidGames, EnvGames, game)
		}
		if !games.set[game] {
			games.ids = append(games.ids, game)
			games.set[game] = true
		}
	}
	return games, nil
}
