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

	// Auth
	JWTSecret string

	// Database
	MetaDBURL      string
	GameStateDBURL string

	// Redis
	RedisAddr     string
	RedisPassword string

	// Logging
	LogLevel string
}

// Load reads configuration from environment variables with sensible defaults.
func Load() Config {
	return Config{
		GatewayAddr:    envOrDefault("GATEWAY_ADDR", ":8000"),
		GameServerAddr: envOrDefault("GAMESERVER_ADDR", ":9000"),
		TickRate:       envOrDefaultInt("TICK_RATE", 10),
		JWTSecret:      envOrDefault("JWT_SECRET", "dev-secret-change-me"),
		MetaDBURL:      envOrDefault("META_DB_URL", "postgres://localhost:5432/rpg_meta?sslmode=disable"),
		GameStateDBURL: envOrDefault("GAMESTATE_DB_URL", "postgres://localhost:5432/rpg_gamestate?sslmode=disable"),
		RedisAddr:      envOrDefault("REDIS_ADDR", "localhost:6379"),
		RedisPassword:  envOrDefault("REDIS_PASSWORD", ""),
		LogLevel:       envOrDefault("LOG_LEVEL", "info"),
	}
}

func envOrDefault(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
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
