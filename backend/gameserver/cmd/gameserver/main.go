package main

import (
	"flag"
	"fmt"
	"os"
	"os/signal"
	"syscall"

	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/gameserver/agones"
	"github.com/duycuong/rpg-mmo/gameserver/server"
)

func main() {
	mode := flag.String("mode", "map", "Server mode: map or dungeon")
	addr := flag.String("addr", "", "Listen address (overrides config)")
	mapID := flag.String("map-id", "map_01", "Map ID to host")
	serverID := flag.String("server-id", "", "Unique server ID")
	capacity := flag.Int("capacity", 100, "Max player capacity")
	useAgones := flag.Bool("agones", false, "Enable Agones SDK integration")
	flag.Parse()

	cfg := config.Load()
	log := logger.New(cfg.LogLevel)

	if *addr != "" {
		cfg.GameServerAddr = *addr
	}
	if *serverID == "" {
		*serverID = fmt.Sprintf("gs-%s-%s", *mode, *mapID)
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
		"agones", *useAgones,
	)

	// Use in-memory stores for MVP
	playerStore := storage.NewMemoryPlayerStore()
	registry := storage.NewMemoryServerRegistry()
	events := storage.NewMemoryEventStream()

	srv := server.New(server.ServerOpts{
		Config:      cfg,
		PlayerStore: playerStore,
		Registry:    registry,
		EventStream: events,
		AgonesSDK:   agonesSDK,
		ServerID:    *serverID,
		MapID:       *mapID,
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
