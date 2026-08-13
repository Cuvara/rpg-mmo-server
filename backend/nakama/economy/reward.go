// Package economy implements server-authoritative economy RPCs for the
// Nakama Go runtime plugin.
package economy

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"

	"github.com/heroiclabs/nakama-common/runtime"
)

// RPCRewardKill is the RPC name the game server calls when a player kills an enemy.
const RPCRewardKill = "reward_kill"

// GoldPerKill is the gold awarded per enemy kill.
const GoldPerKill = 10

// RewardKillRequest is the payload the game server sends.
type RewardKillRequest struct {
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
// Called by the game server via HTTP with runtime.http_key auth.
func RewardKillRPC(ctx context.Context, logger runtime.Logger, db *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	var req RewardKillRequest
	if err := json.Unmarshal([]byte(payload), &req); err != nil {
		return "", runtime.NewError("invalid payload", 3) // INVALID_ARGUMENT
	}

	if req.UserID == "" {
		return "", runtime.NewError("user_id is required", 3)
	}

	// Award gold via Nakama wallet — atomic, handles concurrent updates.
	changeset := map[string]int64{"gold": GoldPerKill}
	metadata := map[string]interface{}{
		"source":    "enemy_kill",
		"victim_id": req.VictimID,
		"map_id":    req.MapID,
	}

	updated, _, err := nk.WalletUpdate(ctx, req.UserID, changeset, metadata, true)
	if err != nil {
		logger.Error("wallet update failed for %s: %v", req.UserID, err)
		return "", runtime.NewError(fmt.Sprintf("wallet update failed: %v", err), 13) // INTERNAL
	}

	gold := int64(0)
	if v, ok := updated["gold"]; ok {
		gold = v
	}

	logger.Info("Awarded %d gold to %s (kill %s on %s), balance: %d",
		GoldPerKill, req.UserID, req.VictimID, req.MapID, gold)

	resp := RewardKillResponse{Gold: gold, Success: true}
	out, _ := json.Marshal(resp)
	return string(out), nil
}
