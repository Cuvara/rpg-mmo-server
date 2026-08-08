package config

import (
	"os"
	"strconv"
)

// Config holds all shared configuration for backend services.
type Config struct {
	// Server
	GatewayAddr    string
	GameServerAddr string
	TickRate       int
	// GatewayTransport / GameServerTransport select the realtime transport
	// ("tcp" or "kcp") each service listens with. See shared/transport.
	GatewayTransport    string
	GameServerTransport string

	// Auth
	//
	// JWTSecret is the HS256 secret (or comma-separated rotation list —
	// see jwt.ParseKeyring) for the Nakama -> client -> gateway auth token.
	// The FIRST entry signs, every entry verifies.
	JWTSecret string
	// JoinTokenSecret is the HS256 secret (or rotation list) for the
	// gateway -> game server join token. REQUIRED: both gateway and game
	// server refuse to start when it is empty.
	JoinTokenSecret string
	// TransportKey is the pre-shared key that encrypts KCP datagrams
	// (32-byte hex recommended). Empty means plaintext. See shared/transport.
	TransportKey string

	// Database
	MetaDBURL      string
	GameStateDBURL string
	// GameDBURL is the DSN of the game-state PostgreSQL instance actually used
	// at runtime. Empty (the default) means "no PostgreSQL configured" and
	// services fall back to their in-memory stores — set GAME_DB_URL to switch
	// the gameserver to pgstore.PostgresPlayerStore.
	// Example: postgres://game:localdev@localhost:5433/gamestate?sslmode=disable
	GameDBURL string

	// Redis
	RedisAddr     string
	RedisPassword string

	// Rate limiting (gateway). Zero or negative disables the limiter.
	//
	// Defaults are deliberately generous relative to a legitimate client, which
	// connects once and sends three frames (auth, enter-world, disconnect):
	// 10 connections/min/IP still tolerates a flapping mobile link and a shared
	// carrier NAT, and 60 messages/s/conn is two orders of magnitude above what
	// the gateway protocol asks for. They exist to bound abuse, not to shape
	// normal traffic.
	GatewayConnRatePerMin float64
	GatewayConnBurst      float64
	GatewayMsgRatePerSec  float64
	GatewayMsgBurst       float64

	// Logging
	LogLevel string
}

// EffectiveJoinTokenSecret returns the secret spec used for gateway -> game
// server join tokens, falling back to JWTSecret when JOIN_TOKEN_SECRET is
// unset. The second return value reports whether the fallback was taken, so
// callers can emit the start-up warning exactly once.
func (c Config) EffectiveJoinTokenSecret() (spec string, sharedWithAuth bool) {
	if c.JoinTokenSecret != "" {
		return c.JoinTokenSecret, false
	}
	return c.JWTSecret, true
}

// Load reads configuration from environment variables with sensible defaults.
func Load() Config {
	return Config{
		GatewayAddr:    envOrDefault("GATEWAY_ADDR", ":8000"),
		GameServerAddr: envOrDefault("GAMESERVER_ADDR", ":9000"),
		TickRate:       envOrDefaultInt("TICK_RATE", 10),

		GatewayTransport:    envOrDefault("GATEWAY_TRANSPORT", "tcp"),
		GameServerTransport: envOrDefault("GAMESERVER_TRANSPORT", "tcp"),

		JWTSecret:       envOrDefault("JWT_SECRET", "dev-secret-change-me"),
		JoinTokenSecret: envOrDefault("JOIN_TOKEN_SECRET", ""),
		TransportKey:    envOrDefault("TRANSPORT_KEY", ""),

		MetaDBURL:      envOrDefault("META_DB_URL", "postgres://localhost:5432/rpg_meta?sslmode=disable"),
		GameStateDBURL: envOrDefault("GAMESTATE_DB_URL", "postgres://localhost:5432/rpg_gamestate?sslmode=disable"),
		GameDBURL:      envOrDefault("GAME_DB_URL", ""),
		RedisAddr:      envOrDefault("REDIS_ADDR", "localhost:6379"),
		RedisPassword:  envOrDefault("REDIS_PASSWORD", ""),
		GatewayConnRatePerMin: envOrDefaultFloat("GATEWAY_CONN_RATE_PER_MIN", 10),
		GatewayConnBurst:      envOrDefaultFloat("GATEWAY_CONN_BURST", 10),
		GatewayMsgRatePerSec:  envOrDefaultFloat("GATEWAY_MSG_RATE_PER_SEC", 60),
		GatewayMsgBurst:       envOrDefaultFloat("GATEWAY_MSG_BURST", 120),

		LogLevel: envOrDefault("LOG_LEVEL", "info"),
	}
}

func envOrDefault(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

// envOrDefaultFloat reads a float env var. An unparseable value falls back to
// the default rather than failing start-up: a typo in a rate must not take the
// service down, and the effective value is logged at start-up anyway.
func envOrDefaultFloat(key string, fallback float64) float64 {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	f, err := strconv.ParseFloat(v, 64)
	if err != nil {
		return fallback
	}
	return f
}

func envOrDefaultInt(key string, fallback int) int {
	v := os.Getenv(key)
	if v == "" {
		return fallback
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		return fallback
	}
	return n
}
