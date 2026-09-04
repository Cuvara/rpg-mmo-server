package server

import (
	"context"
	"encoding/json"
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

	// kickStream publishes duplicate-login supersede events for game servers
	// to consume (events:kick, ADR-5 Streams). Nil only when the option was
	// not passed; main.go always passes it, and publishSupersede logs loudly
	// if it is missing so an unwired publisher cannot be silent again (#211).
	kickStream storage.EventStream

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

	// enterWorldBudget caps how long one handleEnterWorld may block its
	// connection's read loop; EnterWorldBudget by default, overridden only by
	// tests that need the deadline to expire in milliseconds. Immutable after
	// the gateway starts serving.
	enterWorldBudget time.Duration

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

// WithKickStream sets the event stream the gateway publishes duplicate-login
// supersede events to (logical stream constants.KickEventStream). The gateway
// only publishes; game servers consume. Without it, a duplicate login still
// evicts the local gateway socket but the old game-server connection stays —
// and handleAuth logs a warning saying exactly that.
func WithKickStream(stream storage.EventStream) Option {
	return func(g *Gateway) { g.kickStream = stream }
}

// KickStreamConfigured reports whether a kick stream was wired in — the
// wiring-level assertion handle (#204's shape): a test can prove the binary's
// construction path passes WithKickStream without simulating a duplicate login.
func (g *Gateway) KickStreamConfigured() bool { return g.kickStream != nil }

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
		transportKind:    transport.KindTCP,
		sessions:         sessions,
		registry:         reg,
		jwtSecret:        jwtSecret,
		authKeys:         authKeys,
		joinKeys:         authKeys,
		logger:           logger,
		enterWorldBudget: EnterWorldBudget,
		conns:            make(map[*ClientConn]struct{}),
		userConns:        make(map[string]*ClientConn),
		done:             make(chan struct{}),
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
// One event type is acted on here: server_down (published by the registry
// watcher) evicts the named server from the registry so FindServer stops
// handing out a dead address immediately instead of waiting out the registry
// TTL (#236).
//
// MVP limitation: shared/messages has no client-facing event message type, so
// every other event is logged and counted instead of being pushed to connected
// clients. Once agent-shared adds a MsgEvent, this method becomes the fan-out
// point (iterate g.conns, cc.Send). See gateway/docs/DESIGN.md.
func (g *Gateway) OnEvent(ev storage.Event) {
	g.eventCount.Add(1)
	g.metrics.RelayEvent()
	g.logger.Info("relayed event",
		"type", ev.Type,
		"bytes", len(ev.Payload),
		"clients", g.ConnCount(),
	)

	if ev.Type == registry.ServerDownChannel {
		g.handleServerDown(ev.Payload)
	}
}

// serverDownEvictTimeout bounds the registry write an eviction performs. OnEvent
// runs on the relay's consumer goroutine, so a hung store must not stall event
// consumption indefinitely.
const serverDownEvictTimeout = 5 * time.Second

// handleServerDown consumes one server_down event: it evicts the named server
// from the registry so the assignment path stops returning it. Eviction is
// idempotent and a re-registering server becomes assignable again through the
// normal path, so acting on a duplicate or stale event is harmless.
func (g *Gateway) handleServerDown(payload []byte) {
	var ev registry.ServerDownEvent
	if err := json.Unmarshal(payload, &ev); err != nil {
		g.logger.Warn("malformed server_down event", "err", err)
		return
	}
	if ev.ServerID == "" {
		g.logger.Warn("server_down event with empty server_id, ignoring")
		return
	}

	ctx, cancel := context.WithTimeout(context.Background(), serverDownEvictTimeout)
	defer cancel()
	if err := g.registry.Evict(ctx, ev.ServerID); err != nil {
		// The registry TTL remains the backstop: a failed eviction degrades to
		// the pre-#236 behaviour instead of losing the event silently.
		g.logger.Warn("failed to evict dead server from registry; its TTL is now the only removal path",
			"server_id", ev.ServerID, "map_id", ev.MapID, "err", err)
		return
	}
	g.logger.Info("evicted dead server from registry",
		"server_id", ev.ServerID, "map_id", ev.MapID)
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
		// Debug, not info: a TCP accept is reachable by anyone who can open a
		// socket, so it is the one event in this file an unauthenticated peer
		// can mint at will. Everything from MsgAuth onward costs the peer a
		// well-formed frame and is logged at info.
		g.logger.Debug("client connected",
			"conn", cc.ID(),
			"ip", cc.RemoteIP(),
			"transport", transport.Normalize(g.transportKind))
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
	start := time.Now()
	ip := cc.RemoteIP()
	defer func() {
		// A dropped socket must not leave a session record behind, otherwise the
		// store leaks entries until the TTL expires (and a Redis-backed store
		// would keep reporting the player as online).
		userID := g.cleanupSession(cc)
		cc.Close()
		g.trackConn(cc, false)
		// Only a connection that reached auth gets an info line, so the
		// close side matches the open side: one line per *session*, while a
		// scanner that connects and says nothing stays at debug.
		if userID == "" {
			g.logger.Debug("client disconnected",
				"conn", cc.ID(), "ip", ip, "dur_ms", time.Since(start).Milliseconds())
			return
		}
		g.logger.Info("client disconnected",
			"conn", cc.ID(), "user", userID, "ip", ip,
			"dur_ms", time.Since(start).Milliseconds())
	}()

	// Unauthenticated connections get a hard read deadline: if no MsgAuth
	// arrives within authTimeout the read returns an error and ReadLoop exits,
	// which triggers the deferred cleanup above.
	if err := cc.SetReadDeadline(time.Now().Add(authTimeout)); err != nil {
		g.logger.Debug("set auth deadline", "conn", cc.ID(), "ip", cc.RemoteIP(), "err", err)
	}

	go cc.WriteLoop()
	go cc.HeartbeatLoop()
	cc.ReadLoop(g.handleMessage)
}

// cleanupSession destroys the session bound to a connection, if any, and
// returns the user it belonged to ("" when the connection never authenticated
// or was already cleaned up). Callers use the return value to decide whether
// there was a session worth reporting — ClearIdentity is atomic, so exactly one
// caller can ever see a non-empty result for a given session.
func (g *Gateway) cleanupSession(cc *ClientConn) string {
	userID := cc.ClearIdentity()
	if userID == "" {
		return ""
	}
	g.untrackUser(userID, cc)
	if err := g.sessions.DestroySession(context.Background(), session.SessionKey(userID)); err != nil {
		g.logger.Warn("destroy session", "conn", cc.ID(), "user", userID, "err", err)
	}
	return userID
}

func (g *Gateway) handleMessage(cc *ClientConn, env messages.Envelope) {
	// Per-connection flood control.
	if cc.limited {
		return
	}
	if !cc.allowMessage() {
		cc.limited = true
		g.metrics.RateLimited(metrics.RateLimitReasonMessage)
		g.logger.Warn("message rate limited",
			"conn", cc.ID(), "ip", cc.RemoteIP(), "user", cc.UserID(), "type", env.Type)
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
		g.refreshSessionOnPong(cc)
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
		// Latched: this is reachable once per inbound frame, and the gateway
		// accepts only three message types, so a client speaking the wrong
		// protocol version would otherwise emit a warning per frame forever.
		lvl := slog.LevelWarn
		if !cc.firstUnexpectedMessage() {
			lvl = slog.LevelDebug
		}
		g.logger.Log(context.Background(), lvl, "unexpected message type",
			"conn", cc.ID(), "user", cc.UserID(), "type", env.Type, "state", cc.State())
	}
}

// refreshSessionOnPong re-arms the session TTL when a heartbeat MsgPong arrives
// on an authenticated connection, so a client that holds the gateway socket
// open sending only heartbeats keeps its session alive — the "refreshed by
// activity" contract in gameserver-dotnet/docs/API.md (#231). Without this the
// only refresh was checkSession on enter_world, so a player parked on one map
// for over SessionTTL (1h) had the session expire under a live connection and
// the next map transfer fail with "session expired".
//
// It is deliberately cheap and quiet:
//   - it never sends anything and never closes the connection — a pong must
//     stay side-effect-free from the client's point of view;
//   - store writes are bounded to one per sessionRefreshInterval per
//     connection, so a 10s heartbeat does not EXPIRE-spam the store;
//   - store errors fail open, like checkSession: the refresh is skipped, the
//     connection lives on, and a session actually gone is detected (and
//     reported) by checkSession on the next real frame.
func (g *Gateway) refreshSessionOnPong(cc *ClientConn) {
	userID, state := cc.Identity()
	if state == StateConnected || userID == "" {
		return // unauthenticated: no session to keep alive
	}
	if !cc.shouldRefreshSession(time.Now()) {
		return
	}
	if err := g.sessions.RefreshSession(context.Background(), session.SessionKey(userID)); err != nil {
		g.logger.Warn("refresh session on pong", "conn", cc.ID(), "user", userID, "err", err)
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
			"conn", cc.ID(), "user", connUser, "err", err)
		return true
	}
	if err != nil {
		g.metrics.SessionCheckResult(metrics.SessionCheckExpired)
		g.logger.Info("session expired", "conn", cc.ID(), "user", connUser, "err", err)
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
		g.logger.Warn("refresh session", "conn", cc.ID(), "user", connUser, "err", err)
	}
	return true
}

// handleAuth verifies a client token and opens a session.
//
// Logging note: req.Token and anything derived from it never appear in a log
// line. The token is the credential — a log that contains one is a log that can
// be replayed — so failures report a `reason` and the verifier's error (which
// says *why* a token was rejected, e.g. expired vs bad signature, without
// quoting it) and nothing else.
func (g *Gateway) handleAuth(cc *ClientConn, env messages.Envelope) {
	start := time.Now()
	var req messages.AuthRequest
	if err := env.UnmarshalPayload(&req); err != nil {
		g.metrics.AuthResult(false)
		g.logAuthFailure(cc, slog.LevelWarn, "", "malformed_request", err)
		g.sendAuthError(cc, "invalid auth request")
		return
	}

	userID, err := session.VerifyClientJWTKeyring(req.Token, g.authKeys)
	if err != nil {
		g.metrics.AuthResult(false)
		g.logAuthFailure(cc, slog.LevelWarn, "", "invalid_token", err)
		g.sendAuthError(cc, "invalid token")
		return
	}

	ctx := context.Background()

	// --- Duplicate login detection ---
	existing, getErr := g.sessions.GetSession(ctx, userID)
	if getErr == nil {
		gwID := g.sessions.GatewayID()

		// Re-auth on the same socket is not a duplicate: nothing to kick,
		// locally or remotely.
		sameConn := g.findUserConn(userID) == cc

		// Same gateway: kick the old gateway socket (MsgKick + MsgDisconnect,
		// reason duplicate_login). A gateway socket owned by a DIFFERENT
		// replica cannot be reached from here and is still left alone (the
		// remaining ADR-17 item; harmless at one replica).
		if existing.GatewayID == gwID {
			if old := g.findUserConn(userID); old != nil && old != cc {
				g.kickLocalUser(userID)
			}
		}

		// The GAME-SERVER half of the eviction, which the local kick above
		// cannot do: the client talks to the game server directly (ADR-3), so
		// closing the gateway socket leaves the old gameplay connection alive.
		// Publish a supersede event on the events:kick stream (Streams with
		// consumer-group ACK per ADR-5 — the shape #211 said a rebuild must
		// take, and deliberately NOT the Pub/Sub shape #211 deleted). The
		// event names the old session's join-token jti, so the game server
		// kicks exactly that connection and never the newer login's — newest
		// login wins even when the event is delivered late. Published for
		// sessions owned by ANY gateway instance: the stream, unlike the
		// socket, is reachable regardless of which replica owns the session.
		if !sameConn {
			g.publishSupersede(userID, existing)
		}

		g.logger.Info("duplicate login detected",
			"conn", cc.ID(),
			"user", userID,
			"old_gateway", existing.GatewayID,
			"new_gateway", gwID)
	}

	_, err = g.sessions.CreateSession(ctx, userID)
	if err != nil {
		g.metrics.AuthResult(false)
		g.logAuthFailure(cc, slog.LevelError, userID, "session_store", err)
		g.sendAuthError(cc, "session creation failed")
		return
	}
	g.metrics.AuthResult(true)

	cc.SetAuthenticated(userID)
	g.trackUser(userID, cc)

	// Once per session: MsgAuth is sent once per connection, and a client that
	// re-authenticates on a live socket is the duplicate-login case above, which
	// is itself rare and separately logged.
	g.logger.Info("auth ok",
		"conn", cc.ID(), "user", userID, "ip", cc.RemoteIP(),
		"dur_ms", time.Since(start).Milliseconds())

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

// logAuthFailure reports a rejected login. The first failure on a connection is
// logged at the given level; every later one on the same connection drops to
// debug, because MsgAuth is client-driven and a socket looping bad tokens must
// not be able to drive log volume — the message limiter alone still permits 60
// frames a second. userID is "" when the token never verified,
// which is most of the time — the whole point is that we do not know who this is.
//
// The token itself is never an argument here, by construction.
func (g *Gateway) logAuthFailure(cc *ClientConn, level slog.Level, userID, reason string, err error) {
	attrs := []any{"conn", cc.ID(), "ip", cc.RemoteIP(), "reason", reason, "err", err}
	if userID != "" {
		attrs = append(attrs, "user", userID)
	}
	if !cc.firstAuthFailure() {
		level = slog.LevelDebug
	}
	g.logger.Log(context.Background(), level, "auth failed", attrs...)
}

// Machine-readable kick reasons. They travel in both MsgKick and the paired
// MsgDisconnect, so the two frames never disagree about why a client was
// evicted.
const (
	KickReasonDuplicateLogin = "duplicate_login"
)

// kickLocalUser finds a local connection for userID and evicts it with
// MsgKick(reason) followed by MsgDisconnect(reason), then closes it.
func (g *Gateway) kickLocalUser(userID string) {
	old := g.findUserConn(userID)
	if old == nil {
		return
	}
	g.sendKickAndClose(old, KickReasonDuplicateLogin)
	// ClearIdentity so cleanupSession in handleConn's defer doesn't destroy the
	// session we're about to overwrite.
	old.ClearIdentity()
	g.untrackUser(userID, old)
}

// sendKickAndClose evicts a connection with two frames — MsgKick then
// MsgDisconnect, both carrying the same reason — and closes once they flush.
//
// Both are sent because they address different clients, not because the
// information differs. MsgKick is the typed eviction signal; MsgDisconnect is
// what every client built before MsgKick existed already acts on, and an old
// client silently ignores an unknown type 15. Sending only MsgKick would strand
// those clients on a socket that simply stops answering.
//
// Order matters and is part of the contract: a client that understands MsgKick
// reads the reason from it and MUST treat the MsgDisconnect that follows as the
// same eviction rather than a second event. Reversing the order would make a new
// client act on the weaker frame first. See gateway/docs/API.md.
//
// Both frames use NewEnvelope (JSON) rather than cc.Reply: this runs on the
// *evicting* connection's goroutine, and cc.enc may only be read from the
// evicted connection's own ReadLoop.
func (g *Gateway) sendKickAndClose(cc *ClientConn, reason string) {
	// Every eviction funnels through here, so this one line covers both the
	// local duplicate-login kick and one arriving from another gateway. Once
	// per evicted session by construction.
	g.logger.Info("client evicted", "conn", cc.ID(), "user", cc.UserID(), "reason", reason)

	kick, kickErr := messages.NewEnvelope(messages.MsgKick, messages.KickMessage{
		Reason: reason,
	})
	disc, discErr := messages.NewEnvelope(messages.MsgDisconnect, messages.DisconnectMessage{
		Reason: reason,
	})
	if kickErr != nil || discErr != nil {
		// Encoding a two-field struct cannot realistically fail, but a close
		// with no explanation still beats leaving the socket open.
		g.logger.Error("encode kick frames", "conn", cc.ID(), "user", cc.UserID(), "reason", reason,
			"kick_err", kickErr, "disconnect_err", discErr)
		cc.Close()
		return
	}
	cc.Send(kick)
	// SendAndClose on the *last* frame only: WriteLoop half-closes once the
	// queue drains, so both frames are on the wire before the FIN.
	cc.SendAndClose(disc)
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

// handleEnterWorld assigns a game server for the requested map and mints the
// join token the client presents to it.
//
// Logging note: result.JoinToken is deliberately absent from every line here.
// It is a bearer credential for the game server — logging one would let anyone
// with log access join as that player. The server id and address it names are
// logged instead, which is what an operator actually needs to follow a client
// to the next hop.
func (g *Gateway) handleEnterWorld(cc *ClientConn, env messages.Envelope) {
	start := time.Now()
	userID, state := cc.Identity()
	if state != StateAuthenticated && state != StateInWorld {
		g.metrics.EnterWorldResult(false)
		g.logger.Warn("enter world failed",
			"conn", cc.ID(), "ip", cc.RemoteIP(), "reason", "not_authenticated")
		g.sendEnterWorldError(cc, "not authenticated")
		return
	}

	var req messages.EnterWorldRequest
	if err := env.UnmarshalPayload(&req); err != nil {
		g.metrics.EnterWorldResult(false)
		g.logger.Warn("enter world failed",
			"conn", cc.ID(), "user", userID, "reason", "malformed_request", "err", err)
		g.sendEnterWorldError(cc, "invalid enter world request")
		return
	}

	// One deadline over the WHOLE assignment path. Each leg beneath it —
	// registry lookup retries, the Agones allocation call, the wait for the
	// allocated pod to self-register — carries its own timeout, and stacked
	// worst-case they exceed MaxHandlerBlockingWait, which means the heartbeat
	// would kill this connection mid-allocation (issue #235). The budget keeps
	// the block strictly inside the heartbeat window with margin for the
	// write-back below; on expiry the client gets the retryable "server is
	// starting, retry shortly" while the allocation leader carries on detached
	// (registry.allocateOnce), so a later retry finds the server ready.
	ctx, cancel := context.WithTimeout(context.Background(), g.enterWorldBudget)
	defer cancel()
	result, err := transfer.AssignMapKeyring(ctx, userID, req.MapID, g.registry, g.joinKeys)
	if err != nil {
		g.metrics.EnterWorldResult(false)
		g.logger.Error("enter world failed",
			"conn", cc.ID(), "user", userID, "map", req.MapID,
			"reason", "no_assignment", "err", err,
			"dur_ms", time.Since(start).Milliseconds())
		g.sendEnterWorldError(cc, clientSafeAssignError(err))
		return
	}
	g.metrics.EnterWorldResult(true)

	cc.SetInWorld()

	// Once per session: this is the map-assignment handshake, not a per-tick
	// path — gameplay frames never reach this process at all (ADR-3). It is
	// also the only record anywhere of which server a client was sent to, so
	// it carries the server id and address the client is about to dial.
	g.logger.Info("enter world assigned",
		"conn", cc.ID(), "user", userID, "map", req.MapID,
		"server", result.ServerID, "server_addr", result.ServerAddr,
		"transport", result.Transport,
		"dur_ms", time.Since(start).Milliseconds())

	// Update session with server and map association (task 2c). On its own
	// context, not the budget one: an assignment that resolved near the
	// deadline must still record where the client went.
	if uerr := g.sessions.UpdateSession(context.Background(), userID, func(sd *session.SessionData) {
		sd.ServerID = result.ServerID
		sd.MapID = req.MapID
		// The jti of the token minted above. This is what a later duplicate
		// login publishes in its supersede event, so the game server can kick
		// exactly the connection this assignment produced (kick.go). Updated
		// on every assignment — a map transfer re-enters this path, so the
		// session always names the CURRENT game-server connection's jti.
		sd.JoinTokenJTI = result.JTI
	}); uerr != nil {
		g.logger.Warn("update session server association",
			"user", userID, "server", result.ServerID, "map", req.MapID, "err", uerr)
	}

	resp, err := cc.Reply(messages.MsgEnterWorldResp, messages.EnterWorldResponse{
		ServerAddr: result.ServerAddr,
		JoinToken:  result.JoinToken,
		Transport:  result.Transport,
		SessionKey: result.SessionKey,
	})
	if err != nil {
		g.logger.Error("marshal enter world response", "err", err)
		return
	}
	cc.Send(resp)
}

// handleDisconnect processes an explicit client MsgDisconnect: destroy the
// session, then close the socket.
//
// It logs the session end itself rather than leaving it to handleConn's defer:
// cleanupSession clears the identity here, so by the time the defer runs the
// user is already gone and it can only emit the anonymous debug line.
func (g *Gateway) handleDisconnect(cc *ClientConn) {
	g.logger.Info("client disconnect", "conn", cc.ID(), "user", cc.UserID(), "ip", cc.RemoteIP())
	g.cleanupSession(cc)
	cc.CloseGracefully()
}

// Client-facing messages for EnterWorld failures.
const (
	msgNoServerAvailable = "no server available for map"
	// msgServerStarting is a retryable EnterWorld failure: a game server was
	// allocated for the map but has not finished booting and registering
	// itself. The client must be able to tell this apart from
	// msgNoServerAvailable ("this map is full" — do not retry) and retry
	// shortly instead. It names no internal detail. It is not the only
	// retryable failure — see msgFleetBusy, which is the other one and means
	// something different: there, no server was allocated at all.
	msgServerStarting = "server is starting, retry shortly"
	// msgUnknownMap is the terminal EnterWorld failure: no fleet or server in
	// this deployment hosts the requested map. It must not read like
	// msgServerStarting — a client that retries this one drives the very
	// allocation leak registry.rememberMismatch exists to bound — and it must not
	// read like msgNoServerAvailable either, which means "this map exists but is
	// full". It names no fleet, namespace or server id.
	msgUnknownMap = "map is not available"
	// msgFleetBusy is the second retryable EnterWorld failure: the map has no
	// live server and the allocation API answered `UnAllocated` — every
	// GameServer in the fleet is taken and none is Ready at this instant
	// (registry.ErrNoCapacity). It is deliberately NOT msgNoServerAvailable:
	// that one means "this map exists and is full", a stable condition ADR-2
	// forbids growing out of, whereas this one routinely clears in seconds as
	// the Fleet controller brings a replacement pod to Ready (measured 5.38s on
	// k3d, ADR-18). It is also not msgServerStarting: nothing was allocated
	// here, so there is no booting server of the client's to wait for.
	//
	// Retrying this one is safe in the way retrying the others is not. An
	// `UnAllocated` answer is a decoded 2xx body stating that no GameServer was
	// handed out, so a retry costs one allocation POST and leaks no pod — and
	// the retry that finally succeeds usually costs not even that: pods
	// self-register at startup before any allocation (ADR-18), so once the
	// replacement is Ready the next EnterWorld resolves it straight from the
	// registry with no allocator call at all. It names no fleet or namespace.
	msgFleetBusy      = "all servers busy, retry shortly"
	msgNotImplemented = "not implemented"
	msgInternalError  = "internal error"
)

func clientSafeAssignError(err error) string {
	switch {
	// ErrServerStarting is checked first: it is the more specific condition and
	// must not be flattened into the do-not-retry message.
	case errors.Is(err, registry.ErrServerStarting):
		return msgServerStarting
	// A map the deployment cannot serve is terminal for this client: retrying
	// cannot make a fleet host a different map, and every retry allocates.
	case errors.Is(err, registry.ErrFleetMapMismatch):
		return msgUnknownMap
	// A momentarily exhausted fleet is checked before ErrNoServerAvailable and
	// must stay ahead of it: allocateAndWait wraps both sentinels into one error
	// (`"%w %s: allocate: %w"`), so the broader capacity sentinel would swallow
	// this one and report a self-correcting condition as terminal. Narrow on
	// purpose — only ErrNoCapacity, which proves nothing was allocated. Any
	// other allocator failure (transport error, non-2xx, undecodable body) may
	// have allocated a pod whose response was lost, and telling a client to
	// retry that is exactly how un-reclaimable pods pile up.
	case errors.Is(err, registry.ErrNoCapacity):
		return msgFleetBusy
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
