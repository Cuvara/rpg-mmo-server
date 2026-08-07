// Package load implements the load generator: N concurrent virtual players that
// walk the real wire protocol (Nakama -> Gateway -> GameServer) and record
// client-observed latency, throughput and error counters, alongside server-side
// Prometheus counters scraped from /metrics.
//
// It is deliberately a sibling of `smoketest`: the same handshake, the same
// shared/messages codec, the same delta-merge semantics. smoketest answers "does
// one player work"; loadtest answers "how many players before it stops working".
package load

import (
	"flag"
	"fmt"
	"strings"
	"time"

	"github.com/duycuong/rpg-mmo/shared/transport"
)

// AuthMode selects how a virtual player obtains its gateway JWT.
type AuthMode string

const (
	// AuthPresigned mints the gateway JWT locally with the shared HS256 secret,
	// exactly as Nakama's gateway_token RPC would. This is the DEFAULT and it is
	// a deliberate choice: the mission is to benchmark the realtime game path
	// (gateway handshake + game-server tick loop), and driving real Nakama auth
	// would fold Nakama's HTTP stack, its Postgres round-trips and its account
	// creation cost into a number that is supposed to describe the game server.
	// A ramp of 200 players would measure Nakama's login throughput, not the
	// tick budget. Use AuthNakama only when login throughput IS the question.
	AuthPresigned AuthMode = "presigned"
	// AuthNakama drives the real device-auth + gateway_token RPC path per player.
	AuthNakama AuthMode = "nakama"
)

// JoinMode selects how a virtual player reaches the game server.
type JoinMode string

const (
	// JoinGateway walks the production handshake: MsgAuth + MsgEnterWorld against
	// the gateway, then dial whatever ServerAddr/JoinToken it hands back. This is
	// the realistic path and the default.
	//
	// It cannot be swept to high player counts against a stock deployment, and
	// that is a deployment fact rather than a limitation of this tool. Two config
	// ceilings bite first, both far below where the tick loop breaks:
	//
	//   - GATEWAY_CONN_RATE_PER_MIN defaults to 10 connections per minute per
	//     source IP (shared/config). Every virtual player shares one source IP,
	//     so player 11 is rate-limited.
	//   - The server registry advertises capacity=100 and the gateway refuses
	//     allocation once PlayerCount >= Capacity (gateway/registry/registry.go).
	//
	// Use this mode to verify the full path and to measure the gateway itself;
	// use JoinDirect to find where the game server actually breaks.
	JoinGateway JoinMode = "gateway"
	// JoinDirect mints the join token locally with the join-token secret and
	// dials the game server straight away, skipping the gateway.
	//
	// This is not a cheat: ADR-3 says the gateway is a redirector that is
	// deliberately NOT in the gameplay data path, so a game-server capacity
	// number is not supposed to include it. The token is the real thing — an
	// HS256 JWT with the `sid` claim the server checks — so the server-side code
	// path from join onward is byte-for-byte the production one.
	JoinDirect JoinMode = "direct"
)

// Movement selects the input pattern every virtual player drives.
//
// The mode is the experiment control, not a cosmetic knob. The game server's
// tick does three O(n) things per connected client — an AOI distance scan over
// every entity, a delta diff against the last view sent, and JSON serialization
// of whatever changed — so the whole tick is O(n^2) three times over. Only the
// third term depends on whether entities actually moved:
//
//	MovementStill:   positions never change -> deltas are empty -> the JSON term
//	                 collapses to ~0 while the scan and diff terms stay O(n^2).
//	MovementCluster: every entity moves every tick, and every entity is inside
//	                 every other entity's AOI -> the JSON term is fully O(n^2).
//
// Running the same player count in both modes isolates serialization cost from
// scan cost with no server-side change required.
const (
	// MovementStill sends zero-vector input: ack still advances (LastInputTick is
	// bumped before the deadzone check), but no position changes.
	MovementStill = "still"
	// MovementCluster moves every player along +X. They spawn at the origin and
	// stay mutually in-AOI, so this is the worst-case dense-crowd shape.
	MovementCluster = "cluster"
	// MovementSpread gives each player a distinct heading in the +X/+Y quadrant.
	// NOTE: at the default 5 u/s and a 50-unit AOI radius, a 60s run cannot
	// actually separate more than ~9 players out of AOI, so this mode is NOT a
	// low-density control — it is only useful for exercising bounds clamping.
	// Use MovementStill as the "cheap serialization" control instead.
	MovementSpread = "spread"
)

// Config holds every knob of a load run.
type Config struct {
	// --- topology ---
	NakamaURL   string
	ServerKey   string
	GatewayAddr string
	Transport   string
	JWTSecret   string
	MapID       string

	// --- direct-join topology (JoinDirect only) ---
	JoinMode        JoinMode
	GameServerAddr  string // dialed directly, e.g. 127.0.0.1:9200
	ServerID        string // must equal the server's GAMESERVER_ID (the `sid` claim)
	JoinTokenSecret string // JOIN_TOKEN_SECRET, falling back to JWTSecret

	// --- load shape ---
	Players  int
	RampRate float64       // players started per second; <=0 means all at once
	Duration time.Duration // measurement window AFTER the ramp completes
	TickRate int           // client input sends per second
	AuthMode AuthMode
	Movement string

	// --- plumbing ---
	Timeout      time.Duration
	HoldGateway  bool // keep the gateway socket open for the whole run
	GSMetricsURL string
	GWMetricsURL string
	JSONOut      string
	Label        string
	Warmup       time.Duration // discarded after the last player joins
}

// Defaults matching the dev deployment (deploy/docker-compose.yml).
const (
	DefaultNakamaURL   = "http://localhost:7350"
	DefaultServerKey   = "defaultkey"
	DefaultGatewayAddr = ":8000"
	DefaultTransport   = "tcp"
	DefaultMapID       = "map_01"
	DefaultTimeout     = 15 * time.Second
	// DefaultTickRate matches GameConstants.DefaultTickRate (15Hz). A client that
	// sends faster gains nothing: the tick loop coalesces to the newest input per
	// player per tick.
	DefaultTickRate     = 15
	DefaultDuration     = 60 * time.Second
	DefaultWarmup       = 5 * time.Second
	DefaultGSMetricsURL = "http://localhost:9101/metrics"
	DefaultGWMetricsURL = "http://localhost:9102/metrics"
	// DefaultGameServerAddr / DefaultServerID match deploy/docker-compose.yml's
	// published port and GAMESERVER_ID. Only used by JoinDirect.
	DefaultGameServerAddr = "127.0.0.1:9200"
	DefaultServerID       = "gs-dotnet-map_01"
)

// TickBudget is the acceptance threshold from ADR-7: at 15Hz a tick must finish
// inside its period, 1/15s = 66.67ms. gameserver_tick_duration_seconds p99 above
// this means the server cannot hold its simulation rate.
const TickBudget = time.Second / DefaultTickRate

// LoadConfig builds a Config from environment defaults, then applies CLI flags.
// getenv is injected for testability (pass os.Getenv in production).
func LoadConfig(getenv func(string) string, args []string) (Config, error) {
	cfg := Config{
		NakamaURL:       envOr(getenv, "NAKAMA_URL", DefaultNakamaURL),
		ServerKey:       envOr(getenv, "NAKAMA_SERVER_KEY", DefaultServerKey),
		GatewayAddr:     envOr(getenv, "GATEWAY_ADDR", DefaultGatewayAddr),
		Transport:       envOr(getenv, "TRANSPORT", DefaultTransport),
		JWTSecret:       getenv("JWT_SECRET"),
		MapID:           envOr(getenv, "LOADTEST_MAP_ID", DefaultMapID),
		Players:         10,
		RampRate:        20,
		Duration:        DefaultDuration,
		TickRate:        DefaultTickRate,
		AuthMode:        AuthPresigned,
		Movement:        MovementCluster,
		JoinMode:        JoinGateway,
		GameServerAddr:  envOr(getenv, "GAMESERVER_PUBLIC_ADDR", DefaultGameServerAddr),
		ServerID:        envOr(getenv, "GAMESERVER_ID", DefaultServerID),
		JoinTokenSecret: getenv("JOIN_TOKEN_SECRET"),
		Timeout:         DefaultTimeout,
		HoldGateway:     true,
		GSMetricsURL:    envOr(getenv, "GAMESERVER_METRICS_URL", DefaultGSMetricsURL),
		GWMetricsURL:    envOr(getenv, "GATEWAY_METRICS_URL", DefaultGWMetricsURL),
		Warmup:          DefaultWarmup,
	}

	authMode := string(cfg.AuthMode)
	joinMode := string(cfg.JoinMode)
	fs := flag.NewFlagSet("loadtest", flag.ContinueOnError)
	fs.StringVar(&joinMode, "join", joinMode, "Join path: gateway (default, realistic) or direct (skip the gateway; needed above its conn-rate/capacity ceilings)")
	fs.StringVar(&cfg.GameServerAddr, "gameserver-addr", cfg.GameServerAddr, "Game server address dialed by -join=direct")
	fs.StringVar(&cfg.ServerID, "server-id", cfg.ServerID, "Game server id for the join token's sid claim (-join=direct)")
	fs.StringVar(&cfg.JoinTokenSecret, "join-token-secret", cfg.JoinTokenSecret, "JOIN_TOKEN_SECRET; defaults to the JWT secret, as the server does")
	fs.StringVar(&cfg.NakamaURL, "nakama-url", cfg.NakamaURL, "Nakama HTTP base URL (only used with -auth=nakama)")
	fs.StringVar(&cfg.ServerKey, "server-key", cfg.ServerKey, "Nakama server key")
	fs.StringVar(&cfg.GatewayAddr, "gateway-addr", cfg.GatewayAddr, "Gateway address")
	fs.StringVar(&cfg.Transport, "transport", cfg.Transport, "Transport for the gateway hop: tcp or kcp")
	fs.StringVar(&cfg.JWTSecret, "jwt-secret", cfg.JWTSecret, "Shared HS256 secret (env JWT_SECRET)")
	fs.StringVar(&cfg.MapID, "map-id", cfg.MapID, "Map ID to enter")
	fs.IntVar(&cfg.Players, "players", cfg.Players, "Number of concurrent virtual players")
	fs.Float64Var(&cfg.RampRate, "ramp", cfg.RampRate, "Players started per second (<=0 = all at once)")
	fs.DurationVar(&cfg.Duration, "duration", cfg.Duration, "Measurement window after the ramp completes")
	fs.IntVar(&cfg.TickRate, "tick-rate", cfg.TickRate, "Client input sends per second")
	fs.StringVar(&authMode, "auth", authMode, "Auth path: presigned (default, benchmarks the game path) or nakama (adds real login cost)")
	fs.StringVar(&cfg.Movement, "movement", cfg.Movement, "Input pattern: cluster, still or spread")
	fs.DurationVar(&cfg.Timeout, "timeout", cfg.Timeout, "Per-operation network timeout")
	fs.BoolVar(&cfg.HoldGateway, "hold-gateway", cfg.HoldGateway, "Keep the gateway socket open for the whole run (as a real client does)")
	fs.StringVar(&cfg.GSMetricsURL, "gameserver-metrics", cfg.GSMetricsURL, "Game server /metrics URL ('' to skip)")
	fs.StringVar(&cfg.GWMetricsURL, "gateway-metrics", cfg.GWMetricsURL, "Gateway /metrics URL ('' to skip)")
	fs.StringVar(&cfg.JSONOut, "json", cfg.JSONOut, "Write the machine-readable result to this path ('-' for stdout)")
	fs.StringVar(&cfg.Label, "label", cfg.Label, "Free-form label recorded in the JSON result")
	fs.DurationVar(&cfg.Warmup, "warmup", cfg.Warmup, "Settling time discarded after the last player joins")
	if err := fs.Parse(args); err != nil {
		return cfg, err
	}
	cfg.AuthMode = AuthMode(authMode)
	cfg.JoinMode = JoinMode(joinMode)
	// Mirror the server's own fallback: an unset JOIN_TOKEN_SECRET means join
	// tokens are signed and verified with JWT_SECRET (Program.cs:141).
	if cfg.JoinTokenSecret == "" {
		cfg.JoinTokenSecret = cfg.JWTSecret
	}
	return cfg, cfg.Validate()
}

// Validate rejects configurations the runner cannot execute.
func (c Config) Validate() error {
	if c.JWTSecret == "" {
		return fmt.Errorf("JWT_SECRET is required (env or -jwt-secret)")
	}
	if c.Players <= 0 {
		return fmt.Errorf("players must be > 0, got %d", c.Players)
	}
	if c.TickRate <= 0 {
		return fmt.Errorf("tick-rate must be > 0, got %d", c.TickRate)
	}
	if c.Duration <= 0 {
		return fmt.Errorf("duration must be > 0, got %s", c.Duration)
	}
	if c.Timeout <= 0 {
		return fmt.Errorf("timeout must be > 0, got %s", c.Timeout)
	}
	if c.Warmup < 0 {
		return fmt.Errorf("warmup must be >= 0, got %s", c.Warmup)
	}
	switch c.AuthMode {
	case AuthPresigned, AuthNakama:
	default:
		return fmt.Errorf("auth must be %q or %q, got %q", AuthPresigned, AuthNakama, c.AuthMode)
	}
	switch c.Movement {
	case MovementStill, MovementCluster, MovementSpread:
	default:
		return fmt.Errorf("movement must be one of still|cluster|spread, got %q", c.Movement)
	}
	switch c.JoinMode {
	case JoinGateway:
	case JoinDirect:
		if c.GameServerAddr == "" {
			return fmt.Errorf("join=direct requires -gameserver-addr")
		}
		if c.JoinTokenSecret == "" {
			return fmt.Errorf("join=direct requires a join-token secret (JOIN_TOKEN_SECRET or JWT_SECRET)")
		}
	default:
		return fmt.Errorf("join must be %q or %q, got %q", JoinGateway, JoinDirect, c.JoinMode)
	}
	if err := transport.Validate(c.Transport); err != nil {
		return fmt.Errorf("transport: %w", err)
	}
	return nil
}

// InputInterval is the wall-clock gap between two MsgInput frames per player.
func (c Config) InputInterval() time.Duration {
	return time.Second / time.Duration(c.TickRate)
}

// RampDuration is how long the ramp phase takes at the configured rate.
func (c Config) RampDuration() time.Duration {
	if c.RampRate <= 0 {
		return 0
	}
	return time.Duration(float64(c.Players) / c.RampRate * float64(time.Second))
}

func envOr(getenv func(string) string, key, def string) string {
	if v := getenv(key); v != "" {
		return v
	}
	return def
}

// NormalizeDialAddr rewrites listen-style addresses (":8000", "0.0.0.0:9000",
// "[::]:9200") into dialable loopback addresses. Real host:port pairs pass
// through untouched. Mirrors smoke.NormalizeDialAddr.
func NormalizeDialAddr(addr string) string {
	host, port := splitHostPort(addr)
	switch host {
	case "", "0.0.0.0", "::", "[::]":
		return "127.0.0.1:" + port
	}
	return addr
}

func splitHostPort(addr string) (host, port string) {
	i := strings.LastIndex(addr, ":")
	if i < 0 {
		return addr, ""
	}
	host = strings.Trim(addr[:i], "[]")
	return host, addr[i+1:]
}
