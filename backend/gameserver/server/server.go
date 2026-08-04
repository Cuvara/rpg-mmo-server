package server

import (
	"context"
	"fmt"
	"log/slog"
	"net"
	"sync"
	"time"

	"github.com/duycuong/rpg-mmo/gameserver/agones"
	"github.com/duycuong/rpg-mmo/gameserver/events"
	"github.com/duycuong/rpg-mmo/gameserver/game"
	"github.com/duycuong/rpg-mmo/gameserver/input"
	"github.com/duycuong/rpg-mmo/gameserver/persistence"
	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// ModeDungeon is the --mode value that selects dungeon behavior (longer
// reconnect hold window).
const ModeDungeon = "dungeon"

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
	publisher   *events.Publisher
	agonesSDK   agones.SDK
	agonesStop  chan struct{}
	hbStop      chan struct{}
	hbInterval  time.Duration
	mu          sync.Mutex
	listener    net.Listener
	// transportKind is the realtime transport this server listens with
	// ("tcp" or "kcp"); immutable after New, so Run reads it lock-free.
	transportKind string
	serverID      string
	mapID         string
	mode          string
	capacity      int
	holdTTL       time.Duration
	holdMu        sync.Mutex
	holds         map[string]*time.Timer
	shutdownOne   sync.Once
	logger        *slog.Logger
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
	Mode        string // "map" (default) or "dungeon"
	// Transport selects the realtime transport ("tcp" or "kcp"). Empty means
	// TCP. The value is published in the registry so the gateway can tell
	// clients which transport to dial.
	Transport string
	Capacity  int
	// HoldTTL overrides the disconnect hold window. Zero selects
	// constants.EntityHoldTTL (map) or constants.DungeonHoldTTL (dungeon).
	HoldTTL time.Duration
	// HeartbeatInterval overrides the registry heartbeat period. Zero selects
	// constants.ServerHeartbeatTTL / 3.
	HeartbeatInterval time.Duration
	Logger            *slog.Logger
}

// New creates a game server.
func New(opts ServerOpts) *Server {
	world := game.NewWorld()
	handler := input.NewHandler(world, opts.Logger)
	conns := NewConnectionManager()

	holdTTL := opts.HoldTTL
	if holdTTL <= 0 {
		holdTTL = constants.EntityHoldTTL
		if opts.Mode == ModeDungeon {
			holdTTL = constants.DungeonHoldTTL
		}
	}
	hbInterval := opts.HeartbeatInterval
	if hbInterval <= 0 {
		hbInterval = constants.ServerHeartbeatTTL / 3
	}

	s := &Server{
		cfg:           opts.Config,
		world:         world,
		conns:         conns,
		handler:       handler,
		playerStore:   opts.PlayerStore,
		registry:      opts.Registry,
		agonesSDK:     opts.AgonesSDK,
		agonesStop:    make(chan struct{}),
		hbStop:        make(chan struct{}),
		hbInterval:    hbInterval,
		serverID:      opts.ServerID,
		mapID:         opts.MapID,
		mode:          opts.Mode,
		transportKind: transport.Normalize(opts.Transport),
		capacity:      opts.Capacity,
		holdTTL:       holdTTL,
		holds:         make(map[string]*time.Timer),
		logger:        opts.Logger,
	}
	if opts.EventStream != nil {
		s.publisher = events.NewPublisher(opts.EventStream, opts.Logger)
	}
	handler.SetDeathHandler(s.onEntityDeath)
	return s
}

// Run starts the server. Blocks until Shutdown() or listener error.
func (s *Server) Run(addr string) error {
	ln, err := transport.Listen(s.transportKind, addr)
	if err != nil {
		return fmt.Errorf("listen %s: %w", addr, err)
	}
	s.mu.Lock()
	s.listener = ln
	s.mu.Unlock()
	s.logger.Info("game server started",
		"addr", ln.Addr().String(),
		"transport", s.transportKind,
		"map", s.mapID,
		"server_id", s.serverID)

	// Register in server registry and keep the entry alive with heartbeats.
	if s.registry != nil {
		if err := s.register(context.Background()); err != nil {
			s.logger.Error("registry register failed", "err", err)
		}
		go s.heartbeatLoop()
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

	// Enforce the sid (server id) claim: a token minted for another game server
	// must not be accepted here. Tokens without a sid are legacy/dev tokens and
	// are accepted with a warning.
	if claims.ServerID == "" {
		s.logger.Warn("join token without sid claim", "user", claims.UserID)
	} else if claims.ServerID != s.serverID {
		s.logger.Warn("join token for another server",
			"user", claims.UserID, "token_sid", claims.ServerID, "server_id", s.serverID)
		resp, _ := messages.NewEnvelope(messages.MsgJoinTokenResp, messages.JoinTokenResponse{
			OK:    false,
			Error: "join token not valid for this server",
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

	s.acquireEntity(userID)

	gc := NewConnection(conn, userID, s.logger)
	s.conns.Add(gc)

	if s.registry != nil {
		s.registry.UpdatePlayerCount(context.Background(), s.serverID, s.conns.Count())
	}

	go gc.WriteLoop()
	gc.ReadLoop(s.onMessage)

	// Player disconnected: keep the entity alive for the reconnect window
	// instead of removing it immediately.
	s.logger.Info("player disconnected", "user", userID, "hold", s.holdTTL)
	s.conns.Remove(userID)
	if s.saver != nil {
		s.saver.SaveAll()
	}
	s.holdEntity(userID)

	if s.registry != nil {
		s.registry.UpdatePlayerCount(context.Background(), s.serverID, s.conns.Count())
	}
}

// register writes this server's entry into the registry.
func (s *Server) register(ctx context.Context) error {
	info := storage.ServerInfo{
		ServerID:    s.serverID,
		MapID:       s.mapID,
		Addr:        s.Addr(),
		Transport:   s.transportKind,
		Capacity:    s.capacity,
		PlayerCount: s.conns.Count(),
	}
	if err := s.registry.Register(ctx, info); err != nil {
		return fmt.Errorf("register %s: %w", s.serverID, err)
	}
	return nil
}

// heartbeatLoop refreshes the registry liveness TTL. If the entry has expired
// (or was never written), it re-registers so the server reappears in lookups.
func (s *Server) heartbeatLoop() {
	ticker := time.NewTicker(s.hbInterval)
	defer ticker.Stop()

	for {
		select {
		case <-ticker.C:
			ctx := context.Background()
			if err := s.registry.Heartbeat(ctx, s.serverID); err != nil {
				s.logger.Warn("registry heartbeat failed, re-registering", "err", err)
				if err := s.register(ctx); err != nil {
					s.logger.Error("registry re-register failed", "err", err)
				}
			}
		case <-s.hbStop:
			return
		}
	}
}

// acquireEntity returns the player's entity, reattaching to a held one when the
// player reconnects inside the hold window (position/HP preserved). Otherwise a
// fresh entity is spawned and any persisted state is restored onto it.
func (s *Server) acquireEntity(userID string) *game.Entity {
	// Cancel a pending hold expiry, if any.
	s.holdMu.Lock()
	if timer, ok := s.holds[userID]; ok {
		timer.Stop()
		delete(s.holds, userID)
	}
	s.holdMu.Unlock()

	var held *game.Entity
	s.world.View(func(get func(string) *game.Entity) {
		if e := get(userID); e != nil {
			held = e
			s.logger.Info("player reattached to held entity", "user", userID, "x", e.X, "y", e.Y, "hp", e.HP)
		}
	})
	if held != nil {
		return held
	}

	playerEntity := &game.Entity{
		ID:      userID,
		Type:    "player",
		X:       0,
		Y:       0,
		HP:      100,
		MaxHP:   100,
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
	return playerEntity
}

// holdEntity schedules removal of a disconnected player's entity after the
// hold window elapses. A reconnect before then cancels the timer.
func (s *Server) holdEntity(userID string) {
	s.holdMu.Lock()
	defer s.holdMu.Unlock()
	if timer, ok := s.holds[userID]; ok {
		timer.Stop()
	}
	s.holds[userID] = time.AfterFunc(s.holdTTL, func() { s.expireHold(userID) })
}

// expireHold does the final save and removes the entity once the reconnect
// window has passed without the player coming back.
func (s *Server) expireHold(userID string) {
	s.holdMu.Lock()
	delete(s.holds, userID)
	s.holdMu.Unlock()

	// Reconnected between the timer firing and this lock — keep the entity.
	if s.conns.Get(userID) != nil {
		return
	}
	if s.saver != nil {
		s.saver.SaveAll() // final save; entity is still in the world
	}
	s.world.RemoveEntity(userID)
	s.logger.Info("hold expired, entity removed", "user", userID)
}

// HeldCount returns the number of entities currently in the reconnect hold
// window. Exposed for tests and metrics.
func (s *Server) HeldCount() int {
	s.holdMu.Lock()
	defer s.holdMu.Unlock()
	return len(s.holds)
}

// onEntityDeath publishes a cross-server event when a player or boss dies.
// Runs inside the tick loop: publishing happens on its own goroutine so a slow
// event backend can never stall the simulation.
func (s *Server) onEntityDeath(victim, killer *game.Entity) {
	if s.publisher == nil || victim == nil {
		return
	}
	var eventType string
	switch victim.Type {
	case "player":
		eventType = events.TypePlayerDeath
	case "boss":
		eventType = events.TypeBossKilled
	default:
		return // mob/npc deaths are not cross-server events
	}

	payload := events.DeathPayload{
		VictimID:   victim.ID,
		VictimType: victim.Type,
		MapID:      s.mapID,
		ServerID:   s.serverID,
	}
	if killer != nil {
		payload.KillerID = killer.ID
	}
	go s.publisher.PublishDeath(context.Background(), eventType, payload)
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

// Shutdown stops the server gracefully. Safe to call more than once.
func (s *Server) Shutdown() {
	s.shutdownOne.Do(s.shutdown)
}

func (s *Server) shutdown() {
	s.logger.Info("game server shutting down", "server_id", s.serverID)

	// Cancel pending reconnect holds; the saver's final flush covers their state.
	s.holdMu.Lock()
	for _, timer := range s.holds {
		timer.Stop()
	}
	s.holds = make(map[string]*time.Timer)
	s.holdMu.Unlock()

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
		close(s.hbStop)
		if err := s.registry.Deregister(context.Background(), s.serverID); err != nil {
			s.logger.Error("registry deregister failed", "err", err)
		}
	}
	s.conns.CloseAll()
}

// World returns the server's world for testing.
func (s *Server) World() *game.World {
	return s.world
}
