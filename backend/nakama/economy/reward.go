// Package economy implements server-authoritative economy RPCs for the
// Nakama Go runtime plugin.
package economy

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/duycuong/rpg-mmo/shared/gameid"
	"github.com/heroiclabs/nakama-common/runtime"
)

// RPCRewardKill is the RPC name the game server calls when a player kills an enemy.
const RPCRewardKill = "reward_kill"

// GoldPerKill is the gold awarded per enemy kill.
const GoldPerKill = 10

// CurrencyGold is the game's wallet currency, scoped before every wallet access.
const CurrencyGold = "gold"

// RewardKillRequest is the payload the game server sends.
type RewardKillRequest struct {
	GameID   string `json:"game_id,omitempty"`
	UserID   string `json:"user_id"`
	VictimID string `json:"victim_id"`
	MapID    string `json:"map_id"`
}

// RewardKillResponse is returned to the caller.
type RewardKillResponse struct {
	Gold    int64 `json:"gold"`
	Success bool  `json:"success"`
}

// RewardKillRPC awards gold to a player who killed an enemy.
// Called by the game server via HTTP with runtime.http_key auth; client
// sessions are rejected with code 7 before the payload is read (see
// requireServerCaller).
func RewardKillRPC(ctx context.Context, logger runtime.Logger, db *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	return rewardKillCore(ctx, logger, nk, payload)
}

// rewardKillCore is RewardKillRPC against the narrow interface so tests can
// drive it with the same mock as reward_kills.
func rewardKillCore(ctx context.Context, logger runtime.Logger, nk killGranter, payload string) (string, error) {
	if err := requireServerCaller(ctx); err != nil {
		return "", err
	}
	var req RewardKillRequest
	if err := json.Unmarshal([]byte(payload), &req); err != nil {
		return "", runtime.NewError("invalid payload", 3) // INVALID_ARGUMENT
	}

	if req.UserID == "" {
		return "", runtime.NewError("user_id is required", 3)
	}
	game, err := gamectx.GameFromContext(ctx, req.GameID)
	if errors.Is(err, gamectx.ErrGamesConfig) {
		logger.Error("resolve reward_kill game: %v", err)
		return "", runtime.NewError("internal error", 13)
	}
	if err != nil {
		return "", err
	}

	// Award gold via Nakama wallet — atomic, handles concurrent updates.
	changeset := map[string]int64{gamectx.Scoped(game, CurrencyGold): GoldPerKill}
	metadata := map[string]interface{}{
		"source":    "enemy_kill",
		"victim_id": req.VictimID,
		"map_id":    req.MapID,
	}
	if !gameid.IsDefaultGame(game) {
		metadata["game_id"] = game
	}

	updated, _, err := nk.WalletUpdate(ctx, req.UserID, changeset, metadata, true)
	if err != nil {
		logger.Error("wallet update failed for %s: %v", req.UserID, err)
		return "", runtime.NewError(fmt.Sprintf("wallet update failed: %v", err), 13) // INTERNAL
	}

	gold := int64(0)
	if v, ok := updated[gamectx.Scoped(game, CurrencyGold)]; ok {
		gold = v
	}

	// Debug, not Info: this fires once per kill, and at 200 players it was 20+
	// log lines per second of pure noise — part of the per-kill amplification
	// the batched reward_kills RPC replaces (rpg-mmo-server#233). This RPC is
	// kept for compatibility; new callers should use reward_kills.
	logger.Debug("Awarded %d gold to %s (kill %s on %s), balance: %d",
		GoldPerKill, req.UserID, req.VictimID, req.MapID, gold)

	resp := RewardKillResponse{Gold: gold, Success: true}
	out, _ := json.Marshal(resp)
	return string(out), nil
}
