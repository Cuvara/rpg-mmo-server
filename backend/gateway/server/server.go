package server

import (
	"context"
	"fmt"
	"log/slog"
	"net"
	"sync"
	"sync/atomic"

	"github.com/duycuong/rpg-mmo/gateway/events"
	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/gateway/transfer"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// Gateway is the main TCP server that handles client authentication
// and map assignment before redirecting to game servers.
type Gateway struct {
	sessions  *session.SessionManager
	registry  *registry.RegistryService
	jwtSecret string
	logger    *slog.Logger

	relay      events.EventRelay
	eventCount atomic.Int64

	// transportKind is the realtime transport the gateway listens with
	// ("tcp" or "kcp"); it is immutable after New, so Run reads it lock-free.
	transportKind string

	mu       sync.Mutex
	listener net.Listener
	conns    map[*ClientConn]struct{}
	done     chan struct{}
}

// Option customises a Gateway at construction time.
type Option func(*Gateway)

// WithTransport selects the realtime transport the gateway listens with.
// Accepts transport.KindTCP or transport.KindKCP; the empty string keeps the
// default (TCP).
func WithTransport(kind string) Option {
	return func(g *Gateway) { g.transportKind = kind }
}

// WithEventRelay attaches a cross-server event relay. The gateway starts it in
// Run and stops it in Shutdown.
func WithEventRelay(relay events.EventRelay) Option {
	return func(g *Gateway) { g.relay = relay }
}

// New creates a new Gateway instance.
func New(
	sessions *session.SessionManager,
	reg *registry.RegistryService,
	jwtSecret string,
	logger *slog.Logger,
	opts ...Option,
) *Gateway {
	g := &Gateway{
		transportKind: transport.KindTCP,
		sessions:      sessions,
		registry:      reg,
		jwtSecret:     jwtSecret,
		logger:        logger,
		conns:         make(map[*ClientConn]struct{}),
		done:          make(chan struct{}),
	}
	for _, opt := range opts {
		opt(g)
	}
	return g
}

// OnEvent implements events.Sink: it receives every cross-server event consumed
// by the relay.
//
// MVP limitation: shared/messages has no client-facing event message type, so
// events are logged and counted instead of being pushed to connected clients.
// Once agent-shared adds a MsgEvent, this method becomes the fan-out point
// (iterate g.conns, cc.Send). See gateway/docs/DESIGN.md.
func (g *Gateway) OnEvent(ev storage.Event) {
	g.eventCount.Add(1)
	g.logger.Info("relayed event",
		"type", ev.Type,
		"bytes", len(ev.Payload),
		"clients", g.ConnCount(),
	)
}

// EventCount returns how many events the relay has delivered so far.
func (g *Gateway) EventCount() int64 { return g.eventCount.Load() }

// ConnCount returns the number of currently tracked client connections.
func (g *Gateway) ConnCount() int {
	g.mu.Lock()
	defer g.mu.Unlock()
	return len(g.conns)
}

// Run starts the gateway listener on the given address, using the transport
// selected with WithTransport (TCP by default).
func (g *Gateway) Run(addr string) error {
	if g.relay != nil {
		if err := g.relay.Start(context.Background()); err != nil {
			return fmt.Errorf("start event relay: %w", err)
		}
	}

	ln, err := transport.Listen(g.transportKind, addr)
	if err != nil {
		return fmt.Errorf("listen: %w", err)
	}
	g.mu.Lock()
	g.listener = ln
	g.mu.Unlock()
	g.logger.Info("gateway listening",
		"addr", ln.Addr().String(),
		"transport", transport.Normalize(g.transportKind))

	for {
		conn, err := ln.Accept()
		if err != nil {
			select {
			case <-g.done:
				return nil // clean shutdown
			default:
				g.logger.Error("accept error", "err", err)
				continue
			}
		}
		cc := NewClientConn(conn, g.logger)
		g.trackConn(cc, true)
		go g.handleConn(cc)
	}
}

// Shutdown gracefully stops the gateway.
func (g *Gateway) Shutdown() {
	close(g.done)
	g.mu.Lock()
	if g.listener != nil {
		g.listener.Close()
	}
	for cc := range g.conns {
		cc.Close()
	}
	g.mu.Unlock()

	if g.relay != nil {
		if err := g.relay.Stop(); err != nil {
			g.logger.Error("stop event relay", "err", err)
		}
	}
}

// Addr returns the listener address, or empty string if not running.
func (g *Gateway) Addr() string {
	g.mu.Lock()
	ln := g.listener
	g.mu.Unlock()
	if ln == nil {
		return ""
	}
	return ln.Addr().String()
}

func (g *Gateway) trackConn(cc *ClientConn, add bool) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if add {
		g.conns[cc] = struct{}{}
	} else {
		delete(g.conns, cc)
	}
}

func (g *Gateway) handleConn(cc *ClientConn) {
	defer func() {
		// A dropped socket must not leave a session record behind, otherwise the
		// store leaks entries until the TTL expires (and a Redis-backed store
		// would keep reporting the player as online).
		g.cleanupSession(cc)
		cc.Close()
		g.trackConn(cc, false)
	}()

	go cc.WriteLoop()
	cc.ReadLoop(g.handleMessage)
}

// cleanupSession destroys the session bound to a connection, if any.
func (g *Gateway) cleanupSession(cc *ClientConn) {
	if cc.State == StateConnected || cc.UserID == "" {
		return
	}
	if err := g.sessions.DestroySession(context.Background(), session.SessionKey(cc.UserID)); err != nil {
		g.logger.Warn("destroy session", "user", cc.UserID, "err", err)
	}
	cc.State = StateConnected
	cc.UserID = ""
}

func (g *Gateway) handleMessage(cc *ClientConn, env messages.Envelope) {
	// MsgAuth is the only frame accepted without a live session.
	if env.Type != messages.MsgAuth && !g.checkSession(cc, env.Type) {
		return
	}

	switch env.Type {
	case messages.MsgAuth:
		g.handleAuth(cc, env)
	case messages.MsgEnterWorld:
		g.handleEnterWorld(cc, env)
	case messages.MsgDisconnect:
		g.handleDisconnect(cc)
	default:
		g.logger.Warn("unexpected message type", "type", env.Type, "state", cc.State)
	}
}

// checkSession validates that the connection still owns a live session in the
// store and refreshes its TTL (activity heartbeat). Returns false — after
// replying with the appropriate error — when the session is gone.
func (g *Gateway) checkSession(cc *ClientConn, msgType messages.MsgType) bool {
	if cc.State == StateConnected || cc.UserID == "" {
		if msgType == messages.MsgEnterWorld {
			g.sendEnterWorldError(cc, "not authenticated")
		} else {
			g.sendAuthError(cc, "not authenticated")
		}
		return false
	}

	ctx := context.Background()
	key := session.SessionKey(cc.UserID)
	userID, err := g.sessions.ValidateSession(ctx, key)
	if err != nil || userID != cc.UserID {
		g.logger.Info("session expired", "user", cc.UserID, "err", err)
		cc.State = StateConnected
		cc.UserID = ""
		if msgType == messages.MsgEnterWorld {
			g.sendEnterWorldError(cc, "session expired")
		} else {
			g.sendAuthError(cc, "session expired")
		}
		return false
	}

	// Sliding TTL: any client activity keeps the session alive.
	if err := g.sessions.RefreshSession(ctx, key); err != nil {
		g.logger.Warn("refresh session", "user", cc.UserID, "err", err)
	}
	return true
}

func (g *Gateway) handleAuth(cc *ClientConn, env messages.Envelope) {
	var req messages.AuthRequest
	if err := messages.UnmarshalPayload(env.Payload, &req); err != nil {
		g.sendAuthError(cc, "invalid auth request")
		return
	}

	userID, err := session.VerifyClientJWT(req.Token, g.jwtSecret)
	if err != nil {
		g.sendAuthError(cc, "invalid token")
		return
	}

	ctx := context.Background()
	_, err = g.sessions.CreateSession(ctx, userID)
	if err != nil {
		g.sendAuthError(cc, "session creation failed")
		return
	}

	cc.UserID = userID
	cc.State = StateAuthenticated

	resp, err := messages.NewEnvelope(messages.MsgAuthResp, messages.AuthResponse{
		OK:     true,
		UserID: userID,
	})
	if err != nil {
		g.logger.Error("marshal auth response", "err", err)
		return
	}
	cc.Send(resp)
}

func (g *Gateway) sendAuthError(cc *ClientConn, msg string) {
	resp, err := messages.NewEnvelope(messages.MsgAuthResp, messages.AuthResponse{
		OK:    false,
		Error: msg,
	})
	if err != nil {
		return
	}
	cc.Send(resp)
}

func (g *Gateway) handleEnterWorld(cc *ClientConn, env messages.Envelope) {
	if cc.State != StateAuthenticated && cc.State != StateInWorld {
		g.sendEnterWorldError(cc, "not authenticated")
		return
	}

	var req messages.EnterWorldRequest
	if err := messages.UnmarshalPayload(env.Payload, &req); err != nil {
		g.sendEnterWorldError(cc, "invalid enter world request")
		return
	}

	ctx := context.Background()
	result, err := transfer.AssignMap(ctx, cc.UserID, req.MapID, g.registry, g.jwtSecret)
	if err != nil {
		g.sendEnterWorldError(cc, err.Error())
		return
	}

	cc.State = StateInWorld

	resp, err := messages.NewEnvelope(messages.MsgEnterWorldResp, messages.EnterWorldResponse{
		ServerAddr: result.ServerAddr,
		JoinToken:  result.JoinToken,
		Transport:  result.Transport,
	})
	if err != nil {
		g.logger.Error("marshal enter world response", "err", err)
		return
	}
	cc.Send(resp)
}

// handleDisconnect processes an explicit client MsgDisconnect: destroy the
// session, then close the socket.
func (g *Gateway) handleDisconnect(cc *ClientConn) {
	g.logger.Info("client disconnect", "user", cc.UserID)
	g.cleanupSession(cc)
	cc.Close()
}

func (g *Gateway) sendEnterWorldError(cc *ClientConn, msg string) {
	resp, err := messages.NewEnvelope(messages.MsgEnterWorldResp, messages.EnterWorldResponse{
		Error: msg,
	})
	if err != nil {
		return
	}
	cc.Send(resp)
}
