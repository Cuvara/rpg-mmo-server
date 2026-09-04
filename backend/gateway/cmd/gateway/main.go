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
	allocFleetDungeon := flag.String("allocator-fleet-dungeon", "", "Agones Fleet for dungeon servers (overrides ALLOCATOR_FLEET_DUNGEON). No default: no dungeon fleet is deployed yet, and unset makes a dungeon allocation fail immediately and legibly")
	allocTransport := flag.String("allocator-transport", "", "Realtime transport the allocated fleet's game servers listen with: tcp or kcp (overrides ALLOCATOR_TRANSPORT; defaults to --transport)")
	metricsAddr := flag.String("metrics-addr", "", "Prometheus metrics listen address, e.g. :9102 (overrides METRICS_ADDR; \"off\" or an empty METRICS_ADDR disables it)")
	allocWaitTimeout := flag.Duration("allocation-wait-timeout", 0, "How long to wait for a freshly allocated game server to register itself before failing the join as retryable (overrides ALLOCATION_WAIT_TIMEOUT; default 15s). The wait blocks the client connection's read loop, which is also what records its MsgPong, so a value above 20s (pongTimeout-pingInterval) would let the heartbeat disconnect the client mid-allocation; the gateway refuses to start above it")
	allocPollInterval := flag.Duration("allocation-poll-interval", 0, "How often to re-check the registry while waiting for an allocated game server (overrides ALLOCATION_POLL_INTERVAL; default 250ms)")
	allocMismatchTTL := flag.Duration("allocation-mismatch-ttl", 0, "How long to refuse further allocations for a map after an allocated server turned out to serve a different map (overrides ALLOCATION_MISMATCH_TTL; default 60s, negative disables). Agones cannot un-allocate, so without this a client retrying an unservable map drains the fleet one GameServer per attempt")
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
	//
	// alloc stays nil unless Agones is configured; regOpts collects the
	// allocation-tuning options. Both are consumed by the single wireRegistry
	// call below, which is the only place this binary builds a RegistryService.
	var (
		alloc   registry.Allocator
		regOpts []registry.Option
	)
	allocMode, err := resolveAllocator(*allocatorMode)
	if err != nil {
		log.Error("invalid allocator", "err", err)
		os.Exit(1)
	}
	if allocMode == allocatorAgones {
		agonesCfg := registry.AgonesConfig{
			Namespace:    firstNonEmpty(*allocNamespace, os.Getenv("ALLOCATOR_NAMESPACE"), registry.DefaultNamespace),
			FleetMap:     firstNonEmpty(*allocFleetMap, os.Getenv("ALLOCATOR_FLEET_MAP"), registry.DefaultFleetMap),
			// No default fleet: dungeon allocation is unconfigured until a
			// dungeon fleet exists (ADR-14 stage 6).
			FleetDungeon: firstNonEmpty(*allocFleetDungeon, os.Getenv("ALLOCATOR_FLEET_DUNGEON")),
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
		// An allocated pod is not dialable the moment the allocation API
		// answers: it still has to boot, bind, report Ready and self-register.
		// The registry waits for its own entry before a join token is minted,
		// bounded by these two knobs (flag wins, then env, then default).
		waitTimeout := resolveDuration(*allocWaitTimeout, "ALLOCATION_WAIT_TIMEOUT", registry.DefaultAllocationWaitTimeout, log)
		pollInterval := resolveDuration(*allocPollInterval, "ALLOCATION_POLL_INTERVAL", registry.DefaultAllocationPollInterval, log)
		// Fail fast on a wait that could never resolve inline. handleEnterWorld
		// now runs under server.EnterWorldBudget (issue #235), so a large wait
		// no longer starves the heartbeat — the handler answers the retryable
		// "server is starting" at the budget while the allocation runs on
		// detached. But a wait above MaxHandlerBlockingWait remains a
		// misconfiguration worth refusing: it guarantees every cold-map join
		// takes at least one retry, and the value was chosen against a ceiling
		// that no longer applies. Same fail-fast precedent as a missing
		// JOIN_TOKEN_SECRET.
		if waitTimeout > server.MaxHandlerBlockingWait {
			log.Error("allocation wait would starve the client heartbeat; refusing to start",
				"allocation_wait_timeout", waitTimeout,
				"max_handler_blocking_wait", server.MaxHandlerBlockingWait,
				"why", "EnterWorld blocks the connection's read loop for the wait, "+
					"and that loop is what records MsgPong; a wait this long lets the "+
					"heartbeat time out and disconnect the client mid-allocation",
				"fix", "lower --allocation-wait-timeout / ALLOCATION_WAIT_TIMEOUT")
			os.Exit(1)
		}
		agonesAlloc, aerr := registry.NewAgonesAllocator(agonesCfg)
		if aerr != nil {
			log.Error("agones allocator init failed", "err", aerr)
			os.Exit(1)
		}
		mismatchTTL := resolveMismatchTTL(*allocMismatchTTL, log)
		alloc = agonesAlloc
		regOpts = append(regOpts,
			registry.WithAllocationWait(waitTimeout, pollInterval),
			registry.WithMapMismatchTTL(mismatchTTL))
		log.Info("agones allocator enabled",
			"allocation_mismatch_ttl", mismatchTTL,
			"namespace", agonesCfg.Namespace,
			"fleet_map", agonesCfg.FleetMap,
			"fleet_dungeon", firstNonEmpty(agonesCfg.FleetDungeon, "(unconfigured)"),
			"transport", agonesCfg.Transport,
			"allocation_wait_timeout", waitTimeout,
			"allocation_poll_interval", pollInterval,
		)
	} else {
		log.Info("allocator disabled (unserved maps return an error)")
	}

	// Registry + liveness watcher. rootCtx bounds every background loop this
	// binary owns; the shutdown handler below cancels it.
	rootCtx, stopRoot := context.WithCancel(context.Background())
	reg, watcher := wireRegistry(rootCtx, serverRegistry, eventStreamPublisher{stream: eventStream}, met, log, alloc, regOpts...)

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
	// The kick consumer subscribes to the same event stream the gateway
	// publishes gateway_kick events on. Different logical stream name
	// (constants.GatewayKickStream vs constants.KickEventStream), same
	// backend. On Redis these are different stream keys on the same client;
	// on the memory backend MemoryEventStream dispatches by stream name.
	kickConsumer := server.NewKickConsumer(
		eventStream, gatewayID,
		func(userID string) { gw.FindAndCloseConnection(userID) },
		log,
	)

	gw = server.New(sessions, reg, cfg.JWTSecret, log,
		server.WithEventRelay(relay), server.WithTransport(listenTransport),
		server.WithMetrics(met),
		// Duplicate-login supersede events publish into the SAME event stream
		// backend the relay consumes from (events:kick vs events:game are
		// different keys on the same store). On the memory backend the stream
		// has no cross-process consumer, which matches the memory backend's
		// single-process scope; on Redis the C# game servers consume it.
		server.WithKickStream(eventStream),
		server.WithKickConsumer(kickConsumer),
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
		// Cancel the background loops first, then wait for the watcher's poll
		// loop to actually exit before the stores it reads are closed.
		stopRoot()
		watcher.Stop()
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
// resolveMismatchTTL resolves --allocation-mismatch-ttl / ALLOCATION_MISMATCH_TTL.
//
// It cannot reuse resolveDuration because a NEGATIVE value is meaningful here:
// it disables the memory of "this fleet does not serve that map" and restores
// the unbounded allocate-per-retry behaviour. That is an escape hatch for an
// operator who knows their fleet's map changes under a running gateway, not a
// default, so it is logged loudly when taken.
func resolveMismatchTTL(flagValue time.Duration, log *slog.Logger) time.Duration {
	ttl := flagValue
	if ttl == 0 {
		if raw := os.Getenv("ALLOCATION_MISMATCH_TTL"); raw != "" {
			d, err := time.ParseDuration(raw)
			if err != nil {
				log.Warn("invalid duration in environment, using default",
					"env", "ALLOCATION_MISMATCH_TTL", "value", raw, "default", registry.DefaultMapMismatchTTL)
				return registry.DefaultMapMismatchTTL
			}
			ttl = d
		}
	}
	if ttl < 0 {
		log.Warn("allocation mismatch memory disabled; a client retrying a map no fleet serves will allocate a GameServer per attempt and Agones cannot un-allocate them",
			"allocation_mismatch_ttl", ttl)
	}
	if ttl == 0 {
		return registry.DefaultMapMismatchTTL
	}
	return ttl
}

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

// wireRegistry builds the gateway's RegistryService together with the
// RegistryWatcher that keeps its view of live game servers honest, and starts
// the watcher's poll loop on ctx.
//
// It is the ONLY place this binary constructs a RegistryService, on purpose:
// the watcher used to exist with no caller at all (issue #204), so server death
// was noticed only when the registry TTL expired and until then the gateway
// kept handing clients the address of a server that would not answer — the
// split-map fault of #203. Routing every construction through one function
// means the wiring cannot be dropped again without breaking the build, and
// TestWireRegistry_* fails if the watcher stops being constructed or attached.
//
// Agones health checks cover pod liveness; this covers something else — the
// gateway's own registry view — which is why both exist.
func wireRegistry(
	ctx context.Context,
	serverRegistry storage.ServerRegistry,
	pub registry.Publisher,
	met *metrics.Metrics,
	log *slog.Logger,
	alloc registry.Allocator,
	opts ...registry.Option,
) (*registry.RegistryService, *registry.RegistryWatcher) {
	watcher := registry.NewRegistryWatcher(serverRegistry, pub, log)

	opts = append([]registry.Option{
		registry.WithMetrics(met),
		registry.WithLogger(log),
		registry.WithWatcher(watcher),
	}, opts...)

	var svc *registry.RegistryService
	if alloc != nil {
		svc = registry.NewRegistryServiceWithAllocator(serverRegistry, alloc, opts...)
	} else {
		svc = registry.NewRegistryService(serverRegistry, opts...)
	}

	watcher.Start(ctx)
	return svc, watcher
}

// eventStreamPublisher adapts the gateway's existing event stream to
// registry.Publisher, so a server_down event travels the channel the gateway
// already has instead of pulling in a second pub/sub dependency. On the Redis
// backend that is Redis Streams (consumer-group ACK, ADR-5); in-memory
// otherwise.
type eventStreamPublisher struct {
	stream storage.EventStream
}

// Publish forwards the watcher's payload as an event whose type is the
// watcher's channel name.
func (p eventStreamPublisher) Publish(ctx context.Context, channel string, message []byte) error {
	if p.stream == nil {
		return nil
	}
	return p.stream.Publish(ctx, events.DefaultStream, storage.Event{Type: channel, Payload: message})
}
