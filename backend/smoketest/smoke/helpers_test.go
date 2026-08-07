package smoke

import (
	"errors"
	"strings"
	"testing"
	"time"
)

func fakeEnv(m map[string]string) func(string) string {
	return func(k string) string { return m[k] }
}

func TestEnvOr(t *testing.T) {
	tests := []struct {
		name string
		env  map[string]string
		key  string
		def  string
		want string
	}{
		{"set", map[string]string{"K": "v"}, "K", "d", "v"},
		{"unset", map[string]string{}, "K", "d", "d"},
		{"empty counts as unset", map[string]string{"K": ""}, "K", "d", "d"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := EnvOr(fakeEnv(tt.env), tt.key, tt.def); got != tt.want {
				t.Errorf("EnvOr() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestLoadConfig(t *testing.T) {
	base := map[string]string{"JWT_SECRET": "s3cret"}

	tests := []struct {
		name    string
		env     map[string]string
		args    []string
		wantErr bool
		check   func(t *testing.T, c Config)
	}{
		{
			name: "defaults",
			env:  base,
			check: func(t *testing.T, c Config) {
				if c.NakamaURL != DefaultNakamaURL {
					t.Errorf("NakamaURL = %q", c.NakamaURL)
				}
				if c.ServerKey != DefaultServerKey {
					t.Errorf("ServerKey = %q", c.ServerKey)
				}
				if c.GatewayAddr != DefaultGatewayAddr {
					t.Errorf("GatewayAddr = %q", c.GatewayAddr)
				}
				if c.MapID != DefaultMapID {
					t.Errorf("MapID = %q", c.MapID)
				}
				if c.Transport != DefaultTransport {
					t.Errorf("Transport = %q, want %q", c.Transport, DefaultTransport)
				}
				if c.Timeout != DefaultTimeout {
					t.Errorf("Timeout = %s", c.Timeout)
				}
				if c.Inputs != DefaultInputs || c.MinSnapshots != DefaultMinSnapshots {
					t.Errorf("Inputs=%d MinSnapshots=%d", c.Inputs, c.MinSnapshots)
				}
			},
		},
		{
			name: "env overrides",
			env: map[string]string{
				"JWT_SECRET":        "s",
				"NAKAMA_URL":        "http://n:7350",
				"NAKAMA_SERVER_KEY": "k",
				"GATEWAY_ADDR":      ":8123",
				"SMOKE_MAP_ID":      "map_02",
				"SMOKE_TIMEOUT":     "3s",
			},
			check: func(t *testing.T, c Config) {
				if c.NakamaURL != "http://n:7350" || c.ServerKey != "k" ||
					c.GatewayAddr != ":8123" || c.MapID != "map_02" {
					t.Errorf("env not applied: %+v", c)
				}
				if c.Timeout != 3*time.Second {
					t.Errorf("Timeout = %s, want 3s", c.Timeout)
				}
			},
		},
		{
			name: "flags override env",
			env:  map[string]string{"JWT_SECRET": "s", "GATEWAY_ADDR": ":8123"},
			args: []string{"--gateway-addr=:9999", "--inputs=20", "--timeout=1s"},
			check: func(t *testing.T, c Config) {
				if c.GatewayAddr != ":9999" {
					t.Errorf("GatewayAddr = %q, want :9999", c.GatewayAddr)
				}
				if c.Inputs != 20 || c.Timeout != time.Second {
					t.Errorf("Inputs=%d Timeout=%s", c.Inputs, c.Timeout)
				}
			},
		},
		{
			name:    "missing jwt secret",
			env:     map[string]string{},
			wantErr: true,
		},
		{
			name:    "bad SMOKE_TIMEOUT",
			env:     map[string]string{"JWT_SECRET": "s", "SMOKE_TIMEOUT": "nope"},
			wantErr: true,
		},
		{
			name:    "bad flag",
			env:     base,
			args:    []string{"--no-such-flag"},
			wantErr: true,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			c, err := LoadConfig(fakeEnv(tt.env), tt.args)
			if (err != nil) != tt.wantErr {
				t.Fatalf("LoadConfig() err = %v, wantErr %v", err, tt.wantErr)
			}
			if !tt.wantErr && tt.check != nil {
				tt.check(t, c)
			}
		})
	}
}

func TestLoadConfig_Transport(t *testing.T) {
	tests := []struct {
		name    string
		env     map[string]string
		args    []string
		want    string
		wantErr bool
	}{
		{name: "default is tcp", env: map[string]string{"JWT_SECRET": "s"}, want: "tcp"},
		{name: "env kcp", env: map[string]string{"JWT_SECRET": "s", "TRANSPORT": "kcp"}, want: "kcp"},
		{
			name: "flag overrides env",
			env:  map[string]string{"JWT_SECRET": "s", "TRANSPORT": "tcp"},
			args: []string{"--transport=kcp"},
			want: "kcp",
		},
		{
			name:    "unknown transport rejected",
			env:     map[string]string{"JWT_SECRET": "s", "TRANSPORT": "quic"},
			wantErr: true,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg, err := LoadConfig(fakeEnv(tt.env), tt.args)
			if (err != nil) != tt.wantErr {
				t.Fatalf("LoadConfig() error = %v, wantErr %v", err, tt.wantErr)
			}
			if tt.wantErr {
				return
			}
			if cfg.Transport != tt.want {
				t.Errorf("Transport = %q, want %q", cfg.Transport, tt.want)
			}
		})
	}
}

func TestConfigValidate(t *testing.T) {
	valid := Config{
		JWTSecret: "s", Timeout: time.Second, Inputs: 1, MinSnapshots: 1,
		ExpectMigration: DefaultExpectMigration,
		DBPollTimeout:   DefaultDBPollTimeout,
		DBPollInterval:  DefaultDBPollInterval,
		HoldTTL:         DefaultHoldTTL,
	}

	tests := []struct {
		name    string
		mutate  func(*Config)
		wantErr bool
	}{
		{"valid", func(c *Config) {}, false},
		{"no secret", func(c *Config) { c.JWTSecret = "" }, true},
		{"zero timeout", func(c *Config) { c.Timeout = 0 }, true},
		{"zero inputs", func(c *Config) { c.Inputs = 0 }, true},
		{"zero min snapshots", func(c *Config) { c.MinSnapshots = 0 }, true},
		{"zero migration version", func(c *Config) { c.ExpectMigration = 0 }, true},
		{"zero db poll timeout", func(c *Config) { c.DBPollTimeout = 0 }, true},
		{"zero db poll interval", func(c *Config) { c.DBPollInterval = 0 }, true},
		{"negative hold ttl", func(c *Config) { c.HoldTTL = -time.Second }, true},
		{"require-db without a dsn", func(c *Config) { c.RequireDB = true }, true},
		{
			"require-db with a dsn",
			func(c *Config) { c.RequireDB = true; c.GameDBURL = "postgres://u:p@h/db" },
			false,
		},
		{
			"require-db with skip-db",
			func(c *Config) { c.RequireDB = true; c.GameDBURL = "postgres://u:p@h/db"; c.SkipDB = true },
			true,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			c := valid
			tt.mutate(&c)
			if err := c.Validate(); (err != nil) != tt.wantErr {
				t.Errorf("Validate() err = %v, wantErr %v", err, tt.wantErr)
			}
		})
	}
}

func TestNormalizeDialAddr(t *testing.T) {
	tests := []struct {
		in   string
		want string
	}{
		{":8000", "127.0.0.1:8000"},
		{"0.0.0.0:9000", "127.0.0.1:9000"},
		{"[::]:9200", "127.0.0.1:9200"},
		{"10.0.0.5:9000", "10.0.0.5:9000"},
		{"gs.example.com:9000", "gs.example.com:9000"},
		{"127.0.0.1:9200", "127.0.0.1:9200"},
	}
	for _, tt := range tests {
		t.Run(tt.in, func(t *testing.T) {
			if got := NormalizeDialAddr(tt.in); got != tt.want {
				t.Errorf("NormalizeDialAddr(%q) = %q, want %q", tt.in, got, tt.want)
			}
		})
	}
}

func TestFormatStep(t *testing.T) {
	tests := []struct {
		name     string
		result   StepResult
		contains []string
	}{
		{
			name:     "pass with detail",
			result:   StepResult{Name: "nakama_health", Latency: 12 * time.Millisecond, Detail: "url"},
			contains: []string{"PASS", "nakama_health", "12ms", "url"},
		},
		{
			name:     "fail with error",
			result:   StepResult{Name: "join", Latency: 3 * time.Millisecond, Err: errors.New("boom")},
			contains: []string{"FAIL", "join", "error: boom"},
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			line := FormatStep(tt.result)
			for _, want := range tt.contains {
				if !strings.Contains(line, want) {
					t.Errorf("FormatStep() = %q, missing %q", line, want)
				}
			}
		})
	}
}

func TestFinalLineAndSummary(t *testing.T) {
	if FinalLine(true) != "SMOKE=PASS" || FinalLine(false) != "SMOKE=FAIL" {
		t.Fatal("FinalLine wrong")
	}

	tests := []struct {
		name     string
		results  []StepResult
		wantPass bool
		wantLine string
	}{
		{"all pass", []StepResult{{Name: "a"}, {Name: "b"}}, true, "SMOKE=PASS"},
		{"one fail", []StepResult{{Name: "a"}, {Name: "b", Err: errors.New("x")}}, false, "SMOKE=FAIL"},
		{"empty is fail", nil, false, "SMOKE=FAIL"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var sb strings.Builder
			pass := WriteSummary(&sb, tt.results)
			if pass != tt.wantPass {
				t.Errorf("WriteSummary() = %v, want %v", pass, tt.wantPass)
			}
			if !strings.Contains(sb.String(), tt.wantLine) {
				t.Errorf("summary missing %q:\n%s", tt.wantLine, sb.String())
			}
		})
	}
}
