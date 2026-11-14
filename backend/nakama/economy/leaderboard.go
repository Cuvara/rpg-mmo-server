package economy

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"os"

	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/duycuong/rpg-mmo/shared/gameid"
	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// Leaderboard IDs.
const (
	LeaderboardKillsAllTime = "kills_alltime"
)

// LeaderboardMigrateEnv is the runtime env key (Nakama --runtime.env, falling
// back to the process env) that opts into the destructive migration of a
// pre-existing non-authoritative kills leaderboard. Value: "recreate".
const LeaderboardMigrateEnv = "LEADERBOARD_MIGRATE"

// leaderboardAdmin is the slice of runtime.NakamaModule SetupLeaderboards uses,
// narrow so tests can implement it without mocking the whole module.
type leaderboardAdmin interface {
	LeaderboardsGetId(ctx context.Context, ids []string) ([]*api.Leaderboard, error)
	LeaderboardCreate(ctx context.Context, id string, authoritative bool, sortOrder, operator,
		resetSchedule string, metadata map[string]interface{}, enableRanks bool) error
	LeaderboardDelete(ctx context.Context, id string) error
}

// SetupLeaderboards ensures the default kills leaderboard and one scoped board
// per non-default game exist and are authoritative. Called once from InitModule.
//
// The board is authoritative: only the runtime (i.e. the server-only RPCs)
// may write records, so a client cannot post its own score through Nakama's
// public WriteLeaderboardRecord API. Nakama's LeaderboardCreate is idempotent
// and silently leaves an existing board's flags untouched, so a board created
// by an earlier build with authoritative=false stays writable by clients no
// matter how many times this runs. That case is therefore handled explicitly:
//
//   - default: InitModule fails with an error naming the fix, so the hole
//     cannot stay open unnoticed. Data-preserving fix: flip the row in the
//     Nakama DB and restart (docs/RUNBOOK.md, "Migrate a non-authoritative
//     leaderboard").
//   - LEADERBOARD_MIGRATE=recreate: the board is deleted and recreated
//     authoritative. This discards every existing record, so it is opt-in and
//     meant for dev/staging only.
func SetupLeaderboards(ctx context.Context, logger runtime.Logger, nk runtime.NakamaModule, games []string) error {
	return setupLeaderboardsCore(ctx, logger, nk, migrateMode(ctx), games)
}

// migrateMode reads LeaderboardMigrateEnv from the Nakama runtime env first
// and the process env second (same precedence as auth.LoadConfig).
func migrateMode(ctx context.Context) string {
	if env, ok := ctx.Value(runtime.RUNTIME_CTX_ENV).(map[string]string); ok {
		if v := env[LeaderboardMigrateEnv]; v != "" {
			return v
		}
	}
	return os.Getenv(LeaderboardMigrateEnv)
}

func setupLeaderboardsCore(ctx context.Context, logger runtime.Logger, nk leaderboardAdmin, migrate string, games []string) error {
	if err := ensureLeaderboard(ctx, logger, nk, gamectx.Scoped("", LeaderboardKillsAllTime), migrate); err != nil {
		return err
	}
	seen := make(map[string]bool)
	for _, game := range games {
		if gameid.IsDefaultGame(game) || seen[game] {
			continue
		}
		seen[game] = true
		if err := ensureLeaderboard(ctx, logger, nk, gamectx.Scoped(game, LeaderboardKillsAllTime), migrate); err != nil {
			return err
		}
	}
	return nil
}

func ensureLeaderboard(ctx context.Context, logger runtime.Logger, nk leaderboardAdmin, id, migrate string) error {
	boards, err := nk.LeaderboardsGetId(ctx, []string{id})
	if err != nil {
		return fmt.Errorf("get leaderboard %s: %w", id, err)
	}
	for _, b := range boards {
		if b.GetId() != id {
			continue
		}
		if b.GetAuthoritative() {
			logger.Info("Leaderboard %s ready (authoritative, sort=desc, operator=incr, no reset)", id)
			return nil
		}
		if migrate != "recreate" {
			return fmt.Errorf("leaderboard %s exists with authoritative=false, so clients can write their own scores; "+
				"fix: UPDATE leaderboard SET authoritative = true WHERE id = '%s' on the Nakama DB and restart Nakama "+
				"(keeps records), or set %s=recreate to delete and recreate it (discards records) — see docs/RUNBOOK.md",
				id, id, LeaderboardMigrateEnv)
		}
		logger.Warn("Leaderboard %s exists with authoritative=false; %s=recreate set — deleting and recreating it, all records are discarded",
			id, LeaderboardMigrateEnv)
		if err := nk.LeaderboardDelete(ctx, id); err != nil {
			return fmt.Errorf("delete non-authoritative leaderboard %s: %w", id, err)
		}
	}

	// kills_alltime: authoritative, incremental, descending, no reset.
	// LeaderboardCreate signature: (ctx, id, authoritative, sortOrder, operator, resetSchedule, metadata, enableRanks)
	// sortOrder: "desc" or "asc"; operator: "incr", "best", or "set".
	err = nk.LeaderboardCreate(ctx,
		id,     // id
		true,   // authoritative — runtime writes only, never client sessions
		"desc", // sort order
		"incr", // operator — each submit adds to the score
		"",     // reset schedule (empty = never reset)
		nil,    // metadata
		true,   // enable ranks
	)
	if err != nil {
		return fmt.Errorf("create leaderboard %s: %w", id, err)
	}

	logger.Info("Leaderboard %s created (authoritative, sort=desc, operator=incr, no reset)", id)
	return nil
}

// RPCSubmitKill is the RPC name for submitting a kill to the leaderboard.
const RPCSubmitKill = "submit_kill"

// SubmitKillRequest is the payload the game server sends.
type SubmitKillRequest struct {
	GameID string `json:"game_id,omitempty"`
	UserID string `json:"user_id"`
}

// SubmitKillRPC increments the player's kill count on the leaderboard.
// Server-only (runtime.http_key): a client session is rejected with code 7
// before the payload is read (see requireServerCaller).
func SubmitKillRPC(ctx context.Context, logger runtime.Logger, db *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	return submitKillCore(ctx, logger, nk, payload)
}

// submitKillCore is SubmitKillRPC against the narrow interface for tests.
func submitKillCore(ctx context.Context, logger runtime.Logger, nk killGranter, payload string) (string, error) {
	if err := requireServerCaller(ctx); err != nil {
		return "", err
	}
	var req SubmitKillRequest
	if err := json.Unmarshal([]byte(payload), &req); err != nil {
		return "", runtime.NewError("invalid payload", 3)
	}

	if req.UserID == "" {
		return "", runtime.NewError("user_id is required", 3)
	}
	game, err := gamectx.GameFromContext(ctx, req.GameID)
	if errors.Is(err, gamectx.ErrGamesConfig) {
		logger.Error("resolve submit_kill game: %v", err)
		return "", runtime.NewError("internal error", 13)
	}
	if err != nil {
		return "", err
	}

	// Write score=1 with operator=INCR → adds 1 to the existing score.
	// LeaderboardRecordWrite: (ctx, id, ownerID, username, score, subscore, metadata, overrideOperator)
	record, err := nk.LeaderboardRecordWrite(ctx,
		gamectx.Scoped(game, LeaderboardKillsAllTime),
		req.UserID, // owner
		"",         // username (auto-filled by Nakama)
		1,          // score — incremented by operator
		0,          // subscore
		nil,        // metadata
		nil,        // override operator (nil = use leaderboard default = INCR)
	)
	if err != nil {
		logger.Error("leaderboard write failed for %s: %v", req.UserID, err)
		return "", runtime.NewError(fmt.Sprintf("leaderboard write failed: %v", err), 13)
	}

	resp := map[string]interface{}{
		"success": true,
		"score":   record.Score,
		"rank":    record.Rank,
	}
	out, _ := json.Marshal(resp)
	return string(out), nil
}

// RPCGetLeaderboard is the RPC name for querying the leaderboard.
const RPCGetLeaderboard = "get_leaderboard"

// GetLeaderboardRPC returns the top N records from the kills leaderboard.
func GetLeaderboardRPC(ctx context.Context, logger runtime.Logger, db *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	game, err := gamectx.GameFromContext(ctx, "")
	if errors.Is(err, gamectx.ErrGamesConfig) {
		logger.Error("resolve get_leaderboard game: %v", err)
		return "", runtime.NewError("internal error", 13)
	}
	if err != nil {
		return "", err
	}
	id := gamectx.Scoped(game, LeaderboardKillsAllTime)
	limit := 10

	records, _, _, _, err := nk.LeaderboardRecordsList(ctx,
		id,
		[]string{}, // owner IDs filter (empty = all)
		limit,
		"", // cursor
		0,  // expiry override
	)
	if err != nil {
		return "", runtime.NewError(fmt.Sprintf("leaderboard list failed: %v", err), 13)
	}

	type entry struct {
		Rank     int64  `json:"rank"`
		UserID   string `json:"user_id"`
		Username string `json:"username"`
		Score    int64  `json:"score"`
	}

	entries := make([]entry, 0, len(records))
	for _, r := range records {
		username := ""
		if r.Username != nil {
			username = r.Username.Value
		}
		entries = append(entries, entry{
			Rank:     r.Rank,
			UserID:   r.OwnerId,
			Username: username,
			Score:    r.Score,
		})
	}

	resp := map[string]interface{}{
		"leaderboard_id": id,
		"records":        entries,
	}
	out, _ := json.Marshal(resp)
	return string(out), nil
}
