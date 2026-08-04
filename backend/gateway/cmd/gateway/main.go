package main

import (
	"flag"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"syscall"

	"github.com/duycuong/rpg-mmo/gateway/events"
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
	allocKubeconfig := flag.String("allocator-kubeconfig", "", "Kubeconfig path for the allocator (default: in-cluster config, then $KUBECONFIG, then ~/.kube/config)")
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
		sessionStore, serverRegistry, eventStream = sess, reg, stream
		closers = append(closers, stream.Close, client.Close)
		log.Info("using redis backend", "addr", cfg.RedisAddr, "consumer", consumer)
	default:
		sessionStore = storage.NewMemorySessionStore()
		serverRegistry = storage.NewMemoryServerRegistry()
		eventStream = storage.NewMemoryEventStream()
		closers = append(closers, eventStream.Close)
		log.Info("using in-memory backend (single process)")
	}

	sessions := session.NewSessionManager(sessionStore)

	// Allocator: with --allocator=agones the registry asks the Agones allocation
	// API for a GameServer whenever no live server can serve a map. Without it
	// an unserved map is simply an error (the pre-Agones behaviour).
	reg := registry.NewRegistryService(serverRegistry)
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
		}
		alloc, aerr := registry.NewAgonesAllocator(agonesCfg)
		if aerr != nil {
			log.Error("agones allocator init failed", "err", aerr)
			os.Exit(1)
		}
		reg = registry.NewRegistryServiceWithAllocator(serverRegistry, alloc)
		log.Info("agones allocator enabled",
			"namespace", agonesCfg.Namespace,
			"fleet_map", agonesCfg.FleetMap,
			"fleet_dungeon", agonesCfg.FleetDungeon,
		)
	} else {
		log.Info("allocator disabled (unserved maps return an error)")
	}

	// The relay's sink is the gateway and the gateway owns the relay, so the sink
	// is a closure: it only fires after Run starts the relay, when gw is set.
	var gw *server.Gateway
	relay := events.NewRelay(eventStream, events.DefaultStream,
		events.SinkFunc(func(ev storage.Event) { gw.OnEvent(ev) }), log)
	gw = server.New(sessions, reg, cfg.JWTSecret, log,
		server.WithEventRelay(relay), server.WithTransport(listenTransport))

	// Graceful shutdown on SIGINT/SIGTERM.
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	go func() {
		<-sigCh
		log.Info("shutting down gateway")
		gw.Shutdown()
		for _, c := range closers {
			if err := c(); err != nil {
				log.Warn("close resource", "err", err)
			}
		}
	}()

	log.Info("starting gateway",
		slog.String("addr", listenAddr),
		slog.String("backend", mode),
		slog.String("transport", transport.Normalize(listenTransport)))
	if err := gw.Run(listenAddr); err != nil {
		log.Error("gateway exited with error", "err", err)
		os.Exit(1)
	}
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

// firstNonEmpty returns the first non-empty string, or "" when all are empty.
func firstNonEmpty(vals ...string) string {
	for _, v := range vals {
		if v != "" {
			return v
		}
	}
	return ""
}
