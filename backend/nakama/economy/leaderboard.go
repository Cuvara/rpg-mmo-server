package economy

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"

	"github.com/heroiclabs/nakama-common/runtime"
)

// Leaderboard IDs.
const (
	LeaderboardKillsAllTime = "kills_alltime"
)

// SetupLeaderboards creates leaderboards if they don't exist.
// Called once from InitModule. Nakama's LeaderboardCreate is idempotent.
func SetupLeaderboards(ctx context.Context, logger runtime.Logger, nk runtime.NakamaModule) error {
	// kills_alltime: incremental, descending, no reset.
	// LeaderboardCreate signature: (ctx, id, authoritative, sortOrder, operator, resetSchedule, metadata, enableRanks)
	// sortOrder: "desc" or "asc"; operator: "incr", "best", or "set".
	err := nk.LeaderboardCreate(ctx,
		LeaderboardKillsAllTime, // id
		false,                   // authoritative
		"desc",                  // sort order
		"incr",                  // operator — each submit adds to the score
		"",                      // reset schedule (empty = never reset)
		nil,                     // metadata
		true,                    // enable ranks
	)
	if err != nil {
		return fmt.Errorf("create leaderboard %s: %w", LeaderboardKillsAllTime, err)
	}

	logger.Info("Leaderboard %s ready (sort=desc, operator=incr, no reset)", LeaderboardKillsAllTime)
	return nil
}

// RPCSubmitKill is the RPC name for submitting a kill to the leaderboard.
const RPCSubmitKill = "submit_kill"

// SubmitKillRequest is the payload the game server sends.
type SubmitKillRequest struct {
	UserID string `json:"user_id"`
}

// SubmitKillRPC increments the player's kill count on the leaderboard.
func SubmitKillRPC(ctx context.Context, logger runtime.Logger, db *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	var req SubmitKillRequest
	if err := json.Unmarshal([]byte(payload), &req); err != nil {
		return "", runtime.NewError("invalid payload", 3)
	}

	if req.UserID == "" {
		return "", runtime.NewError("user_id is required", 3)
	}

	// Write score=1 with operator=INCR → adds 1 to the existing score.
	// LeaderboardRecordWrite: (ctx, id, ownerID, username, score, subscore, metadata, overrideOperator)
	record, err := nk.LeaderboardRecordWrite(ctx,
		LeaderboardKillsAllTime,
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
	limit := 10

	records, _, _, _, err := nk.LeaderboardRecordsList(ctx,
		LeaderboardKillsAllTime,
		[]string{},  // owner IDs filter (empty = all)
		limit,
		"",          // cursor
		0,           // expiry override
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
		"leaderboard_id": LeaderboardKillsAllTime,
		"records":        entries,
	}
	out, _ := json.Marshal(resp)
	return string(out), nil
}

