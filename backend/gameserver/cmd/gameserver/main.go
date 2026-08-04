package main

import (
	"context"
	"flag"
	"fmt"
	"net/url"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/pgstore"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"
	"github.com/duycuong/rpg-mmo/shared/transport"

	"github.com/duycuong/rpg-mmo/gameserver/agones"
	"github.com/duycuong/rpg-mmo/gameserver/server"
)

func main() {
	mode := flag.String("mode", "map", "Server mode: map or dungeon")
	addr := flag.String("addr", "", "Listen address (overrides config)")
	mapID := flag.String("map-id", "map_01", "Map ID to host")
	serverID := flag.String("server-id", "", "Unique server ID")
	transportKind := flag.String("transport", "", "Realtime transport: tcp or kcp (overrides GAMESERVER_TRANSPORT, default tcp)")
	capacity := flag.Int("capacity", 100, "Max player capacity")
	useAgones := flag.Bool("agones", false, "Enable Agones SDK integration")
	useRedis := flag.Bool("redis", false, "Use Redis-backed server registry and event stream (default: in-memory)")
	redisAddr := flag.String("redis-addr", "", "Redis address (overrides REDIS_ADDR)")
	gameDBURL := flag.String("game-db-url", "", "PostgreSQL DSN for game state (overrides GAME_DB_URL); empty = in-memory player store")
	flag.Parse()

	cfg := config.Load()
	log := logger.New(cfg.LogLevel)

	if *addr != "" {
		cfg.GameServerAddr = *addr
	}
	if *serverID == "" {
		*serverID = resolveServerID(*mode, *mapID)
	}
	if *transportKind != "" {
		cfg.GameServerTransport = *transportKind
	}
	if err := transport.Validate(cfg.GameServerTransport); err != nil {
		log.Error("invalid transport", "err", err)
		os.Exit(1)
	}
	if *redisAddr != "" {
		cfg.RedisAddr = *redisAddr
	}
	if *gameDBURL != "" {
		cfg.GameDBURL = *gameDBURL
	}

	// Initialize Agones SDK
	var agonesSDK agones.SDK
	if *useAgones {
		log.Info("initializing Agones SDK")
		real, err := agones.NewRealSDK(log)
		if err != nil {
			log.Error("agones SDK init failed", "err", err)
			os.Exit(1)
		}
		agonesSDK = real
	} else {
		log.Info("agones disabled, using noop SDK")
		agonesSDK = agones.NewNoopSDK(log)
	}

	log.Info("starting game server",
		"mode", *mode,
		"map_id", *mapID,
		"server_id", *serverID,
		"addr", cfg.GameServerAddr,
		"transport", transport.Normalize(cfg.GameServerTransport),
		"agones", *useAgones,
	)

	// Player state: PostgreSQL when GAME_DB_URL / --game-db-url is set,
	// otherwise in-memory (state lost on restart — dev only).
	var playerStore storage.PlayerStore
	if cfg.GameDBURL != "" {
		ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		pgPlayerStore, err := pgstore.NewPlayerStore(ctx, cfg.GameDBURL)
		if err != nil {
			cancel()
			log.Error("postgres player store init failed", "err", err)
			os.Exit(1)
		}
		if err := pgPlayerStore.Migrate(ctx); err != nil {
			cancel()
			pgPlayerStore.Close()
			log.Error("postgres player store migrate failed", "err", err)
			os.Exit(1)
		}
		cancel()
		defer pgPlayerStore.Close()
		log.Info("using postgres player store", "dsn", redactDSN(cfg.GameDBURL))
		playerStore = pgPlayerStore
	} else {
		log.Info("using in-memory player store (set GAME_DB_URL for postgres persistence)")
		playerStore = storage.NewMemoryPlayerStore()
	}

	// Registry + event stream: in-memory by default, Redis with --redis so that
	// gateway and game servers share one registry across processes.
	var (
		registry storage.ServerRegistry
		events   storage.EventStream
	)
	if *useRedis {
		log.Info("using redis-backed registry and event stream", "addr", cfg.RedisAddr)
		redisRegistry := redisstore.NewServerRegistry(cfg.RedisAddr, cfg.RedisPassword)
		redisStream := redisstore.NewEventStream(cfg.RedisAddr, cfg.RedisPassword, "gameserver", *serverID)
		defer redisRegistry.Close()
		defer redisStream.Close()
		registry = redisRegistry
		events = redisStream
	} else {
		log.Info("using in-memory registry and event stream")
		registry = storage.NewMemoryServerRegistry()
		events = storage.NewMemoryEventStream()
	}

	srv := server.New(server.ServerOpts{
		Config:      cfg,
		PlayerStore: playerStore,
		Registry:    registry,
		EventStream: events,
		AgonesSDK:   agonesSDK,
		ServerID:    *serverID,
		MapID:       *mapID,
		Mode:        *mode,
		Transport:   cfg.GameServerTransport,
		Capacity:    *capacity,
		Logger:      log,
	})

	// Handle shutdown signals
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-sigCh
		srv.Shutdown()
	}()

	if err := srv.Run(cfg.GameServerAddr); err != nil {
		log.Error("server error", "err", err)
		os.Exit(1)
	}
}

// resolveServerID derives the server id when --server-id is not given.
//
// Server-id contract with the gateway: an Agones allocation answers with the
// GameServer name, and the gateway mints the join token's sid from it. A pod
// must therefore register under that same name, so POD_NAME (injected by the
// fleet manifests through the downward API, fieldRef metadata.name) wins over
// the gs-<mode>-<map_id> default — that default collides across replicas of a
// fleet and is only useful for a single process run by hand.
func resolveServerID(mode, mapID string) string {
	if id := os.Getenv("GAMESERVER_ID"); id != "" {
		return id
	}
	if id := os.Getenv("POD_NAME"); id != "" {
		return id
	}
	return fmt.Sprintf("gs-%s-%s", mode, mapID)
}

// redactDSN strips the password from a postgres DSN so it is safe to log.
// Falls back to a fully redacted string if the DSN cannot be parsed.
func redactDSN(dsn string) string {
	u, err := url.Parse(dsn)
	if err != nil {
		return "(unparseable dsn)"
	}
	if u.User != nil {
		if _, hasPassword := u.User.Password(); hasPassword {
			u.User = url.UserPassword(u.User.Username(), "xxxxx")
		}
	}
	return u.Redacted()
}
