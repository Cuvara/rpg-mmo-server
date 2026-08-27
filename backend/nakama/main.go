// Package main is the Nakama Go runtime plugin for the RPG MMO backend.
// It is compiled with -buildmode=plugin and loaded by Nakama at start-up,
// which calls InitModule to register hooks and RPCs.
package main

import (
	"context"
	"database/sql"
	"fmt"
	"time"

	"github.com/duycuong/rpg-mmo/nakama/auth"
	"github.com/duycuong/rpg-mmo/nakama/economy"
	"github.com/heroiclabs/nakama-common/runtime"
)

// main is never executed: this package is built with -buildmode=plugin and
// loaded by Nakama, which calls InitModule. It exists only so that a plain
// `go build ./...` / `go vet ./...` succeeds for the main package.
func main() {}

// InitModule is the Nakama plugin entry point. Nakama looks up this exact
// symbol after loading the shared object and invokes it once at start-up.
func InitModule(ctx context.Context, logger runtime.Logger, db *sql.DB, nk runtime.NakamaModule, initializer runtime.Initializer) error {
	start := time.Now()

	if err := initializer.RegisterRpc(auth.RPCGatewayToken, auth.GatewayTokenRPC); err != nil {
		return fmt.Errorf("register rpc %s: %w", auth.RPCGatewayToken, err)
	}
	if err := initializer.RegisterAfterAuthenticateDevice(auth.AfterAuthenticateDevice); err != nil {
		return fmt.Errorf("register after authenticate device: %w", err)
	}
	if err := initializer.RegisterAfterAuthenticateEmail(auth.AfterAuthenticateEmail); err != nil {
		return fmt.Errorf("register after authenticate email: %w", err)
	}
	if err := initializer.RegisterBeforeAuthenticateEmail(auth.BeforeAuthenticateEmail); err != nil {
		return fmt.Errorf("register before authenticate email: %w", err)
	}

	// Economy RPCs
	if err := initializer.RegisterRpc(economy.RPCRewardKill, economy.RewardKillRPC); err != nil {
		return fmt.Errorf("register rpc %s: %w", economy.RPCRewardKill, err)
	}
	if err := initializer.RegisterRpc(economy.RPCRewardKills, economy.RewardKillsRPC); err != nil {
		return fmt.Errorf("register rpc %s: %w", economy.RPCRewardKills, err)
	}
	if err := initializer.RegisterRpc(economy.RPCSubmitKill, economy.SubmitKillRPC); err != nil {
		return fmt.Errorf("register rpc %s: %w", economy.RPCSubmitKill, err)
	}
	if err := initializer.RegisterRpc(economy.RPCGetLeaderboard, economy.GetLeaderboardRPC); err != nil {
		return fmt.Errorf("register rpc %s: %w", economy.RPCGetLeaderboard, err)
	}

	// Create leaderboards (idempotent)
	if err := economy.SetupLeaderboards(ctx, logger, nk); err != nil {
		return fmt.Errorf("setup leaderboards: %w", err)
	}

	logger.Info("rpg-mmo nakama module loaded in %dms", time.Since(start).Milliseconds())
	return nil
}
