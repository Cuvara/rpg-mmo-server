// Package smoke implements the post-deploy smoke test: a headless client that
// exercises the full login -> gameplay flow (Nakama -> Gateway -> GameServer)
// against a freshly deployed stack and reports PASS/FAIL per step.
package smoke

import (
	"flag"
	"fmt"
	"io"
	"strconv"
	"strings"
	"time"

	"github.com/duycuong/rpg-mmo/shared/transport"
)

// Config holds every endpoint and knob the smoke test needs. All values can be
// set via environment variables and overridden with CLI flags.
type Config struct {
	NakamaURL     string        // NAKAMA_URL      — Nakama HTTP base URL
	ServerKey     string        // NAKAMA_SERVER_KEY — Nakama socket server key
	GatewayAddr   string        // GATEWAY_ADDR    — gateway listen addr
	Transport     string        // TRANSPORT       — gateway hop transport: tcp or kcp
	JWTSecret     string        // JWT_SECRET      — shared secret for local JWT verify
	MapID         string        // SMOKE_MAP_ID    — map to enter
	Timeout       time.Duration // SMOKE_TIMEOUT   — per network operation
	Inputs        int           // SMOKE_INPUTS    — number of MsgInput frames
	InputInterval time.Duration // SMOKE_INPUT_INTERVAL — delay between inputs
	MinSnapshots  int           // SMOKE_MIN_SNAPSHOTS — required MsgSnapshot count

	// --- persistence checks (see docs/README.md "Database checks") ---

	// DeviceID pins the Nakama device id instead of generating a random one.
	// Reusing an id reuses the account, which is occasionally useful when
	// debugging against a specific player. Empty = random per run (the default,
	// and the only mode that guarantees a clean slate).
	DeviceID string // SMOKE_DEVICE_ID

	// GameDBURL is the game-state PostgreSQL DSN. When empty the game-state
	// checks are SKIPPED rather than silently passed; --require-db turns a skip
	// into a failure. CD already writes GAME_DB_URL into deploy/.env, which the
	// post-deploy smoke step sources, so no extra credential is needed there.
	GameDBURL string // GAME_DB_URL

	// StrictAddr fails the run when the gateway advertises a listen-style
	// game-server address (":9000", "0.0.0.0:9000", "[::]:9200") instead of
	// rewriting it to loopback. Default OFF: host-mode deploys and the CD
	// post-deploy smoke step legitimately reach a bare ":9000" on the host.
	// Turn it ON for any run whose purpose is to prove that a Kubernetes /
	// Agones-allocated game server is reachable by a real client — there the
	// rewrite would hide the very defect the run is meant to catch. It applies
	// to the game-server hop only, never to GatewayAddr, which is operator
	// config rather than something a server advertised.
	StrictAddr bool // SMOKE_STRICT_ADDR

	SkipDB          bool          // SMOKE_SKIP_DB    — skip every persistence check
	RequireDB       bool          // SMOKE_REQUIRE_DB — a skipped persistence check fails the run
	ExpectMigration int           // SMOKE_EXPECT_MIGRATION — required schema_migrations version
	DBPollTimeout   time.Duration // SMOKE_DB_POLL_TIMEOUT  — deadline for the player_states row
	DBPollInterval  time.Duration // SMOKE_DB_POLL_INTERVAL — gap between polls
	HoldTTL         time.Duration // SMOKE_HOLD_TTL — game server reconnect hold, waited out before the reload check
}

// Defaults matching the dev deployment.
const (
	DefaultNakamaURL   = "http://localhost:7350"
	DefaultServerKey   = "defaultkey"
	DefaultGatewayAddr = ":8000"
	// DefaultTransport keeps the CD smoke test on TCP unless TRANSPORT says
	// otherwise. The game server hop is not configured here: it always follows
	// EnterWorldResponse.Transport.
	DefaultTransport     = "tcp"
	DefaultMapID         = "map_01"
	DefaultTimeout       = 10 * time.Second
	DefaultInputs        = 10
	DefaultInputInterval = 100 * time.Millisecond
	DefaultMinSnapshots  = 5

	// DefaultExpectMigration is the highest version in
	// backend/deploy/db/migrations/gamestate/. Bump it in the same commit that
	// adds a migration — that is the point of the assertion.
	DefaultExpectMigration = 1

	// DefaultDBPollTimeout bounds the wait for the player_states row. The game
	// server writes on the AsyncSaver sweep (30s) or when the reconnect hold
	// expires and the entity is evicted (another 30s), so the worst case is
	// ~60s; 75s leaves headroom without letting CI hang.
	DefaultDBPollTimeout  = 75 * time.Second
	DefaultDBPollInterval = time.Second

	// DefaultHoldTTL mirrors GameServerOptions.HoldTtl for a map server
	// (GameServer/Server/GameServer.cs). The reload check waits this out so the
	// rejoin really goes through the player store instead of reattaching to a
	// still-resident entity.
	DefaultHoldTTL = 30 * time.Second

	// holdGrace is added to HoldTTL before rejoining, covering clock skew and
	// the server's eviction latency.
	holdGrace = 3 * time.Second

	// reloadTolerance is the accepted gap between the position PostgreSQL holds
	// and the position the server respawns at. player_states.x is a float4, so
	// a round-trip through it loses precision relative to the float32 the wire
	// carries; the tolerance only has to absorb that, not real movement.
	reloadTolerance = 0.01
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
		Transport:     EnvOr(getenv, "TRANSPORT", DefaultTransport),
		JWTSecret:     getenv("JWT_SECRET"),
		MapID:         EnvOr(getenv, "SMOKE_MAP_ID", DefaultMapID),
		Timeout:       DefaultTimeout,
		Inputs:        DefaultInputs,
		InputInterval: DefaultInputInterval,
		MinSnapshots:  DefaultMinSnapshots,

		DeviceID:        getenv("SMOKE_DEVICE_ID"),
		GameDBURL:       getenv("GAME_DB_URL"),
		StrictAddr:      isTruthy(getenv("SMOKE_STRICT_ADDR")),
		SkipDB:          isTruthy(getenv("SMOKE_SKIP_DB")),
		RequireDB:       isTruthy(getenv("SMOKE_REQUIRE_DB")),
		ExpectMigration: DefaultExpectMigration,
		DBPollTimeout:   DefaultDBPollTimeout,
		DBPollInterval:  DefaultDBPollInterval,
		HoldTTL:         DefaultHoldTTL,
	}
	for _, d := range []struct {
		key string
		dst *time.Duration
	}{
		{"SMOKE_TIMEOUT", &cfg.Timeout},
		{"SMOKE_DB_POLL_TIMEOUT", &cfg.DBPollTimeout},
		{"SMOKE_DB_POLL_INTERVAL", &cfg.DBPollInterval},
		{"SMOKE_HOLD_TTL", &cfg.HoldTTL},
	} {
		v := getenv(d.key)
		if v == "" {
			continue
		}
		parsed, err := time.ParseDuration(v)
		if err != nil {
			return cfg, fmt.Errorf("%s: %w", d.key, err)
		}
		*d.dst = parsed
	}
	if v := getenv("SMOKE_EXPECT_MIGRATION"); v != "" {
		n, err := strconv.Atoi(v)
		if err != nil {
			return cfg, fmt.Errorf("SMOKE_EXPECT_MIGRATION: %w", err)
		}
		cfg.ExpectMigration = n
	}

	fs := flag.NewFlagSet("smoketest", flag.ContinueOnError)
	fs.StringVar(&cfg.NakamaURL, "nakama-url", cfg.NakamaURL, "Nakama HTTP base URL")
	fs.StringVar(&cfg.ServerKey, "server-key", cfg.ServerKey, "Nakama server key")
	fs.StringVar(&cfg.GatewayAddr, "gateway-addr", cfg.GatewayAddr, "Gateway address")
	fs.StringVar(&cfg.Transport, "transport", cfg.Transport, "Transport for the gateway hop: tcp or kcp")
	fs.StringVar(&cfg.JWTSecret, "jwt-secret", cfg.JWTSecret, "Shared JWT secret for local verification")
	fs.StringVar(&cfg.MapID, "map-id", cfg.MapID, "Map ID to enter")
	fs.DurationVar(&cfg.Timeout, "timeout", cfg.Timeout, "Per-operation network timeout")
	fs.IntVar(&cfg.Inputs, "inputs", cfg.Inputs, "Number of input frames to send")
	fs.DurationVar(&cfg.InputInterval, "input-interval", cfg.InputInterval, "Delay between input frames")
	fs.IntVar(&cfg.MinSnapshots, "min-snapshots", cfg.MinSnapshots, "Minimum snapshots required to pass")
	fs.StringVar(&cfg.DeviceID, "device-id", cfg.DeviceID, "Nakama device id to authenticate with (default: random per run)")
	fs.StringVar(&cfg.GameDBURL, "game-db-url", cfg.GameDBURL, "Game-state PostgreSQL DSN; unset skips the game-state checks")
	fs.BoolVar(&cfg.StrictAddr, "strict-addr", cfg.StrictAddr, "Fail when the gateway advertises a listen-style game server address instead of rewriting it to loopback")
	fs.BoolVar(&cfg.SkipDB, "skip-db", cfg.SkipDB, "Skip every persistence check (realtime flow only)")
	fs.BoolVar(&cfg.RequireDB, "require-db", cfg.RequireDB, "Fail instead of skipping when a persistence check cannot run")
	fs.IntVar(&cfg.ExpectMigration, "expect-migration-version", cfg.ExpectMigration, "Required schema_migrations version")
	fs.DurationVar(&cfg.DBPollTimeout, "db-poll-timeout", cfg.DBPollTimeout, "Deadline for the player_states row to appear")
	fs.DurationVar(&cfg.DBPollInterval, "db-poll-interval", cfg.DBPollInterval, "Gap between player_states polls")
	fs.DurationVar(&cfg.HoldTTL, "hold-ttl", cfg.HoldTTL, "Game server reconnect hold, waited out before the reload check")
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
	if err := transport.Validate(c.Transport); err != nil {
		return fmt.Errorf("transport: %w", err)
	}
	if c.ExpectMigration <= 0 {
		return fmt.Errorf("expect-migration-version must be > 0, got %d", c.ExpectMigration)
	}
	if c.DBPollTimeout <= 0 {
		return fmt.Errorf("db-poll-timeout must be > 0, got %s", c.DBPollTimeout)
	}
	if c.DBPollInterval <= 0 {
		return fmt.Errorf("db-poll-interval must be > 0, got %s", c.DBPollInterval)
	}
	if c.HoldTTL < 0 {
		return fmt.Errorf("hold-ttl must be >= 0, got %s", c.HoldTTL)
	}
	// --require-db promises the persistence checks actually ran; a config that
	// cannot run them must be rejected up front rather than at step time.
	if c.RequireDB && c.SkipDB {
		return fmt.Errorf("--require-db and --skip-db are mutually exclusive")
	}
	if c.RequireDB && c.GameDBURL == "" {
		return fmt.Errorf("--require-db needs GAME_DB_URL (env or --game-db-url) for the game-state checks")
	}
	return nil
}

// isTruthy interprets an environment flag. Anything other than the usual
// negatives enables the option, so SMOKE_SKIP_DB=1 and =true both work.
func isTruthy(v string) bool {
	switch strings.ToLower(strings.TrimSpace(v)) {
	case "", "0", "false", "no", "off":
		return false
	}
	return true
}

// NormalizeDialAddr rewrites listen-style addresses (":8000", "0.0.0.0:9000",
// "[::]:9200") into dialable loopback addresses. Real host:port pairs pass
// through untouched.
func NormalizeDialAddr(addr string) string {
	if !IsListenAddr(addr) {
		return addr
	}
	_, port := splitHostPort(addr)
	return "127.0.0.1:" + port
}

// IsListenAddr reports whether addr is listen-style — a bind address with no
// host part (":9000", "0.0.0.0:9000", "[::]:9200") rather than something a
// client can dial. The C# side keeps a matching helper (GameServer/Program.cs,
// IsHostlessAddr) so both ends agree on which addresses are listen-style.
// A loopback address a server deliberately advertised ("127.0.0.1:9000") is
// NOT listen-style: under k3d that may be exactly where the client connects.
func IsListenAddr(addr string) bool {
	host, _ := splitHostPort(addr)
	switch host {
	case "", "0.0.0.0", "::", "[::]":
		return true
	}
	return false
}

// ResolveServerDialAddr turns the ServerAddr a gateway advertised in
// MsgEnterWorldResp into the address the smoke test dials.
//
// With strict off (the default) it is exactly NormalizeDialAddr: listen-style
// addresses are rewritten to loopback, which is correct for host-mode deploys
// where a bare ":9000" really is reachable on the host.
//
// With strict on a listen-style address is a hard failure. That rewrite would
// otherwise connect to whatever else happens to sit on that port locally and
// report PASS for a game server no real client could reach — precisely the
// defect a Kubernetes/Agones run exists to catch.
func ResolveServerDialAddr(addr string, strict bool) (string, error) {
	if strict && IsListenAddr(addr) {
		return "", fmt.Errorf("strict address mode: game server advertised %q, a listen-style address no client can dial; "+
			"the game server never learned its externally-dialable address — under Agones that is the sidecar GameServer "+
			"status read (allocated address + dynamic port), otherwise set GAMESERVER_PUBLIC_ADDR to the host:port clients reach", addr)
	}
	return NormalizeDialAddr(addr), nil
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
	// Skipped marks a check that could not run for want of configuration
	// (typically GAME_DB_URL). A skip is reported as SKIP with its reason in
	// Detail — never as PASS, because "the check did not run" and "the check
	// passed" must never look the same. --require-db turns skips into errors.
	Skipped bool
}

// OK reports whether the step passed. A skipped step is not a failure.
func (r StepResult) OK() bool { return r.Err == nil }

// FormatStep renders one human-readable result line.
func FormatStep(r StepResult) string {
	status := "PASS"
	switch {
	case !r.OK():
		status = "FAIL"
	case r.Skipped:
		status = "SKIP"
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
