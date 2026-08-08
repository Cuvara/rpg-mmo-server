package server

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"sync"
	"sync/atomic"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/events"
	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/gateway/transfer"
	gameerrors "github.com/duycuong/rpg-mmo/shared/errors"
	sharedjwt "github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/ratelimit"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// KickPublisher publishes duplicate-login kick requests to other gateway
// instances. The in-memory implementation is a no-op (single process); the
// Redis implementation publishes to the gateway:kick Pub/Sub channel.
type KickPublisher interface {
	PublishKick(ctx context.Context, userID string) error
}

// KickSubscriber receives kick requests from other gateway instances.
type KickSubscriber interface {
	SubscribeKick(ctx context.Context, handler func(userID string)) error
	Close() error
}

// noopKickPublisher is used when no cross-gateway kick channel is configured.
type noopKickPublisher struct{}

func (noopKickPublisher) PublishKick(context.Context, string) error { return nil }

// Gateway is the main TCP server that handles client authentication
// and map assignment before redirecting to game servers.
type Gateway struct {
	sessions  *session.SessionManager
	registry  *registry.RegistryService
	jwtSecret string
	logger    *slog.Logger

	// authKeys verifies client auth tokens (JWT_SECRET, possibly a rotation
	// list). joinKeys signs join tokens (JOIN_TOKEN_SECRET, defaulting to
	// JWT_SECRET). Both are parsed once at construction.
	authKeys sharedjwt.Keyring
	joinKeys sharedjwt.Keyring

	// connLimiter bounds accepts per source IP; msgRate/msgBurst seed each
	// connection's own inbound-frame bucket. Both are no-ops when unset.
	connLimiter *ratelimit.Limiter
	msgRate     float64
	msgBurst    float64

	// transportKey is the pre-shared KCP encryption key ("" = plaintext).
	transportKey string

	// kickPub publishes kick requests to other gateway instances.
	kickPub KickPublisher
	// kickSub receives kick requests from other instances; nil when running
	// single-process (in-memory backend).
	kickSub KickSubscriber

	relay      events.EventRelay
	eventCount atomic.Int64
	// relayUp tracks whether the relay is subscribed. It starts false and is
	// set once Start succeeds, possibly after several retries (see
	// startRelayWithRetry), so readiness can reflect a degraded gateway.
	relayUp atomic.Bool

	// metrics is nil when the gateway runs without instrumentation; every
	// recording helper is nil-safe.
	metrics *metrics.Metrics

	// transportKind is the realtime transport the gateway listens with
	// ("tcp" or "kcp"); it is immutable after New, so Run reads it lock-free.
	transportKind string

	mu        sync.Mutex
	listener  net.Listener
	conns     map[*ClientConn]struct{}
	userConns map[string]*ClientConn // userID -> active connection (O(1) duplicate-login lookup)
	done      chan struct{}
}

// Option customises a Gateway at construction time.
type Option func(*Gateway)

// WithTransport selects the realtime transport the gateway listens with.
// Accepts transport.KindTCP or transport.KindKCP; the empty string keeps the
// default (TCP).
func WithTransport(kind string) Option {
	return func(g *Gateway) { g.transportKind = kind }
}

// WithMetrics attaches the Prometheus metric set. Without it the gateway is
// uninstrumented (all recording calls are no-ops).
func WithMetrics(m *metrics.Metrics) Option {
	return func(g *Gateway) { g.metrics = m }
}

// WithEventRelay attaches a cross-server event relay. The gateway starts it in
// Run and stops it in Shutdown.
func WithEventRelay(relay events.EventRelay) Option {
	return func(g *Gateway) { g.relay = relay }
}

// WithJoinTokenSecret sets the secret (or comma-separated rotation list) used
// to sign gateway -> game server join tokens. Without it the gateway falls back
// to the auth JWT secret, which is the pre-split behaviour: one leaked secret
// then forges both client auth tokens and join tokens.
func WithJoinTokenSecret(spec string) Option {
	return func(g *Gateway) {
		if keys, err := sharedjwt.ParseKeyring(spec); err == nil {
			g.joinKeys = keys
		}
	}
}

// WithTransportKey sets the pre-shared key that encrypts the KCP listener.
// Ignored for TCP. Empty means plaintext (and Listen logs a warning).
func WithTransportKey(key string) Option {
	return func(g *Gateway) { g.transportKey = key }
}

// WithConnRateLimit bounds accepted connections per source IP: `burst`
// instantly, then `ratePerSec` sustained. A non-positive rate disables it.
//
// The check happens right after Accept and before any goroutine or session is
// created, so a rejected connection costs one map lookup and a Close.
func WithConnRateLimit(ratePerSec, burst float64) Option {
	return func(g *Gateway) {
		if ratePerSec <= 0 {
			g.connLimiter = nil
			return
		}
		g.connLimiter = ratelimit.NewLimiter(ratePerSec, burst, ratelimit.DefaultIdleTTL)
	}
}

// WithMsgRateLimit bounds inbound frames per connection. A non-positive rate
// disables it.
func WithMsgRateLimit(ratePerSec, burst float64) Option {
	return func(g *Gateway) {
		g.msgRate = ratePerSec
		g.msgBurst = burst
	}
}

// WithKickPublisher sets the publisher used to send duplicate-login kick
// requests to other gateway instances.
func WithKickPublisher(pub KickPublisher) Option {
	return func(g *Gateway) { g.kickPub = pub }
}

// WithKickSubscriber sets the subscriber for receiving kick requests from
// other instances. The gateway starts listening in Run.
func WithKickSubscriber(sub KickSubscriber) Option {
	return func(g *Gateway) { g.kickSub = sub }
}

// New creates a new Gateway instance.
//
// jwtSecret is the client-auth secret and accepts a comma-separated rotation
// list ("current,previous"): the first entry signs anything the gateway issues,
// every entry verifies. It also seeds the join-token keyring unless
// WithJoinTokenSecret overrides it.
func New(
	sessions *session.SessionManager,
	reg *registry.RegistryService,
	jwtSecret string,
	logger *slog.Logger,
	opts ...Option,
) *Gateway {
	// ParseKeyring only fails on an empty spec. Leaving the zero Keyring in
	// that case is deliberate and fail-closed: a gateway started with no secret
	// rejects every token instead of accepting tokens signed with "".
	authKeys, _ := sharedjwt.ParseKeyring(jwtSecret)
	g := &Gateway{
		transportKind: transport.KindTCP,
		sessions:      sessions,
		registry:      reg,
		jwtSecret:     jwtSecret,
		authKeys:      authKeys,
		joinKeys:      authKeys,
		kickPub:       noopKickPublisher{},
		logger:        logger,
		conns:         make(map[*ClientConn]struct{}),
		userConns:     make(map[string]*ClientConn),
		done:          make(chan struct{}),
	}
	for _, opt := range opts {
		opt(g)
	}
	return g
}

// Relay retry schedule: fast enough that a Redis restart is picked up within a
// few seconds, capped so a long outage does not hammer a recovering Redis.
const (
	relayRetryMin = 1 * time.Second
	relayRetryMax = 30 * time.Second
)

// startRelayWithRetry attempts relay.Start immediately and, if that fails,
// keeps retrying with exponential backoff until it succeeds or the gateway
// shuts down. The gateway serves traffic throughout: cross-server events are
// simply not delivered while the relay is down, which is a partial degradation
// rather than an outage.
func (g *Gateway) startRelayWithRetry() {
	if err := g.relay.Start(context.Background()); err == nil {
		g.relayUp.Store(true)
		g.metrics.SetRelayUp(true)
		return
	} else {
		g.logger.Error("event relay failed to start, continuing degraded and retrying",
			"err", err, "retry_in", relayRetryMin)
		g.metrics.SetRelayUp(false)
	}

	go func() {
		backoff := relayRetryMin
		for {
			select {
			case <-g.done:
				return
			case <-time.After(backoff):
			}

			if err := g.relay.Start(context.Background()); err != nil {
				backoff *= 2
				if backoff > relayRetryMax {
					backoff = relayRetryMax
				}
				g.logger.Warn("event relay still unavailable",
					"err", err, "retry_in", backoff)
				continue
			}
			g.relayUp.Store(true)
			g.metrics.SetRelayUp(true)
			g.logger.Info("event relay recovered")
			return
		}
	}()
}

// RelayUp reports whether the event relay is currently subscribed. False means
// the gateway is running degraded: cross-server events are not being delivered.
func (g *Gateway) RelayUp() bool { return g.relayUp.Load() }

// OnEvent implements events.Sink: it receives every cross-server event consumed
// by the relay.
//
// MVP limitation: shared/messages has no client-facing event message type, so
// events are logged and counted instead of being pushed to connected clients.
// Once agent-shared adds a MsgEvent, this method becomes the fan-out point
// (iterate g.conns, cc.Send). See gateway/docs/DESIGN.md.
func (g *Gateway) OnEvent(ev storage.Event) {
	g.eventCount.Add(1)
	g.metrics.RelayEvent()
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
	// The event relay is a degradable dependency, not a startup requirement.
	if g.relay != nil {
		g.startRelayWithRetry()
	}

	// Start the kick subscriber so this gateway can receive cross-instance
	// duplicate-login kick requests.
	if g.kickSub != nil {
		if err := g.kickSub.SubscribeKick(context.Background(), g.handleKickEvent); err != nil {
			g.logger.Error("kick subscriber failed to start", "err", err)
		}
	}

	ln, err := transport.Listen(g.transportKind, addr,
		transport.WithKey(g.transportKey), transport.WithLogger(g.logger))
	if err != nil {
		return fmt.Errorf("listen: %w", err)
	}
	g.mu.Lock()
	g.listener = ln
	g.mu.Unlock()
	g.connLimiter.StartCleanup(time.Minute)
	g.logger.Info("gateway listening",
		"addr", ln.Addr().String(),
		"transport", transport.Normalize(g.transportKind),
		"encrypted", g.transportKey != "",
		"conn_limit", g.connLimiter.Enabled(),
		"msg_limit", g.msgRate > 0)

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
		// Per-IP admission control happens before anything is allocated for
		// this connection — no goroutine, no session, no tracking entry — so a
		// connection flood costs one map lookup per attempt.
		if ip := remoteIP(conn); !g.connLimiter.Allow(ip) {
			g.metrics.RateLimited(metrics.RateLimitReasonConnection)
			g.logger.Warn("connection rate limited", "ip", ip)
			conn.Close()
			continue
		}
		cc := NewClientConn(conn, g.logger, ratelimit.NewBucket(g.msgRate, g.msgBurst))
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

	g.connLimiter.Stop()

	if g.relay != nil {
		if err := g.relay.Stop(); err != nil {
			g.logger.Error("stop event relay", "err", err)
		}
	}
	if g.kickSub != nil {
		if err := g.kickSub.Close(); err != nil {
			g.logger.Error("stop kick subscriber", "err", err)
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
		g.metrics.ConnOpened()
		return
	}
	if _, tracked := g.conns[cc]; tracked {
		delete(g.conns, cc)
		g.metrics.ConnClosed()
	}
}

// trackUser associates a userID with a connection for O(1) duplicate-login
// lookup. Returns the previous connection for that user, if any.
func (g *Gateway) trackUser(userID string, cc *ClientConn) *ClientConn {
	g.mu.Lock()
	defer g.mu.Unlock()
	old := g.userConns[userID]
	g.userConns[userID] = cc
	return old
}

// untrackUser removes a user->connection association. Only removes if the
// current mapping points to cc (avoid clearing a newer connection).
func (g *Gateway) untrackUser(userID string, cc *ClientConn) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.userConns[userID] == cc {
		delete(g.userConns, userID)
	}
}

// findUserConn returns the connection for the given userID, or nil.
func (g *Gateway) findUserConn(userID string) *ClientConn {
	g.mu.Lock()
	defer g.mu.Unlock()
	return g.userConns[userID]
}

// authTimeout is how long an unauthenticated connection may idle before the
// gateway closes it. Prevents connection-slot exhaustion from clients that
// connect but never send MsgAuth.
const authTimeout = 30 * time.Second

func (g *Gateway) handleConn(cc *ClientConn) {
	defer func() {
		// A dropped socket must not leave a session record behind, otherwise the
		// store leaks entries until the TTL expires (and a Redis-backed store
		// would keep reporting the player as online).
		g.cleanupSession(cc)
		cc.Close()
		g.trackConn(cc, false)
	}()

	// Unauthenticated connections get a hard read deadline: if no MsgAuth
	// arrives within authTimeout the read returns an error and ReadLoop exits,
	// which triggers the deferred cleanup above.
	if err := cc.SetReadDeadline(time.Now().Add(authTimeout)); err != nil {
		g.logger.Debug("set auth deadline", "ip", cc.RemoteIP(), "err", err)
	}

	go cc.WriteLoop()
	go cc.HeartbeatLoop()
	cc.ReadLoop(g.handleMessage)
}

// cleanupSession destroys the session bound to a connection, if any.
func (g *Gateway) cleanupSession(cc *ClientConn) {
	userID := cc.ClearIdentity()
	if userID == "" {
		return
	}
	g.untrackUser(userID, cc)
	if err := g.sessions.DestroySession(context.Background(), session.SessionKey(userID)); err != nil {
		g.logger.Warn("destroy session", "user", userID, "err", err)
	}
}

func (g *Gateway) handleMessage(cc *ClientConn, env messages.Envelope) {
	// Per-connection flood control.
	if cc.limited {
		return
	}
	if !cc.allowMessage() {
		cc.limited = true
		g.metrics.RateLimited(metrics.RateLimitReasonMessage)
		g.logger.Warn("message rate limited", "ip", cc.RemoteIP(), "user", cc.UserID(), "type", env.Type)
		resp, err := cc.Reply(messages.MsgAuthResp, messages.AuthResponse{
			OK:    false,
			Error: "rate limited",
		})
		if err != nil {
			cc.Close()
			return
		}
		cc.SendAndClose(resp)
		return
	}

	// Heartbeat messages are handled before session checks: a MsgPong that
	// refreshes the timeout must not be rejected because a Redis blip made the
	// session lookup fail, and pings carry no session semantics at all.
	switch env.Type {
	case messages.MsgPing:
		g.handlePing(cc, env)
		return
	case messages.MsgPong:
		cc.RecordPong()
		return
	}

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
		g.logger.Warn("unexpected message type", "type", env.Type, "state", cc.State())
	}
}

// checkSession validates that the connection still owns a live session in the
// store and refreshes its TTL (activity heartbeat). Returns false — after
// replying with the appropriate error — when the session is gone.
func (g *Gateway) checkSession(cc *ClientConn, msgType messages.MsgType) bool {
	connUser, connState := cc.Identity()
	if connState == StateConnected || connUser == "" {
		if msgType == messages.MsgEnterWorld {
			g.metrics.EnterWorldResult(false)
			g.sendEnterWorldError(cc, "not authenticated")
		} else {
			g.sendAuthError(cc, "not authenticated")
		}
		return false
	}

	ctx := context.Background()
	key := session.SessionKey(connUser)
	_, err := g.sessions.ValidateSession(ctx, key)

	if err != nil && !errors.Is(err, storage.ErrNotFound) {
		g.metrics.SessionCheckResult(metrics.SessionCheckStoreError)
		g.logger.Error("session store unavailable, failing open",
			"user", connUser, "err", err)
		return true
	}
	if err != nil {
		g.metrics.SessionCheckResult(metrics.SessionCheckExpired)
		g.logger.Info("session expired", "user", connUser, "err", err)
		cc.ClearIdentity()
		if msgType == messages.MsgEnterWorld {
			g.metrics.EnterWorldResult(false)
			g.sendEnterWorldError(cc, "session expired")
		} else {
			g.sendAuthError(cc, "session expired")
		}
		return false
	}
	g.metrics.SessionCheckResult(metrics.SessionCheckOK)

	// Sliding TTL: any client activity keeps the session alive.
	if err := g.sessions.RefreshSession(ctx, key); err != nil {
		g.logger.Warn("refresh session", "user", connUser, "err", err)
	}
	return true
}

func (g *Gateway) handleAuth(cc *ClientConn, env messages.Envelope) {
	var req messages.AuthRequest
	if err := env.UnmarshalPayload(&req); err != nil {
		g.metrics.AuthResult(false)
		g.sendAuthError(cc, "invalid auth request")
		return
	}

	userID, err := session.VerifyClientJWTKeyring(req.Token, g.authKeys)
	if err != nil {
		g.metrics.AuthResult(false)
		g.sendAuthError(cc, "invalid token")
		return
	}

	ctx := context.Background()

	// --- Duplicate login detection ---
	existing, getErr := g.sessions.GetSession(ctx, userID)
	if getErr == nil {
		gwID := g.sessions.GatewayID()
		if existing.GatewayID == gwID {
			// Same gateway: kick the old connection, but only if it is a
			// different socket (re-auth on the same conn is not a duplicate).
			if old := g.findUserConn(userID); old != nil && old != cc {
				g.kickLocalUser(userID)
			}
		} else {
			// Different gateway: publish a kick request via Pub/Sub.
			if perr := g.kickPub.PublishKick(ctx, userID); perr != nil {
				g.logger.Warn("publish kick event", "user", userID, "err", perr)
			}
		}
		g.logger.Info("duplicate login detected",
			"user", userID,
			"old_gateway", existing.GatewayID,
			"new_gateway", gwID)
	}

	_, err = g.sessions.CreateSession(ctx, userID)
	if err != nil {
		g.metrics.AuthResult(false)
		g.sendAuthError(cc, "session creation failed")
		return
	}
	g.metrics.AuthResult(true)

	cc.SetAuthenticated(userID)
	g.trackUser(userID, cc)

	// Auth succeeded: remove the unauthenticated read deadline so the
	// connection is no longer time-limited.
	if derr := cc.ClearReadDeadline(); derr != nil {
		g.logger.Debug("clear auth deadline", "user", userID, "err", derr)
	}

	resp, err := cc.Reply(messages.MsgAuthResp, messages.AuthResponse{
		OK:     true,
		UserID: userID,
	})
	if err != nil {
		g.logger.Error("marshal auth response", "err", err)
		return
	}
	cc.Send(resp)
}

// kickLocalUser finds a local connection for userID and sends
// MsgDisconnect(reason="duplicate_login") before closing it.
func (g *Gateway) kickLocalUser(userID string) {
	old := g.findUserConn(userID)
	if old == nil {
		return
	}
	// Use NewEnvelope (JSON) rather than old.Reply to avoid reading the enc
	// field, which is only safe from the old connection's ReadLoop goroutine.
	resp, err := messages.NewEnvelope(messages.MsgDisconnect, messages.DisconnectMessage{
		Reason: "duplicate_login",
	})
	if err == nil {
		old.SendAndClose(resp)
	} else {
		old.Close()
	}
	// ClearIdentity so cleanupSession in handleConn's defer doesn't destroy the
	// session we're about to overwrite.
	old.ClearIdentity()
	g.untrackUser(userID, old)
}

// handleKickEvent processes a kick request received from another gateway
// instance via the Pub/Sub channel.
func (g *Gateway) handleKickEvent(userID string) {
	g.logger.Info("received kick event", "user", userID)
	g.kickLocalUser(userID)
}

func (g *Gateway) sendAuthError(cc *ClientConn, msg string) {
	resp, err := cc.Reply(messages.MsgAuthResp, messages.AuthResponse{
		OK:    false,
		Error: msg,
	})
	if err != nil {
		return
	}
	cc.Send(resp)
}

func (g *Gateway) handleEnterWorld(cc *ClientConn, env messages.Envelope) {
	userID, state := cc.Identity()
	if state != StateAuthenticated && state != StateInWorld {
		g.metrics.EnterWorldResult(false)
		g.sendEnterWorldError(cc, "not authenticated")
		return
	}

	var req messages.EnterWorldRequest
	if err := env.UnmarshalPayload(&req); err != nil {
		g.metrics.EnterWorldResult(false)
		g.sendEnterWorldError(cc, "invalid enter world request")
		return
	}

	ctx := context.Background()
	result, err := transfer.AssignMapKeyring(ctx, userID, req.MapID, g.registry, g.joinKeys)
	if err != nil {
		g.metrics.EnterWorldResult(false)
		g.logger.Error("enter world failed", "user", userID, "map", req.MapID, "err", err)
		g.sendEnterWorldError(cc, clientSafeAssignError(err))
		return
	}
	g.metrics.EnterWorldResult(true)

	cc.SetInWorld()

	// Update session with server and map association (task 2c).
	if uerr := g.sessions.UpdateSession(ctx, userID, func(sd *session.SessionData) {
		sd.ServerID = result.ServerID
		sd.MapID = req.MapID
	}); uerr != nil {
		g.logger.Warn("update session server association",
			"user", userID, "server", result.ServerID, "map", req.MapID, "err", uerr)
	}

	resp, err := cc.Reply(messages.MsgEnterWorldResp, messages.EnterWorldResponse{
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
	g.logger.Info("client disconnect", "user", cc.UserID())
	g.cleanupSession(cc)
	cc.CloseGracefully()
}

// Client-facing messages for EnterWorld failures.
const (
	msgNoServerAvailable = "no server available for map"
	msgNotImplemented    = "not implemented"
	msgInternalError     = "internal error"
)

func clientSafeAssignError(err error) string {
	switch {
	case errors.Is(err, registry.ErrNoServerAvailable):
		return msgNoServerAvailable
	case gameerrors.Is(err, gameerrors.ErrNotImplemented):
		return msgNotImplemented
	default:
		return msgInternalError
	}
}

func (g *Gateway) handlePing(cc *ClientConn, env messages.Envelope) {
	var req messages.PingMessage
	if err := env.UnmarshalPayload(&req); err != nil {
		return
	}
	resp, err := cc.Reply(messages.MsgPong, messages.PongMessage{
		Timestamp:  req.Timestamp,
		ServerTime: time.Now().UnixMilli(),
	})
	if err != nil {
		return
	}
	cc.Send(resp)
}

func (g *Gateway) sendEnterWorldError(cc *ClientConn, msg string) {
	resp, err := cc.Reply(messages.MsgEnterWorldResp, messages.EnterWorldResponse{
		Error: msg,
	})
	if err != nil {
		return
	}
	cc.Send(resp)
}
