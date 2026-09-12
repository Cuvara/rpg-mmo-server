package load

import (
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
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
	// Protobuf is the wire the client speaks (ADR-9). A JSON default measured
	// the legacy arm for a whole sweep before anyone noticed the ~5x gap.
	if cfg.Encoding != messages.EncodingProto {
		t.Errorf("Encoding = %q, want proto: the default must measure the wire the client actually uses", cfg.Encoding)
	}
	if cfg.BaselineEntities != 0 {
		t.Errorf("BaselineEntities = %d, want 0 (the validity gate is strict unless told otherwise)", cfg.BaselineEntities)
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

func TestLoadConfigEncodingAndBaselineFlags(t *testing.T) {
	cfg, err := LoadConfig(env(map[string]string{"JWT_SECRET": "s"}),
		[]string{"-encoding", "json", "-baseline-entities", "6"})
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}
	if cfg.Encoding != messages.EncodingJSON {
		t.Errorf("Encoding = %q, want json (the legacy arm must stay reachable for A/B sweeps)", cfg.Encoding)
	}
	if cfg.BaselineEntities != 6 {
		t.Errorf("BaselineEntities = %d, want 6", cfg.BaselineEntities)
	}
	if _, err := LoadConfig(env(map[string]string{"JWT_SECRET": "s"}), []string{"-baseline-entities", "-1"}); err == nil {
		t.Error("a negative baseline must be rejected; it would tighten the gate below zero players")
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

// TestClusterLeavesAStationaryCrowd pins the arithmetic behind the warning on
// MovementCluster: a player marching +X clears an origin-centred AOI long before a
// default run finishes, so the mode cannot be used to hold players inside a stationary
// entity population.
//
// This is a guard on a documented claim, not on code that can regress on its own. It
// exists because the claim it replaces — "they stay mutually in-AOI, so this is the
// worst-case dense-crowd shape" — was true of the players and false of everything else,
// and a run that believed it measured a nearly empty AOI while reporting the population
// it started with. Measured live: server-side snapshot bytes fell from 113 kB/s to
// 0.6 kB/s by t=24s under this mode, and stayed flat at 117.6 kB/s under MovementStill.
func TestClusterLeavesAStationaryCrowd(t *testing.T) {
	// Server-side constants this mode is measured against.
	const (
		playerSpeed = 5.0  // ServerDefaults.DefaultPlayerSpeed, world units/second
		aoiRadius   = 50.0 // GameConstants.DefaultAoiRadius
	)

	secondsToClearAOI := aoiRadius / playerSpeed
	if secondsToClearAOI > 15 {
		t.Fatalf("a cluster player now takes %.1fs to clear the AOI; the warning on "+
			"MovementCluster is calibrated for ~10s and should be re-derived", secondsToClearAOI)
	}

	// The default measurement window starts after the warmup and runs for Duration.
	// If the player is already outside the AOI when measurement begins, the mode is
	// measuring an empty AOI for the whole window, not merely part of it.
	distanceAtWindowStart := playerSpeed * DefaultWarmup.Seconds()
	if distanceAtWindowStart < aoiRadius {
		t.Logf("player is %.0f units out when measurement starts (AOI %.0f): the collapse "+
			"happens during the window", distanceAtWindowStart, aoiRadius)
	}

	distanceAtWindowEnd := playerSpeed * (DefaultWarmup + DefaultDuration).Seconds()
	if distanceAtWindowEnd <= aoiRadius {
		t.Fatalf("a default run now ends %.0f units from spawn, within the %.0f-unit AOI; "+
			"MovementCluster no longer walks players out of a stationary crowd and its "+
			"warning should be revisited", distanceAtWindowEnd, aoiRadius)
	}

	// And the mode really is the default, which is what makes the above a trap rather
	// than an opt-in.
	cfg, err := LoadConfig(func(k string) string {
		if k == "JWT_SECRET" {
			return "test-secret"
		}
		return ""
	}, nil)
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}
	if cfg.Movement != MovementCluster {
		t.Fatalf("default movement = %q, want %q — if the default moved, the warning on "+
			"MovementCluster overstates the risk and should be toned down",
			cfg.Movement, MovementCluster)
	}
}
