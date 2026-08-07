package load

import (
	"testing"
	"time"
)

func env(m map[string]string) func(string) string {
	return func(k string) string { return m[k] }
}

func TestLoadConfigDefaults(t *testing.T) {
	cfg, err := LoadConfig(env(map[string]string{"JWT_SECRET": "s"}), nil)
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}
	if cfg.AuthMode != AuthPresigned {
		t.Errorf("AuthMode = %q, want %q (pre-signing must be the default so a run "+
			"measures the game path, not Nakama's login throughput)", cfg.AuthMode, AuthPresigned)
	}
	if cfg.JoinMode != JoinGateway {
		t.Errorf("JoinMode = %q, want %q", cfg.JoinMode, JoinGateway)
	}
	if cfg.TickRate != DefaultTickRate {
		t.Errorf("TickRate = %d, want %d", cfg.TickRate, DefaultTickRate)
	}
	// An unset JOIN_TOKEN_SECRET must fall back to JWT_SECRET, mirroring the
	// game server's own fallback.
	if cfg.JoinTokenSecret != "s" {
		t.Errorf("JoinTokenSecret = %q, want the JWT secret", cfg.JoinTokenSecret)
	}
}

func TestLoadConfigJoinTokenSecretOverride(t *testing.T) {
	cfg, err := LoadConfig(env(map[string]string{
		"JWT_SECRET": "auth", "JOIN_TOKEN_SECRET": "join",
	}), nil)
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}
	if cfg.JoinTokenSecret != "join" {
		t.Errorf("JoinTokenSecret = %q, want %q", cfg.JoinTokenSecret, "join")
	}
}

func TestLoadConfigFlags(t *testing.T) {
	cfg, err := LoadConfig(env(map[string]string{"JWT_SECRET": "s"}), []string{
		"-players", "50", "-duration", "30s", "-movement", "still",
		"-join", "direct", "-auth", "nakama", "-ramp", "5",
	})
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}
	if cfg.Players != 50 || cfg.Duration != 30*time.Second {
		t.Errorf("got players=%d duration=%s", cfg.Players, cfg.Duration)
	}
	if cfg.Movement != MovementStill || cfg.JoinMode != JoinDirect || cfg.AuthMode != AuthNakama {
		t.Errorf("got movement=%s join=%s auth=%s", cfg.Movement, cfg.JoinMode, cfg.AuthMode)
	}
	if cfg.RampDuration() != 10*time.Second {
		t.Errorf("RampDuration = %s, want 10s (50 players at 5/s)", cfg.RampDuration())
	}
}

func TestConfigValidate(t *testing.T) {
	base := func() Config {
		c, _ := LoadConfig(env(map[string]string{"JWT_SECRET": "s"}), nil)
		return c
	}
	tests := []struct {
		name    string
		mutate  func(*Config)
		wantErr bool
	}{
		{"valid", func(*Config) {}, false},
		{"no secret", func(c *Config) { c.JWTSecret = "" }, true},
		{"zero players", func(c *Config) { c.Players = 0 }, true},
		{"zero tick rate", func(c *Config) { c.TickRate = 0 }, true},
		{"zero duration", func(c *Config) { c.Duration = 0 }, true},
		{"negative warmup", func(c *Config) { c.Warmup = -time.Second }, true},
		{"bad auth", func(c *Config) { c.AuthMode = "oauth" }, true},
		{"bad movement", func(c *Config) { c.Movement = "teleport" }, true},
		{"bad join", func(c *Config) { c.JoinMode = "magic" }, true},
		{"bad transport", func(c *Config) { c.Transport = "carrier-pigeon" }, true},
		{"direct without addr", func(c *Config) {
			c.JoinMode = JoinDirect
			c.GameServerAddr = ""
		}, true},
		{"direct with addr", func(c *Config) { c.JoinMode = JoinDirect }, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			c := base()
			tt.mutate(&c)
			err := c.Validate()
			if (err != nil) != tt.wantErr {
				t.Errorf("Validate() error = %v, wantErr %v", err, tt.wantErr)
			}
		})
	}
}

func TestInputInterval(t *testing.T) {
	c := Config{TickRate: 15}
	// 15Hz -> 66.66ms, matching the server's own tick period.
	if got := c.InputInterval(); got != time.Second/15 {
		t.Errorf("InputInterval = %s, want %s", got, time.Second/15)
	}
}

func TestTickBudgetMatchesTickRate(t *testing.T) {
	// The whole benchmark hangs off this equality: the acceptance threshold in
	// ADR-7 is one tick period at the default rate.
	if TickBudget != time.Second/DefaultTickRate {
		t.Errorf("TickBudget = %s, want %s", TickBudget, time.Second/DefaultTickRate)
	}
}

func TestNormalizeDialAddr(t *testing.T) {
	tests := []struct{ in, want string }{
		{":8000", "127.0.0.1:8000"},
		{"0.0.0.0:9000", "127.0.0.1:9000"},
		{"[::]:9200", "127.0.0.1:9200"},
		{"10.0.0.5:9000", "10.0.0.5:9000"},
		{"example.com:9000", "example.com:9000"},
	}
	for _, tt := range tests {
		if got := NormalizeDialAddr(tt.in); got != tt.want {
			t.Errorf("NormalizeDialAddr(%q) = %q, want %q", tt.in, got, tt.want)
		}
	}
}
