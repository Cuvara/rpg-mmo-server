package server

import (
	"context"
	"fmt"
	"log/slog"
	"net"
	"sync"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/gateway/transfer"
	"github.com/duycuong/rpg-mmo/shared/messages"
)

// Gateway is the main TCP server that handles client authentication
// and map assignment before redirecting to game servers.
type Gateway struct {
	sessions  *session.SessionManager
	registry  *registry.RegistryService
	jwtSecret string
	logger    *slog.Logger

	mu       sync.Mutex
	listener net.Listener
	conns    map[*ClientConn]struct{}
	done     chan struct{}
}

// New creates a new Gateway instance.
func New(
	sessions *session.SessionManager,
	reg *registry.RegistryService,
	jwtSecret string,
	logger *slog.Logger,
) *Gateway {
	return &Gateway{
		sessions:  sessions,
		registry:  reg,
		jwtSecret: jwtSecret,
		logger:    logger,
		conns:     make(map[*ClientConn]struct{}),
		done:      make(chan struct{}),
	}
}

// Run starts the gateway TCP listener on the given address.
func (g *Gateway) Run(addr string) error {
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return fmt.Errorf("listen: %w", err)
	}
	g.mu.Lock()
	g.listener = ln
	g.mu.Unlock()
	g.logger.Info("gateway listening", "addr", ln.Addr().String())

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
		cc.Close()
		g.trackConn(cc, false)
	}()

	go cc.WriteLoop()
	cc.ReadLoop(g.handleMessage)
}

func (g *Gateway) handleMessage(cc *ClientConn, env messages.Envelope) {
	switch env.Type {
	case messages.MsgAuth:
		g.handleAuth(cc, env)
	case messages.MsgEnterWorld:
		g.handleEnterWorld(cc, env)
	default:
		g.logger.Warn("unexpected message type", "type", env.Type, "state", cc.State)
	}
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
	if cc.State != StateAuthenticated {
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
	})
	if err != nil {
		g.logger.Error("marshal enter world response", "err", err)
		return
	}
	cc.Send(resp)
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
