// Package gamectx resolves game identity and scoped Nakama data names under
// ADR-33 D1 Option B and D4 Data under B.
package gamectx

import (
	"context"
	"fmt"
	"os"
	"strings"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/gameid"
	"github.com/heroiclabs/nakama-common/runtime"
)

// VarGame is the Nakama session variable carrying game identity in ADR-33 D1.
const VarGame = "game"

// EnvGames is the runtime environment allow-list defined by ADR-33 D1.
const EnvGames = "GAMES"

var (
	// ErrGameNotAllowed rejects invalid or unconfigured game IDs under ADR-33 D1.
	ErrGameNotAllowed = runtime.NewError("game not allowed", 3)
	// ErrGameMismatch rejects session game overrides under ADR-33 D1.
	ErrGameMismatch = runtime.NewError("game_id does not match the session game", 7)
	// ErrGamesConfig reports an invalid server allow-list under ADR-33 D1.
	ErrGamesConfig = runtime.NewError("internal error", 13)
)

// ParseAllowList parses ADR-33 D1's comma-separated GAMES allow-list, preserving
// order and dropping duplicates. An absent value allows only the default game;
// a configured list allows exactly its entries, with no implicit default game.
func ParseAllowList(raw string) ([]string, error) {
	if strings.TrimSpace(raw) == "" {
		return []string{constants.DefaultGameID}, nil
	}

	games := make([]string, 0)
	seen := make(map[string]bool)
	for _, entry := range strings.Split(raw, ",") {
		game := strings.TrimSpace(entry)
		if !gameid.ValidGameID(game) {
			return nil, fmt.Errorf("%w: invalid %s entry %q", ErrGamesConfig, EnvGames, game)
		}
		if !seen[game] {
			games = append(games, game)
			seen[game] = true
		}
	}
	return games, nil
}

// AllowList reads ADR-33 D1's GAMES from Nakama's runtime environment, falling
// back to the process environment only when the runtime map has no GAMES key.
// Configuration errors include the invalid entry for server-side logging.
func AllowList(ctx context.Context) ([]string, error) {
	if env, ok := ctx.Value(runtime.RUNTIME_CTX_ENV).(map[string]string); ok {
		if raw, exists := env[EnvGames]; exists {
			return ParseAllowList(raw)
		}
	}
	return ParseAllowList(os.Getenv(EnvGames))
}

// Allowed reports whether game is in ADR-33 D1's exact allow-list. It returns
// configuration errors to the caller rather than treating them as a denial.
func Allowed(ctx context.Context, game string) (bool, error) {
	games, err := AllowList(ctx)
	if err != nil {
		return false, err
	}
	for _, allowed := range games {
		if game == allowed {
			return true, nil
		}
	}
	return false, nil
}

// GameFromVars validates ADR-33 D1's game session variable for authentication
// and refresh hooks. Missing or empty variables select the default game.
func GameFromVars(ctx context.Context, vars map[string]string) (string, error) {
	return validateGame(ctx, constants.GameOrDefault(vars[VarGame]))
}

// GameFromContext resolves ADR-33 D1's session game, refusing a differing
// payload game_id. Without a session, the HTTP-key caller's payload selects
// the game and context session variables are ignored.
func GameFromContext(ctx context.Context, payloadGameID string) (string, error) {
	game := constants.GameOrDefault(payloadGameID)
	if userID, _ := ctx.Value(runtime.RUNTIME_CTX_USER_ID).(string); userID != "" {
		vars, _ := ctx.Value(runtime.RUNTIME_CTX_VARS).(map[string]string)
		game = constants.GameOrDefault(vars[VarGame])
		if payloadGameID != "" && constants.GameOrDefault(payloadGameID) != game {
			return "", ErrGameMismatch
		}
	}
	return validateGame(ctx, game)
}

func validateGame(ctx context.Context, game string) (string, error) {
	allowed, err := Allowed(ctx, game)
	if err != nil {
		return "", ErrGamesConfig
	}
	if !gameid.ValidGameID(game) || !allowed {
		return "", ErrGameNotAllowed
	}
	return game, nil
}

// Scoped resolves data names under ADR-33 D4 Data under B: the default game's
// existing names stay bare, while every other game uses a game prefix.
func Scoped(game, name string) string {
	if gameid.IsDefaultGame(game) {
		return name
	}
	return game + "." + name
}
