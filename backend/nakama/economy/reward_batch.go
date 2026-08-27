package economy

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// RPCRewardKills is the batched replacement for the reward_kill + submit_kill
// pair. One call grants the gold AND the leaderboard score for every kill a
// player accumulated since the game server's last flush.
//
// Why it exists: the per-kill pair cost 2 HTTP requests and 2 separate meta-DB
// transactions per mob kill. At 200 players on the grindy end (a kill every 3s)
// that is ~133 commits/s of pure per-kill overhead — the first thing to
// saturate a shared small-VPS Postgres (rpg-mmo-server#233). Batching per
// killer turns it into one request and two increments per killer per flush
// interval, with identical semantics because both underlying operations are
// increments.
const RPCRewardKills = "reward_kills"

// MaxKillsPerBatch bounds a single batch. Far above anything a legitimate game
// server flushes (a 3s flush at one kill per second is 3), it exists so a
// corrupted or malicious payload cannot mint unbounded gold in one call.
const MaxKillsPerBatch = 1000

// RewardKillsRequest is the payload the game server sends.
type RewardKillsRequest struct {
	UserID string `json:"user_id"`
	Kills  int64  `json:"kills"`
	MapID  string `json:"map_id"`
	// BatchID identifies this flush attempt. It is recorded in the wallet
	// metadata so a suspected double-grant can be audited after the fact, and
	// it is the slot a future storage-backed idempotency guard would key on.
	// The server does not deduplicate on it today — see the contract note on
	// RewardKillsRPC.
	BatchID string `json:"batch_id"`
}

// RewardKillsResponse is returned to the caller.
type RewardKillsResponse struct {
	Success bool  `json:"success"`
	Gold    int64 `json:"gold"`
	Score   int64 `json:"score"`
	Rank    int64 `json:"rank"`
	// LeaderboardError is set when the gold was granted but the score write
	// failed. The call still reports success — see the contract note below.
	LeaderboardError string `json:"leaderboard_error,omitempty"`
}

// killGranter is the slice of runtime.NakamaModule this RPC actually uses,
// narrow so tests can implement it without mocking the whole module.
type killGranter interface {
	WalletUpdate(ctx context.Context, userID string, changeset map[string]int64,
		metadata map[string]interface{}, updateLedger bool) (map[string]int64, map[string]int64, error)
	LeaderboardRecordWrite(ctx context.Context, id, ownerID, username string,
		score, subscore int64, metadata map[string]interface{},
		overrideOperator *int) (*api.LeaderboardRecord, error)
}

// RewardKillsRPC grants gold and leaderboard score for a batch of kills.
//
// Error contract, load-bearing for the game server's retry policy: an error is
// returned ONLY when nothing was granted (bad payload, or the wallet update
// itself failed), so a caller that receives an error may safely re-queue the
// batch. Once the wallet update has succeeded the call always reports success —
// a leaderboard failure after it is logged and surfaced in the response, never
// turned into an error, because an error at that point would invite a retry
// that grants the gold twice. Bounded score loss is the accepted cost; double
// gold is not (ADR-6).
func RewardKillsRPC(ctx context.Context, logger runtime.Logger, db *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	return rewardKillsCore(ctx, logger, nk, payload)
}

// rewardKillsCore is RewardKillsRPC against the narrow interface; the split
// exists so tests can drive it with a two-method mock.
func rewardKillsCore(ctx context.Context, logger runtime.Logger, nk killGranter, payload string) (string, error) {
	var req RewardKillsRequest
	if err := json.Unmarshal([]byte(payload), &req); err != nil {
		return "", runtime.NewError("invalid payload", 3) // INVALID_ARGUMENT
	}
	if req.UserID == "" {
		return "", runtime.NewError("user_id is required", 3)
	}
	if req.Kills <= 0 || req.Kills > MaxKillsPerBatch {
		return "", runtime.NewError(
			fmt.Sprintf("kills must be in 1..%d", MaxKillsPerBatch), 3)
	}

	changeset := map[string]int64{"gold": GoldPerKill * req.Kills}
	metadata := map[string]interface{}{
		"source":   "enemy_kills",
		"kills":    req.Kills,
		"map_id":   req.MapID,
		"batch_id": req.BatchID,
	}

	updated, _, err := nk.WalletUpdate(ctx, req.UserID, changeset, metadata, true)
	if err != nil {
		logger.Error("batch wallet update failed for %s (%d kills): %v", req.UserID, req.Kills, err)
		return "", runtime.NewError(fmt.Sprintf("wallet update failed: %v", err), 13) // INTERNAL
	}

	resp := RewardKillsResponse{Success: true}
	if v, ok := updated["gold"]; ok {
		resp.Gold = v
	}

	record, err := nk.LeaderboardRecordWrite(ctx,
		LeaderboardKillsAllTime, req.UserID, "", req.Kills, 0, nil, nil)
	if err != nil {
		// Gold is already granted: report success so the caller does not
		// re-queue and double-grant. The loss is this batch's score only.
		logger.Error("batch leaderboard write failed for %s (%d kills, gold already granted): %v",
			req.UserID, req.Kills, err)
		resp.LeaderboardError = err.Error()
	} else {
		resp.Score = record.Score
		resp.Rank = record.Rank
	}

	// One line per FLUSH, not per kill — reward_kill's per-kill Info line was
	// itself part of the amplification this RPC removes.
	logger.Debug("Awarded %d gold to %s for %d kills on %s (batch %s)",
		GoldPerKill*req.Kills, req.UserID, req.Kills, req.MapID, req.BatchID)

	out, _ := json.Marshal(resp)
	return string(out), nil
}
