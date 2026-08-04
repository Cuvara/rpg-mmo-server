// Package auth implements the Nakama runtime hooks and RPCs that cover
// authentication for the RPG MMO backend: realtime (gateway) token issuance,
// player-profile bootstrapping on first login, and email/password validation.
package auth

import (
	"context"
	"time"

	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/heroiclabs/nakama-common/runtime"
)

// Config holds the auth-related settings used by the Nakama plugin.
type Config struct {
	// JWTSecret is the HS256 secret shared with the Gateway. The Gateway
	// verifies realtime tokens locally with this secret, so it MUST match.
	JWTSecret string
	// TokenTTL is the lifetime of an issued realtime (gateway) token.
	TokenTTL time.Duration
	// MinPasswordLength is the minimum accepted password length on email auth.
	MinPasswordLength int
}

// DefaultMinPasswordLength is Nakama's own minimum password length.
const DefaultMinPasswordLength = 8

// LoadConfig builds a Config from the Nakama runtime environment. Nakama
// exposes configured environment variables through the request context under
// runtime.RUNTIME_CTX_ENV; anything missing there falls back to the process
// environment via the shared config loader (same defaults as the Gateway).
func LoadConfig(ctx context.Context) Config {
	base := config.Load()
	cfg := Config{
		JWTSecret:         base.JWTSecret,
		TokenTTL:          constants.SessionTTL,
		MinPasswordLength: DefaultMinPasswordLength,
	}

	if env, ok := ctx.Value(runtime.RUNTIME_CTX_ENV).(map[string]string); ok {
		if v := env["JWT_SECRET"]; v != "" {
			cfg.JWTSecret = v
		}
	}
	return cfg
}
