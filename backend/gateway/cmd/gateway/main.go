package main

import (
	"log/slog"
	"os"
	"os/signal"
	"syscall"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/server"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

func main() {
	cfg := config.Load()
	log := logger.New(cfg.LogLevel)

	// In-memory stores for MVP; swap with Redis-backed stores in production.
	sessionStore := storage.NewMemorySessionStore()
	serverRegistry := storage.NewMemoryServerRegistry()

	sessions := session.NewSessionManager(sessionStore)
	reg := registry.NewRegistryService(serverRegistry)

	gw := server.New(sessions, reg, cfg.JWTSecret, log)

	// Graceful shutdown on SIGINT/SIGTERM.
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	go func() {
		<-sigCh
		log.Info("shutting down gateway")
		gw.Shutdown()
	}()

	log.Info("starting gateway", slog.String("addr", cfg.GatewayAddr))
	if err := gw.Run(cfg.GatewayAddr); err != nil {
		log.Error("gateway exited with error", "err", err)
		os.Exit(1)
	}
}
