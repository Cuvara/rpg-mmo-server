package server

import (
	"io"
	"log/slog"
	"net"
	"sync"
	"sync/atomic"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/ratelimit"
)

// ConnState represents the connection lifecycle state.
type ConnState int

const (
	StateConnected     ConnState = iota // TCP connected, not yet authenticated
	StateAuthenticated                  // JWT verified, session created
	StateInWorld                        // Assigned to a game server
)

// connSeq numbers connections for logging. It is process-local and monotonic,
// not a durable identifier: its only job is to let every line belonging to one
// client session be pulled out of an interleaved log with a single grep.
var connSeq atomic.Uint64

// ClientConn wraps a TCP connection with state tracking for a gateway client.
type ClientConn struct {
	conn net.Conn

	// id is this connection's log correlation number, assigned at accept and
	// immutable, so it can be read without a lock from any goroutine.
	id uint64

	// mu guards the connection's identity (userID/state). Unlike msgBucket
	// below, identity genuinely crosses goroutines: the read side writes it
	// (auth, session expiry, teardown in handleConn's defer) while the write
	// side reads it for every log line it emits. Exported accessors are the
	// only way in — the fields used to be exported and plain, which is exactly
	// how a write in cleanupSession raced a read in CloseGracefully.
	mu     sync.RWMutex
	userID string
	state  ConnState

	sendCh chan messages.Envelope
	done   chan struct{}
	once   sync.Once
	logger *slog.Logger

	// msgBucket rate-limits inbound frames on this connection.
	//
	// It is a struct field, not a map lookup, on purpose: this is the only
	// per-message check in the gateway's read path, so it must cost a few
	// float ops and zero allocations. It is only ever touched from ReadLoop's
	// goroutine — allowMessage is called from handleMessage and nowhere else —
	// so it needs no lock. A zero-Rate bucket (the default) allows everything.
	// Audited: unlike userID/state above, nothing on the write side reads it.
	msgBucket ratelimit.Bucket

	// limited is set once this connection tripped the message limiter and is
	// being torn down. Read-loop goroutine only, like msgBucket. It makes the
	// "reply once, then go quiet" behaviour explicit: without it a flooding
	// client would get one error frame per over-limit frame, turning the
	// limiter into an amplifier.
	limited bool

	// loggedAuthFail / loggedUnexpected latch the two rejection lines that a
	// client can otherwise mint on demand: nothing stops a socket from looping
	// MsgAuth with a bad token, or sending an unroutable message type. The
	// message limiter bounds that but does not make it safe — its default is 60
	// frames per second per connection, so an unlatched line would still be 60
	// log lines a second from one socket. The first occurrence on a connection
	// is reported at its natural level and the rest drop to debug, which keeps
	// the diagnostic (you always see the first failure and its reason) while
	// bounding the worst case at one line per connection.
	//
	// Read-loop goroutine only, like msgBucket above — every write is on
	// handleMessage's call stack — so they need no lock.
	loggedAuthFail   bool
	loggedUnexpected bool

	// lastSessionRefresh is when a MsgPong last re-armed the session TTL, used
	// to bound pong-driven store writes to one per sessionRefreshInterval. Zero
	// means never, so the first pong on an authenticated connection refreshes.
	// Read-loop goroutine only, like msgBucket above — every access is on
	// handleMessage's call stack — so it needs no lock.
	lastSessionRefresh time.Time

	// enc is the wire encoding this connection speaks, latched from the first
	// frame decoded on it and used for every reply.
	//
	// The gateway never chooses an encoding: it answers in whatever the client
	// used. That is what lets a Protobuf gateway keep serving a JSON client, and
	// lets the gateway and the game servers be upgraded in either order — see
	// shared/docs/DESIGN.md. Zero value is EncodingJSON, so a connection that
	// somehow replies before reading anything stays on the legacy encoding.
	//
	// Read-loop goroutine only, like msgBucket above: every reply is built from
	// handleMessage's call stack, and nothing on the write side reads it, so it
	// needs no lock.
	enc messages.Encoding

	// lastPong is the time the last MsgPong was received (or the connection
	// was created). The heartbeat loop compares this against pongTimeout to
	// declare the peer dead. Written by ReadLoop (handlePong), read by the
	// heartbeat goroutine — both through atomic load/store on the int64, so
	// no lock is needed.
	lastPong atomic.Int64

	// closeAfterFlush tells WriteLoop to shut the connection down once sendCh
	// is empty, so a final error frame actually reaches the client instead of
	// racing an immediate Close.
	closeAfterFlush atomic.Bool

	// halfClosed is set once the write side has been shut down with
	// CloseWrite. It guards the transition so the half-close happens exactly
	// once even if both WriteLoop and a handler ask for it.
	halfClosed atomic.Bool
}

// closeDrainTimeout bounds how long a half-closed connection waits for the peer
// to notice the FIN and close its side. It only ever applies to a client that
// has been told to go away and refuses to; a well-behaved one closes in one
// RTT. Short enough that an abusive client cannot pin a socket, long enough to
// cover a bad mobile RTT.
const closeDrainTimeout = 2 * time.Second

// Heartbeat protocol constants. Both sides send MsgPing every pingInterval;
// if no MsgPong arrives within pongTimeout the connection is considered dead.
const (
	pingInterval = 10 * time.Second
	pongTimeout  = 30 * time.Second
)

// sessionRefreshInterval bounds how often a heartbeat MsgPong re-arms the
// session TTL in the store. Heartbeats arrive every pingInterval (10s), but the
// session TTL is an hour — re-arming it on every pong would be one store write
// per connection per 10s for no gain. Once a minute keeps the sliding window
// accurate to well under 0.1% of the TTL at a sixth of the write rate.
const sessionRefreshInterval = time.Minute

// MaxHandlerBlockingWait is the longest a message handler may block before it
// starves the heartbeat, and is exported so start-up can refuse a configuration
// that exceeds it.
//
// A connection is served by ONE goroutine: the read loop dispatches a frame,
// and the next frame — including the client's MsgPong — is not read until that
// handler returns. A handler that blocks therefore stops this connection's
// pongs from being recorded, and HeartbeatLoop closes the connection after
// pongTimeout of silence. handleEnterWorld is the one handler that can block for
// a configurable time (it waits for a freshly allocated game server to
// register), which is exactly the case that must not exceed this.
//
// The margin is one full pingInterval: at pongTimeout-pingInterval a single lost
// or delayed ping still leaves a whole ping period for a pong to arrive and be
// processed before the connection is judged dead. Anything closer, and the
// gateway drops the very client it is holding the socket open for — with a
// symptom (client vanishes during a slow allocation) that points nowhere near
// the cause.
const MaxHandlerBlockingWait = pongTimeout - pingInterval

// halfCloser is the optional half-close capability of a net.Conn.
// *net.TCPConn and *net.UnixConn implement it; kcp.UDPSession does not.
type halfCloser interface{ CloseWrite() error }

// NewClientConn creates a new client connection wrapper. The bucket is the
// per-connection inbound message limiter; pass the zero value for no limit.
func NewClientConn(conn net.Conn, logger *slog.Logger, bucket ratelimit.Bucket) *ClientConn {
	cc := &ClientConn{
		conn:      conn,
		id:        connSeq.Add(1),
		state:     StateConnected,
		sendCh:    make(chan messages.Envelope, 64),
		done:      make(chan struct{}),
		logger:    logger,
		msgBucket: bucket,
	}
	cc.lastPong.Store(time.Now().UnixMilli())
	return cc
}

// ID returns the connection's log correlation number. Immutable, lock-free.
func (c *ClientConn) ID() uint64 { return c.id }

// UserID returns the authenticated user bound to this connection, or "" when
// it has no identity. Safe from any goroutine.
func (c *ClientConn) UserID() string {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.userID
}

// State returns the connection's lifecycle state. Safe from any goroutine.
func (c *ClientConn) State() ConnState {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.state
}

// Identity returns userID and state together. Callers that branch on both must
// use this rather than two calls, so they cannot observe a half-applied
// transition (a userID from before a clear paired with a state from after it).
func (c *ClientConn) Identity() (string, ConnState) {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.userID, c.state
}

// SetAuthenticated binds a verified user to the connection.
func (c *ClientConn) SetAuthenticated(userID string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.userID = userID
	c.state = StateAuthenticated
}

// SetInWorld marks the connection as assigned to a game server. It is a no-op
// if the connection lost its identity in the meantime, so a late assignment
// cannot resurrect a torn-down connection into StateInWorld with no user.
func (c *ClientConn) SetInWorld() {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.userID == "" {
		return
	}
	c.state = StateInWorld
}

// ClearIdentity drops the connection back to an unauthenticated state and
// returns the user it was bound to, or "" if it had none.
//
// Returning the cleared value is what makes session teardown safe to call from
// more than one path: the check ("was there a session?") and the act ("take
// it") happen under one lock, so an explicit MsgDisconnect and the deferred
// cleanup in handleConn cannot both decide they own the same session.
func (c *ClientConn) ClearIdentity() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	userID := c.userID
	c.userID = ""
	c.state = StateConnected
	return userID
}

// SetReadDeadline sets the read deadline on the underlying connection.
func (c *ClientConn) SetReadDeadline(t time.Time) error { return c.conn.SetReadDeadline(t) }

// ClearReadDeadline removes any read deadline on the underlying connection.
func (c *ClientConn) ClearReadDeadline() error { return c.conn.SetReadDeadline(time.Time{}) }

// RemoteIP returns the peer's IP without the port, for use as a rate-limit key.
// It falls back to the raw address string when the address has no port (which
// no real net.Conn produces, but a test fake might).
func (c *ClientConn) RemoteIP() string { return remoteIP(c.conn) }

// allowMessage consumes one token from the connection's message bucket.
// Called only from ReadLoop's goroutine.
func (c *ClientConn) allowMessage() bool { return c.msgBucket.Allow() }

// firstAuthFailure reports whether this is the first auth rejection on this
// connection, latching for subsequent calls. ReadLoop goroutine only.
func (c *ClientConn) firstAuthFailure() bool {
	first := !c.loggedAuthFail
	c.loggedAuthFail = true
	return first
}

// shouldRefreshSession reports whether at least sessionRefreshInterval has
// passed since the last pong-driven session refresh, recording now as the new
// mark when it has. ReadLoop goroutine only, like firstAuthFailure.
func (c *ClientConn) shouldRefreshSession(now time.Time) bool {
	if !c.lastSessionRefresh.IsZero() && now.Sub(c.lastSessionRefresh) < sessionRefreshInterval {
		return false
	}
	c.lastSessionRefresh = now
	return true
}

// firstUnexpectedMessage reports whether this is the first unroutable message
// type on this connection, latching for subsequent calls. ReadLoop goroutine
// only.
func (c *ClientConn) firstUnexpectedMessage() bool {
	first := !c.loggedUnexpected
	c.loggedUnexpected = true
	return first
}

// remoteIP extracts the host part of a connection's remote address.
func remoteIP(conn net.Conn) string {
	if conn == nil {
		return ""
	}
	addr := conn.RemoteAddr()
	if addr == nil {
		return ""
	}
	host, _, err := net.SplitHostPort(addr.String())
	if err != nil {
		return addr.String()
	}
	return host
}

// Send enqueues a message to be sent to the client.
func (c *ClientConn) Send(env messages.Envelope) {
	select {
	case c.sendCh <- env:
	case <-c.done:
	}
}

// SendAndClose enqueues a final message and closes the connection as soon as
// the write loop has flushed everything queued.
func (c *ClientConn) SendAndClose(env messages.Envelope) {
	select {
	case c.sendCh <- env:
		c.closeAfterFlush.Store(true)
	case <-c.done:
	}
}

// Close shuts down the connection immediately.
//
// On TCP this is a *hard* close. If the kernel receive queue still holds unread
// bytes — which is exactly the situation when a client is being disconnected
// for flooding — the kernel answers with RST instead of FIN, and an RST
// discards the socket's unsent send buffer. Any frame written just before the
// Close can therefore be thrown away before it reaches the client, so the
// client observes a bare disconnect with no reason.
//
// Any path that writes an explanatory frame and then disconnects must call
// CloseGracefully instead. Close remains correct for the abrupt cases
// (shutdown, read error, encode failure) where there is nothing left to
// deliver.
func (c *ClientConn) Close() {
	c.once.Do(func() {
		close(c.done)
		c.conn.Close()
	})
}

// CloseGracefully shuts the write side down with CloseWrite so the pending
// bytes leave with a FIN rather than being discarded by an RST, then leaves the
// read side open for ReadLoop to drain.
//
// The sequence is:
//
//  1. CloseWrite — everything already written (including the final error frame)
//     is flushed and followed by a FIN. The client reads the frame, then EOF.
//  2. A read deadline bounds the wait so a client that ignores the FIN cannot
//     pin the socket.
//  3. ReadLoop — the connection's *only* reader — keeps consuming the inbound
//     backlog until EOF or the deadline, then its deferred Close runs. By then
//     the receive queue is empty, so that Close is a clean FIN, not an RST.
//
// Draining is deliberately left to ReadLoop instead of being done here: the
// socket has exactly one reader, and adding a second one racing Decode
// mid-frame would corrupt the very teardown this is meant to make orderly.
//
// Transports without half-close (KCP — kcp.UDPSession has no CloseWrite) fall
// back to a plain Close. That is safe there for the reason it is unsafe on TCP:
// there is no kernel receive queue and no RST, and kcp-go flushes pending
// output on Close.
func (c *ClientConn) CloseGracefully() {
	if !c.halfClosed.CompareAndSwap(false, true) {
		return // already half-closed; ReadLoop owns the rest
	}
	hc, ok := c.conn.(halfCloser)
	if !ok {
		c.Close()
		return
	}
	if err := hc.CloseWrite(); err != nil {
		// Peer already gone, or a conn that reports the capability but cannot
		// honour it. Nothing left to deliver, so fall back.
		c.logger.Debug("close write", "conn", c.id, "user", c.UserID(), "err", err)
		c.Close()
		return
	}
	// Bound the drain. ReadLoop turns this into an error and exits, which runs
	// the deferred Close on an empty receive queue.
	if err := c.conn.SetReadDeadline(time.Now().Add(closeDrainTimeout)); err != nil {
		c.logger.Debug("set drain deadline", "conn", c.id, "user", c.UserID(), "err", err)
		c.Close()
	}
}

// Done returns a channel that is closed when the connection ends.
func (c *ClientConn) Done() <-chan struct{} {
	return c.done
}

// RecordPong updates the last-pong timestamp. Called from the message handler
// when a MsgPong arrives.
func (c *ClientConn) RecordPong() { c.lastPong.Store(time.Now().UnixMilli()) }

// HeartbeatLoop sends MsgPing every pingInterval and closes the connection if
// no MsgPong has been received within pongTimeout. It is intended to run in
// its own goroutine and returns when the connection closes.
func (c *ClientConn) HeartbeatLoop() {
	ticker := time.NewTicker(pingInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ticker.C:
			// Check for pong timeout.
			last := c.lastPong.Load()
			if time.Since(time.UnixMilli(last)) > pongTimeout {
				c.logger.Info("heartbeat timeout", "conn", c.id, "user", c.UserID())
				c.Close()
				return
			}
			// Send ping.
			env, err := c.Reply(messages.MsgPing, messages.PingMessage{
				Timestamp: time.Now().UnixMilli(),
			})
			if err != nil {
				c.logger.Debug("heartbeat encode error", "conn", c.id, "err", err)
				continue
			}
			c.Send(env)
		case <-c.done:
			return
		}
	}
}

// ReadLoop reads envelopes from the TCP connection and calls handler for each.
func (c *ClientConn) ReadLoop(handler func(conn *ClientConn, env messages.Envelope)) {
	defer c.Close()
	for {
		env, err := messages.Decode(c.conn)
		if err != nil {
			if err != io.EOF {
				c.logger.Debug("read error", "conn", c.id, "user", c.UserID(), "err", err)
			}
			return
		}
		c.enc = env.Enc
		handler(c, env)
	}
}

// Reply builds an envelope in the encoding this connection speaks.
//
// Every gateway response goes through here rather than through
// messages.NewEnvelope, so that adding a new response type cannot accidentally
// pin it to JSON.
func (c *ClientConn) Reply(msgType messages.MsgType, payload any) (messages.Envelope, error) {
	return messages.NewEnvelopeAs(c.enc, msgType, payload)
}

// WriteLoop sends envelopes from the send channel to the TCP connection.
//
// It ends in one of two ways. A deferred-close request (SendAndClose) drains
// the queue and then half-closes, so the last frame is guaranteed to reach the
// client. Anything else — an encode failure, a dead socket, Shutdown — is an
// abrupt end with nothing left worth delivering, so it hard-closes.
func (c *ClientConn) WriteLoop() {
	if c.writeLoop() {
		c.CloseGracefully()
		return
	}
	c.Close()
}

// writeLoop pumps the send queue and reports whether it stopped because a
// deferred close was requested and fully flushed (true) rather than because the
// connection failed or was torn down (false).
func (c *ClientConn) writeLoop() bool {
	for {
		select {
		case env := <-c.sendCh:
			data, err := messages.Encode(env)
			if err != nil {
				c.logger.Error("encode error", "conn", c.id, "user", c.UserID(), "err", err)
				return false
			}
			if _, err := c.conn.Write(data); err != nil {
				c.logger.Debug("write error", "conn", c.id, "user", c.UserID(), "err", err)
				return false
			}
			// Deferred close requested (rate limiter, protocol abort): leave
			// once the queue is drained so the client sees the reason.
			if c.closeAfterFlush.Load() && len(c.sendCh) == 0 {
				return true
			}
		case <-c.done:
			return false
		}
	}
}
