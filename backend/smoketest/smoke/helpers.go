// Package smoke implements the post-deploy smoke test: a headless client that
// exercises the full login -> gameplay flow (Nakama -> Gateway -> GameServer)
// against a freshly deployed stack and reports PASS/FAIL per step.
package smoke

import (
	"flag"
	"fmt"
	"io"
	"strings"
	"time"
)

// Config holds every endpoint and knob the smoke test needs. All values can be
// set via environment variables and overridden with CLI flags.
type Config struct {
	NakamaURL     string        // NAKAMA_URL      — Nakama HTTP base URL
	ServerKey     string        // NAKAMA_SERVER_KEY — Nakama socket server key
	GatewayAddr   string        // GATEWAY_ADDR    — gateway TCP listen addr
	JWTSecret     string        // JWT_SECRET      — shared secret for local JWT verify
	MapID         string        // SMOKE_MAP_ID    — map to enter
	Timeout       time.Duration // SMOKE_TIMEOUT   — per network operation
	Inputs        int           // SMOKE_INPUTS    — number of MsgInput frames
	InputInterval time.Duration // SMOKE_INPUT_INTERVAL — delay between inputs
	MinSnapshots  int           // SMOKE_MIN_SNAPSHOTS — required MsgSnapshot count
}

// Defaults matching the dev deployment.
const (
	DefaultNakamaURL     = "http://localhost:7350"
	DefaultServerKey     = "defaultkey"
	DefaultGatewayAddr   = ":8000"
	DefaultMapID         = "map_01"
	DefaultTimeout       = 10 * time.Second
	DefaultInputs        = 10
	DefaultInputInterval = 100 * time.Millisecond
	DefaultMinSnapshots  = 5
)

// EnvOr returns getenv(key) or def when the variable is unset/empty.
func EnvOr(getenv func(string) string, key, def string) string {
	if v := getenv(key); v != "" {
		return v
	}
	return def
}

// LoadConfig builds a Config from the environment, then applies CLI flags on
// top. getenv is injected for testability (pass os.Getenv in production).
func LoadConfig(getenv func(string) string, args []string) (Config, error) {
	cfg := Config{
		NakamaURL:     EnvOr(getenv, "NAKAMA_URL", DefaultNakamaURL),
		ServerKey:     EnvOr(getenv, "NAKAMA_SERVER_KEY", DefaultServerKey),
		GatewayAddr:   EnvOr(getenv, "GATEWAY_ADDR", DefaultGatewayAddr),
		JWTSecret:     getenv("JWT_SECRET"),
		MapID:         EnvOr(getenv, "SMOKE_MAP_ID", DefaultMapID),
		Timeout:       DefaultTimeout,
		Inputs:        DefaultInputs,
		InputInterval: DefaultInputInterval,
		MinSnapshots:  DefaultMinSnapshots,
	}
	if v := getenv("SMOKE_TIMEOUT"); v != "" {
		d, err := time.ParseDuration(v)
		if err != nil {
			return cfg, fmt.Errorf("SMOKE_TIMEOUT: %w", err)
		}
		cfg.Timeout = d
	}

	fs := flag.NewFlagSet("smoketest", flag.ContinueOnError)
	fs.StringVar(&cfg.NakamaURL, "nakama-url", cfg.NakamaURL, "Nakama HTTP base URL")
	fs.StringVar(&cfg.ServerKey, "server-key", cfg.ServerKey, "Nakama server key")
	fs.StringVar(&cfg.GatewayAddr, "gateway-addr", cfg.GatewayAddr, "Gateway TCP address")
	fs.StringVar(&cfg.JWTSecret, "jwt-secret", cfg.JWTSecret, "Shared JWT secret for local verification")
	fs.StringVar(&cfg.MapID, "map-id", cfg.MapID, "Map ID to enter")
	fs.DurationVar(&cfg.Timeout, "timeout", cfg.Timeout, "Per-operation network timeout")
	fs.IntVar(&cfg.Inputs, "inputs", cfg.Inputs, "Number of input frames to send")
	fs.DurationVar(&cfg.InputInterval, "input-interval", cfg.InputInterval, "Delay between input frames")
	fs.IntVar(&cfg.MinSnapshots, "min-snapshots", cfg.MinSnapshots, "Minimum snapshots required to pass")
	if err := fs.Parse(args); err != nil {
		return cfg, err
	}
	return cfg, cfg.Validate()
}

// Validate rejects configurations the runner cannot execute.
func (c Config) Validate() error {
	if c.JWTSecret == "" {
		return fmt.Errorf("JWT_SECRET is required (env or --jwt-secret)")
	}
	if c.Timeout <= 0 {
		return fmt.Errorf("timeout must be > 0, got %s", c.Timeout)
	}
	if c.Inputs <= 0 {
		return fmt.Errorf("inputs must be > 0, got %d", c.Inputs)
	}
	if c.MinSnapshots <= 0 {
		return fmt.Errorf("min-snapshots must be > 0, got %d", c.MinSnapshots)
	}
	return nil
}

// NormalizeDialAddr rewrites listen-style addresses (":8000", "0.0.0.0:9000",
// "[::]:9200") into dialable loopback addresses. Real host:port pairs pass
// through untouched.
func NormalizeDialAddr(addr string) string {
	host, port := splitHostPort(addr)
	switch host {
	case "", "0.0.0.0", "::", "[::]":
		return "127.0.0.1:" + port
	}
	return addr
}

// splitHostPort is a forgiving split on the last colon; it tolerates the
// bracketed-IPv6 form the net package produces for listener addresses.
func splitHostPort(addr string) (host, port string) {
	i := strings.LastIndex(addr, ":")
	if i < 0 {
		return addr, ""
	}
	host = strings.Trim(addr[:i], "[]")
	return host, addr[i+1:]
}

// StepResult is the outcome of a single smoke step.
type StepResult struct {
	Name    string
	Latency time.Duration
	Err     error
	Detail  string
}

// OK reports whether the step passed.
func (r StepResult) OK() bool { return r.Err == nil }

// FormatStep renders one human-readable result line.
func FormatStep(r StepResult) string {
	status := "PASS"
	if !r.OK() {
		status = "FAIL"
	}
	line := fmt.Sprintf("%-4s  %-22s %8s", status, r.Name, r.Latency.Round(time.Millisecond))
	if r.Detail != "" {
		line += "  " + r.Detail
	}
	if r.Err != nil {
		line += "  error: " + r.Err.Error()
	}
	return line
}

// FinalLine renders the machine-readable verdict consumed by CI.
func FinalLine(pass bool) string {
	if pass {
		return "SMOKE=PASS"
	}
	return "SMOKE=FAIL"
}

// WriteSummary prints every step line plus the final verdict to w and returns
// the overall pass/fail.
func WriteSummary(w io.Writer, results []StepResult) bool {
	pass := len(results) > 0
	fmt.Fprintln(w, "--- smoke test summary ---")
	for _, r := range results {
		fmt.Fprintln(w, FormatStep(r))
		if !r.OK() {
			pass = false
		}
	}
	fmt.Fprintln(w, FinalLine(pass))
	return pass
}
