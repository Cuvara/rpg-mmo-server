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

func main() {
	addr := flag.String("addr", "", "Listen address (overrides GATEWAY_ADDR)")
	backend := flag.String("backend", "", "Store backend: memory or redis (default: redis when REDIS_ADDR is set, else memory)")
	transportKind := flag.String("transport", "", "Realtime transport: tcp or kcp (overrides GATEWAY_TRANSPORT, default tcp)")
	instanceID := flag.String("instance-id", "", "Gateway instance id, used as the event-stream consumer name (default: hostname)")
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
	// Allocator is still a stub (Agones allocation not implemented); the registry
	// falls back to an error when no live server has capacity.
	reg := registry.NewRegistryService(serverRegistry)

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
