package main

import (
	"context"
	"flag"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/events"
	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/server"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// Backend selects which storage implementations the gateway runs against.
const (
	backendMemory = "memory"
	backendRedis  = "redis"

	// streamLossSampleInterval is how often main samples the event stream's
	// consumer-group recovery count into the Prometheus counter. Group loss is
	// a rare, operator-visible event, so a coarse interval is plenty.
	streamLossSampleInterval = 10 * time.Second
)

// Allocator modes: how the gateway reacts to a map no live server serves.
const (
	allocatorNone   = "none"
	allocatorAgones = "agones"
)

func main() {
	addr := flag.String("addr", "", "Listen address (overrides GATEWAY_ADDR)")
	backend := flag.String("backend", "", "Store backend: memory or redis (default: redis when REDIS_ADDR is set, else memory)")
	transportKind := flag.String("transport", "", "Realtime transport: tcp or kcp (overrides GATEWAY_TRANSPORT, default tcp)")
	instanceID := flag.String("instance-id", "", "Gateway instance id, used as the event-stream consumer name (default: hostname)")
	allocatorMode := flag.String("allocator", "", "Game server allocator: none or agones (overrides ALLOCATOR; default none)")
	allocNamespace := flag.String("allocator-namespace", "", "Kubernetes namespace holding the Agones fleets (overrides ALLOCATOR_NAMESPACE)")
	allocFleetMap := flag.String("allocator-fleet-map", "", "Agones Fleet for map servers (overrides ALLOCATOR_FLEET_MAP)")
	allocFleetDungeon := flag.String("allocator-fleet-dungeon", "", "Agones Fleet for dungeon servers (overrides ALLOCATOR_FLEET_DUNGEON)")
	allocTransport := flag.String("allocator-transport", "", "Realtime transport the allocated fleet's game servers listen with: tcp or kcp (overrides ALLOCATOR_TRANSPORT; defaults to --transport)")
	metricsAddr := flag.String("metrics-addr", "", "Prometheus metrics listen address, e.g. :9102 (overrides METRICS_ADDR; \"off\" or an empty METRICS_ADDR disables it)")
	allocWaitTimeout := flag.Duration("allocation-wait-timeout", 0, "How long to wait for a freshly allocated game server to register itself before failing the join as retryable (overrides ALLOCATION_WAIT_TIMEOUT; default 20s)")
	allocPollInterval := flag.Duration("allocation-poll-interval", 0, "How often to re-check the registry while waiting for an allocated game server (overrides ALLOCATION_POLL_INTERVAL; default 250ms)")
	allocKubeconfig := flag.String("allocator-kubeconfig", "", "Kubeconfig path for the allocator (default: in-cluster config, then $KUBECONFIG, then ~/.kube/config)")
	transportKey := flag.String("transport-key", "", "Pre-shared key encrypting the KCP listener, 32-byte hex recommended (overrides TRANSPORT_KEY; empty = plaintext)")
	joinTokenSecret := flag.String("join-token-secret", "", "HS256 secret (comma-separated list to rotate) for gateway->gameserver join tokens (overrides JOIN_TOKEN_SECRET; REQUIRED)")
	connRate := flag.Float64("conn-rate-per-min", -1, "Max accepted connections per minute per source IP (overrides GATEWAY_CONN_RATE_PER_MIN; 0 disables)")
	msgRate := flag.Float64("msg-rate-per-sec", -1, "Max inbound messages per second per connection (overrides GATEWAY_MSG_RATE_PER_SEC; 0 disables)")
	flag.Parse()

	cfg := config.Load()
	log := logger.New(cfg.LogLevel)

	listenAddr := cfg.GatewayAddr
	if *addr != "" {
		listenAddr = *addr
	}

	listenTransport := cfg.GatewayTransport
	if *transportKind != "" {
		listenTransport = *transportKind
	}
	if err := transport.Validate(listenTransport); err != nil {
		log.Error("invalid transport", "err", err)
		os.Exit(1)
	}

	// --- Security configuration -------------------------------------------
	//
	// Three independent secrets, each with an explicit "insecure default" that
	// is legal for local dev and loudly logged so nobody ships it:
	//   TRANSPORT_KEY      — KCP wire encryption. Empty = plaintext.
	//   JWT_SECRET         — Nakama-issued client auth token.
	//   JOIN_TOKEN_SECRET  — gateway-issued join token. REQUIRED (fatal if unset).
	tKey := cfg.TransportKey
	if *transportKey != "" {
		tKey = *transportKey
	}
	if tKey != "" {
		if _, kerr := transport.DeriveKey(tKey); kerr != nil {
			log.Error("invalid transport key", "err", kerr)
			os.Exit(1)
		}
	}

	joinSecret := cfg.JoinTokenSecret
	if *joinTokenSecret != "" {
		joinSecret = *joinTokenSecret
	}
	if joinSecret == "" {
		log.Error("JOIN_TOKEN_SECRET is required but not set -- refusing to start. " +
			"Set JOIN_TOKEN_SECRET to a dedicated secret (and the matching value on every game server). " +
			"Do NOT reuse JWT_SECRET: a compromised game-server pod must not be able to forge auth tokens.")
		os.Exit(1)
	}
	if joinSecret == cfg.JWTSecret {
		log.Warn("JOIN_TOKEN_SECRET and JWT_SECRET are the same value -- a leak of either secret compromises both the Nakama auth hop and the game-server hop. Use distinct secrets before launch.")
	}
	if cfg.JWTSecret == "dev-secret-change-me" {
		log.Warn("JWT_SECRET is the built-in development default -- anyone can forge auth tokens. Set JWT_SECRET before exposing this gateway.")
	}

	// Rate limits: flags win, then env (via config), then the built-in
	// defaults. A negative flag means "unset"; 0 explicitly disables a limiter.
	connRatePerMin := cfg.GatewayConnRatePerMin
	if *connRate >= 0 {
		connRatePerMin = *connRate
	}
	msgRatePerSec := cfg.GatewayMsgRatePerSec
	if *msgRate >= 0 {
		msgRatePerSec = *msgRate
	}
	connBurst := cfg.GatewayConnBurst
	if connBurst < 1 {
		connBurst = 1
	}
	msgBurst := cfg.GatewayMsgBurst
	if msgBurst < 1 {
		msgBurst = 1
	}

	// Metrics listener: separate port from the realtime listener, started before
	// anything else so a crash-looping gateway is still scrapeable/probeable.
	met, promReg := metrics.NewDefault()
	// Readiness checks are registered below once the backend is known — the
	// metrics listener starts first on purpose, so probes work even if backend
	// wiring fails.
	ready := metrics.NewReadiness()
	metricsSrv, err := metrics.ServeWithChecks(resolveMetricsAddr(*metricsAddr), promReg, ready, log)
	if err != nil {
		log.Error("metrics listener failed", "err", err)
		os.Exit(1)
	}

	mode, err := resolveBackend(*backend)
	if err != nil {
		log.Error("invalid backend", "err", err)
		os.Exit(1)
	}

	var (
		sessionStore   storage.SessionStore
		serverRegistry storage.ServerRegistry
		eventStream    storage.EventStream
		closers        []func() error
		// redisStream is non-nil only on the Redis backend; it exposes the
		// consumer-group recovery counter that feeds
		// gateway_stream_group_loss_total.
		redisStream *redisstore.EventStream
	)

	switch mode {
	case backendRedis:
		consumer := *instanceID
		if consumer == "" {
			consumer, _ = os.Hostname()
		}
		if consumer == "" {
			consumer = "gateway"
		}
		// One shared client/pool for all three Redis-backed stores.
		client := redisstore.NewRedisClient(cfg.RedisAddr, cfg.RedisPassword)
		sess := redisstore.NewSessionStoreWithClient(client)
		reg := redisstore.NewServerRegistryWithClient(client, 0)
		stream := redisstore.NewEventStreamWithClient(client, "gateway", consumer)
		stream.SetLogger(log)
		sessionStore, serverRegistry, eventStream = sess, reg, stream
		closers = append(closers, stream.Close, client.Close)

		// Redis is a real dependency of login and map assignment, so it gates
		// readiness (not liveness — see metrics.HandlerWithChecks). The probe
		// doubles as the sampler for gateway_redis_up.
		ready.Register("redis", func(ctx context.Context) error {
			err := redisstore.Ping(ctx, client, redisstore.DefaultDialTimeout)
			met.SetRedisUp(err == nil)
			return err
		})
		redisStream = stream
		log.Info("using redis backend", "addr", cfg.RedisAddr, "consumer", consumer)
	default:
		sessionStore = storage.NewMemorySessionStore()
		serverRegistry = storage.NewMemoryServerRegistry()
		eventStream = storage.NewMemoryEventStream()
		closers = append(closers, eventStream.Close)
		log.Info("using in-memory backend (single process)")
	}

	// The gateway_id labels every session this instance creates, enabling
	// cross-gateway duplicate-login coordination.
	gatewayID := *instanceID
	if gatewayID == "" {
		gatewayID, _ = os.Hostname()
	}
	if gatewayID == "" {
		gatewayID = "gateway"
	}
	sessions := session.NewSessionManager(sessionStore, gatewayID)

	// Allocator: with --allocator=agones the registry asks the Agones allocation
	// API for a GameServer whenever no live server can serve a map. Without it
	// an unserved map is simply an error (the pre-Agones behaviour).
	reg := registry.NewRegistryService(serverRegistry, registry.WithMetrics(met), registry.WithLogger(log))
	allocMode, err := resolveAllocator(*allocatorMode)
	if err != nil {
		log.Error("invalid allocator", "err", err)
		os.Exit(1)
	}
	if allocMode == allocatorAgones {
		agonesCfg := registry.AgonesConfig{
			Namespace:    firstNonEmpty(*allocNamespace, os.Getenv("ALLOCATOR_NAMESPACE"), registry.DefaultNamespace),
			FleetMap:     firstNonEmpty(*allocFleetMap, os.Getenv("ALLOCATOR_FLEET_MAP"), registry.DefaultFleetMap),
			FleetDungeon: firstNonEmpty(*allocFleetDungeon, os.Getenv("ALLOCATOR_FLEET_DUNGEON"), registry.DefaultFleetDungeon),
			Kubeconfig:   firstNonEmpty(*allocKubeconfig, os.Getenv("ALLOCATOR_KUBECONFIG")),
			// Allocated servers are announced to clients before the pod's own
			// registration lands, so the allocator must know what the fleet
			// speaks. Falls back to the gateway's own transport, which is the
			// right guess for a uniform rollout.
			Transport: firstNonEmpty(*allocTransport, os.Getenv("ALLOCATOR_TRANSPORT"), listenTransport),
		}
		if terr := transport.Validate(agonesCfg.Transport); terr != nil {
			log.Error("invalid allocator transport", "err", terr)
			os.Exit(1)
		}
		alloc, aerr := registry.NewAgonesAllocator(agonesCfg)
		if aerr != nil {
			log.Error("agones allocator init failed", "err", aerr)
			os.Exit(1)
		}
		// An allocated pod is not dialable the moment the allocation API
		// answers: it still has to boot, bind, report Ready and self-register.
		// The registry waits for its own entry before a join token is minted,
		// bounded by these two knobs (flag wins, then env, then default).
		waitTimeout := resolveDuration(*allocWaitTimeout, "ALLOCATION_WAIT_TIMEOUT", registry.DefaultAllocationWaitTimeout, log)
		pollInterval := resolveDuration(*allocPollInterval, "ALLOCATION_POLL_INTERVAL", registry.DefaultAllocationPollInterval, log)
		reg = registry.NewRegistryServiceWithAllocator(serverRegistry, alloc,
			registry.WithMetrics(met), registry.WithLogger(log),
			registry.WithAllocationWait(waitTimeout, pollInterval))
		log.Info("agones allocator enabled",
			"namespace", agonesCfg.Namespace,
			"fleet_map", agonesCfg.FleetMap,
			"fleet_dungeon", agonesCfg.FleetDungeon,
			"transport", agonesCfg.Transport,
			"allocation_wait_timeout", waitTimeout,
			"allocation_poll_interval", pollInterval,
		)
	} else {
		log.Info("allocator disabled (unserved maps return an error)")
	}

	// The stream owns the recovery count (it lives in shared/, which must not
	// import the gateway's metrics package), so main samples it into the
	// counter. Sampling a monotonic count and adding the delta keeps the
	// Prometheus counter monotonic too.
	if redisStream != nil {
		go func() {
			ticker := time.NewTicker(streamLossSampleInterval)
			defer ticker.Stop()
			var reported int64
			for range ticker.C {
				if cur := redisStream.GroupLosses(); cur > reported {
					met.StreamGroupLost(cur - reported)
					reported = cur
				}
			}
		}()
	}

	// The relay's sink is the gateway and the gateway owns the relay, so the sink
	// is a closure: it only fires after Run starts the relay, when gw is set.
	var gw *server.Gateway
	relay := events.NewRelay(eventStream, events.DefaultStream,
		events.SinkFunc(func(ev storage.Event) { gw.OnEvent(ev) }), log)
	gw = server.New(sessions, reg, cfg.JWTSecret, log,
		server.WithEventRelay(relay), server.WithTransport(listenTransport),
		server.WithMetrics(met),
		server.WithTransportKey(tKey),
		server.WithJoinTokenSecret(joinSecret),
		// The per-IP limiter is configured per minute (the natural unit for a
		// login rate) but the bucket refills per second.
		server.WithConnRateLimit(connRatePerMin/60, connBurst),
		server.WithMsgRateLimit(msgRatePerSec, msgBurst),
	)

	// Graceful shutdown on SIGINT/SIGTERM.
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	go func() {
		<-sigCh
		log.Info("shutting down gateway")
		gw.Shutdown()
		if serr := metricsSrv.Shutdown(); serr != nil {
			log.Warn("stop metrics listener", "err", serr)
		}
		for _, c := range closers {
			if err := c(); err != nil {
				log.Warn("close resource", "err", err)
			}
		}
	}()

	log.Info("starting gateway",
		slog.String("addr", listenAddr),
		slog.String("backend", mode),
		slog.String("transport", transport.Normalize(listenTransport)),
		slog.Bool("transport_encrypted", tKey != ""),
		slog.Float64("conn_rate_per_min", connRatePerMin),
		slog.Float64("msg_rate_per_sec", msgRatePerSec))
	if err := gw.Run(listenAddr); err != nil {
		log.Error("gateway exited with error", "err", err)
		os.Exit(1)
	}
}

// resolveMetricsAddr picks the metrics listen address from the --metrics-addr
// flag, the METRICS_ADDR env var, or DefaultAddr. "off"/"none"/"disabled" (and
// an explicitly exported empty METRICS_ADDR) turn the listener off; the empty
// string returned here means "disabled".
func resolveMetricsAddr(flagValue string) string {
	addr := flagValue
	if addr == "" {
		env, ok := os.LookupEnv("METRICS_ADDR")
		if !ok {
			return metrics.DefaultAddr
		}
		addr = env
	}
	switch addr {
	case "", "off", "none", "disabled":
		return ""
	}
	return addr
}

// resolveBackend picks the store backend from the --backend flag, the
// GATEWAY_BACKEND env var, or the presence of an explicit REDIS_ADDR.
func resolveBackend(flagValue string) (string, error) {
	mode := flagValue
	if mode == "" {
		mode = os.Getenv("GATEWAY_BACKEND")
	}
	if mode == "" {
		// config.Load defaults RedisAddr to localhost:6379, so only an explicitly
		// exported REDIS_ADDR opts into the Redis backend.
		if os.Getenv("REDIS_ADDR") != "" {
			mode = backendRedis
		} else {
			mode = backendMemory
		}
	}
	if mode != backendMemory && mode != backendRedis {
		return "", fmt.Errorf("unknown backend %q (want %q or %q)", mode, backendMemory, backendRedis)
	}
	return mode, nil
}

// resolveAllocator picks the allocator mode from the --allocator flag or the
// ALLOCATOR env var, defaulting to none.
func resolveAllocator(flagValue string) (string, error) {
	mode := flagValue
	if mode == "" {
		mode = os.Getenv("ALLOCATOR")
	}
	if mode == "" {
		mode = allocatorNone
	}
	if mode != allocatorNone && mode != allocatorAgones {
		return "", fmt.Errorf("unknown allocator %q (want %q or %q)", mode, allocatorNone, allocatorAgones)
	}
	return mode, nil
}

// resolveDuration picks a duration from the flag (wins when positive), then the
// named env var, then def. An unparseable or non-positive env value is logged
// and ignored rather than failing start-up: a bad tuning knob must not take the
// gateway down.
func resolveDuration(flagValue time.Duration, envKey string, def time.Duration, log *slog.Logger) time.Duration {
	if flagValue > 0 {
		return flagValue
	}
	raw := os.Getenv(envKey)
	if raw == "" {
		return def
	}
	d, err := time.ParseDuration(raw)
	if err != nil || d <= 0 {
		log.Warn("invalid duration in environment, using default", "env", envKey, "value", raw, "default", def)
		return def
	}
	return d
}

// firstNonEmpty returns the first non-empty string, or "" when all are empty.
func firstNonEmpty(vals ...string) string {
	for _, v := range vals {
		if v != "" {
			return v
		}
	}
	return ""
}
