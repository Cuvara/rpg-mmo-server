package config

import (
	"os"
	"testing"
)

func TestLoad_Defaults(t *testing.T) {
	// Clear any env vars that might interfere
	os.Unsetenv("GATEWAY_ADDR")
	os.Unsetenv("TICK_RATE")
	os.Unsetenv("JWT_SECRET")

	cfg := Load()

	if cfg.GatewayAddr != ":8000" {
		t.Errorf("GatewayAddr = %q, want %q", cfg.GatewayAddr, ":8000")
	}
	if cfg.TickRate != 10 {
		t.Errorf("TickRate = %d, want 10", cfg.TickRate)
	}
	if cfg.JWTSecret != "dev-secret-change-me" {
		t.Errorf("JWTSecret = %q, want default", cfg.JWTSecret)
	}
}

func TestLoad_EnvOverride(t *testing.T) {
	os.Setenv("GATEWAY_ADDR", ":7777")
	os.Setenv("TICK_RATE", "15")
	defer os.Unsetenv("GATEWAY_ADDR")
	defer os.Unsetenv("TICK_RATE")

	cfg := Load()

	if cfg.GatewayAddr != ":7777" {
		t.Errorf("GatewayAddr = %q, want %q", cfg.GatewayAddr, ":7777")
	}
	if cfg.TickRate != 15 {
		t.Errorf("TickRate = %d, want 15", cfg.TickRate)
	}
}

func TestLoad_InvalidTickRate(t *testing.T) {
	os.Setenv("TICK_RATE", "not-a-number")
	defer os.Unsetenv("TICK_RATE")

	cfg := Load()
	if cfg.TickRate != 10 {
		t.Errorf("TickRate = %d, want 10 (fallback)", cfg.TickRate)
	}
}
