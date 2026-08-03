package server

import (
	"context"
	"fmt"
	"log/slog"
	"net"
	"sync"
	"time"

	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/gameserver/agones"
	"github.com/duycuong/rpg-mmo/gameserver/game"
	"github.com/duycuong/rpg-mmo/gameserver/input"
	"github.com/duycuong/rpg-mmo/gameserver/persistence"
)

// Server is the game server that hosts a map or dungeon.
type Server struct {
	cfg         config.Config
	world       *game.World
	conns       *ConnectionManager
	tick        *TickRunner
	saver       *persistence.Saver
	handler     *input.Handler
	playerStore storage.PlayerStore
	registry    storage.ServerRegistry
	agonesSDK   agones.SDK
	agonesStop  chan struct{}
	mu          sync.Mutex
	listener    net.Listener
	serverID    string
	mapID       string
	capacity    int
	logger      *slog.Logger
}

// ServerOpts holds options for creating a game server.
type ServerOpts struct {
	Config      config.Config
	PlayerStore storage.PlayerStore
	Registry    storage.ServerRegistry
	EventStream storage.EventStream
	AgonesSDK   agones.SDK // nil = no Agones integration
	ServerID    string
	MapID       string
	Capacity    int
	Logger      *slog.Logger
}

// New creates a game server.
func New(opts ServerOpts) *Server {
	world := game.NewWorld()
	handler := input.NewHandler(world, opts.Logger)
	conns := NewConnectionManager()

	return &Server{
		cfg:         opts.Config,
		world:       world,
		conns:       conns,
		handler:     handler,
		playerStore: opts.PlayerStore,
		registry:    opts.Registry,
		agonesSDK:   opts.AgonesSDK,
		agonesStop:  make(chan struct{}),
		serverID:    opts.ServerID,
		mapID:       opts.MapID,
		capacity:    opts.Capacity,
		logger:      opts.Logger,
	}
}

// Run starts the server. Blocks until Shutdown() or listener error.
func (s *Server) Run(addr string) error {
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return fmt.Errorf("listen %s: %w", addr, err)
	}
	s.mu.Lock()
	s.listener = ln
	s.mu.Unlock()
	s.logger.Info("game server started", "addr", s.listener.Addr().String(), "map", s.mapID, "server_id", s.serverID)

	// Register in server registry
	if s.registry != nil {
		info := storage.ServerInfo{
			ServerID:    s.serverID,
			MapID:       s.mapID,
			Addr:        s.listener.Addr().String(),
			Capacity:    s.capacity,
			PlayerCount: 0,
		}
		if err := s.registry.Register(context.Background(), info); err != nil {
			s.logger.Error("registry register failed", "err", err)
		}
	}

	// Agones: mark ready + start health loop
	if s.agonesSDK != nil {
		if err := s.agonesSDK.Ready(); err != nil {
			s.logger.Error("agones ready failed", "err", err)
		}
		go agones.StartHealthLoop(s.agonesSDK, 2*time.Second, s.agonesStop, s.logger)
	}

	// Start tick loop
	s.tick = NewTickRunner(s.world, s.handler, s.conns, s.cfg.TickRate, s.logger)
	go s.tick.Run()

	// Start persistence saver
	s.saver = persistence.NewSaver(s.playerStore, s.world, s.mapID, 30*time.Second, s.logger)
	go s.saver.Run()

	// Accept connections
	for {
		conn, err := s.listener.Accept()
		if err != nil {
			return nil
		}
		go s.handleConnection(conn)
	}
}

// Addr returns the listener address. Only valid after Run() starts.
func (s *Server) Addr() string {
	s.mu.Lock()
	ln := s.listener
	s.mu.Unlock()
	if ln == nil {
		return ""
	}
	return ln.Addr().String()
}

func (s *Server) handleConnection(conn net.Conn) {
	env, err := messages.Decode(conn)
	if err != nil {
		s.logger.Debug("handshake read error", "err", err)
		conn.Close()
		return
	}

	if env.Type != messages.MsgJoinToken {
		s.logger.Debug("expected MsgJoinToken", "got", env.Type)
		conn.Close()
		return
	}

	var req messages.JoinTokenRequest
	if err := messages.UnmarshalPayload(env.Payload, &req); err != nil {
		s.logger.Debug("unmarshal join token error", "err", err)
		conn.Close()
		return
	}

	claims, err := jwt.Verify(req.Token, s.cfg.JWTSecret)
	if err != nil {
		resp, _ := messages.NewEnvelope(messages.MsgJoinTokenResp, messages.JoinTokenResponse{
			OK:    false,
			Error: "invalid join token",
		})
		data, _ := messages.Encode(resp)
		conn.Write(data)
		conn.Close()
		return
	}

	userID := claims.UserID
	s.logger.Info("player joined", "user", userID)

	// Send join accepted response
	resp, _ := messages.NewEnvelope(messages.MsgJoinTokenResp, messages.JoinTokenResponse{
		OK:     true,
		UserID: userID,
	})
	data, _ := messages.Encode(resp)
	conn.Write(data)

	// Create player entity
	playerEntity := &game.Entity{
		ID:      userID,
		Type:    "player",
		X:      0, Y: 0,
		HP:     100,
		MaxHP:  100,
		Attack:  10,
		Defense: 5,
		Speed:   1.0,
	}

	// Try to load existing state
	if state, err := s.playerStore.LoadPlayer(context.Background(), userID); err == nil {
		playerEntity.X = state.X
		playerEntity.Y = state.Y
		playerEntity.HP = state.HP
		playerEntity.MaxHP = state.MaxHP
	}

	s.world.AddEntity(playerEntity)

	gc := NewConnection(conn, userID, s.logger)
	s.conns.Add(gc)

	if s.registry != nil {
		s.registry.UpdatePlayerCount(context.Background(), s.serverID, s.conns.Count())
	}

	go gc.WriteLoop()
	gc.ReadLoop(s.onMessage)

	// Player disconnected
	s.logger.Info("player disconnected", "user", userID)
	s.conns.Remove(userID)
	s.saver.SaveAll()
	s.world.RemoveEntity(userID)

	if s.registry != nil {
		s.registry.UpdatePlayerCount(context.Background(), s.serverID, s.conns.Count())
	}
}

func (s *Server) onMessage(conn *Connection, env messages.Envelope) {
	switch env.Type {
	case messages.MsgInput:
		var input messages.InputMessage
		if err := messages.UnmarshalPayload(env.Payload, &input); err != nil {
			s.logger.Debug("invalid input message", "user", conn.UserID, "err", err)
			return
		}
		s.world.PushInput(conn.UserID, input)
	case messages.MsgDisconnect:
		conn.Close()
	}
}

// Shutdown stops the server gracefully.
func (s *Server) Shutdown() {
	s.logger.Info("game server shutting down", "server_id", s.serverID)

	// Stop Agones health loop and mark shutdown
	if s.agonesSDK != nil {
		close(s.agonesStop)
		if err := s.agonesSDK.Shutdown(); err != nil {
			s.logger.Error("agones shutdown failed", "err", err)
		}
	}

	s.mu.Lock()
	ln := s.listener
	s.mu.Unlock()
	if ln != nil {
		ln.Close()
	}
	if s.tick != nil {
		s.tick.Stop()
	}
	if s.saver != nil {
		s.saver.Stop()
	}
	if s.registry != nil {
		s.registry.Deregister(context.Background(), s.serverID)
	}
	s.conns.CloseAll()
}

// World returns the server's world for testing.
func (s *Server) World() *game.World {
	return s.world
}
