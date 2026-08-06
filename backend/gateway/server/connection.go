package server

import (
	"io"
	"log/slog"
	"net"
	"sync"
	"sync/atomic"

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

// ClientConn wraps a TCP connection with state tracking for a gateway client.
type ClientConn struct {
	conn   net.Conn
	UserID string
	State  ConnState
	sendCh chan messages.Envelope
	done   chan struct{}
	once   sync.Once
	logger *slog.Logger

	// msgBucket rate-limits inbound frames on this connection.
	//
	// It is a struct field, not a map lookup, on purpose: this is the only
	// per-message check in the gateway's read path, so it must cost a few
	// float ops and zero allocations. It is only ever touched from ReadLoop's
	// goroutine, so it needs no lock. A zero-Rate bucket (the default) allows
	// everything.
	msgBucket ratelimit.Bucket

	// limited is set once this connection tripped the message limiter and is
	// being torn down. Read-loop goroutine only, like msgBucket. It makes the
	// "reply once, then go quiet" behaviour explicit: without it a flooding
	// client would get one error frame per over-limit frame, turning the
	// limiter into an amplifier.
	limited bool

	// closeAfterFlush tells WriteLoop to close the connection once sendCh is
	// empty, so a final error frame actually reaches the client instead of
	// racing an immediate Close.
	closeAfterFlush atomic.Bool
}

// NewClientConn creates a new client connection wrapper. The bucket is the
// per-connection inbound message limiter; pass the zero value for no limit.
func NewClientConn(conn net.Conn, logger *slog.Logger, bucket ratelimit.Bucket) *ClientConn {
	return &ClientConn{
		conn:      conn,
		State:     StateConnected,
		sendCh:    make(chan messages.Envelope, 64),
		done:      make(chan struct{}),
		logger:    logger,
		msgBucket: bucket,
	}
}

// RemoteIP returns the peer's IP without the port, for use as a rate-limit key.
// It falls back to the raw address string when the address has no port (which
// no real net.Conn produces, but a test fake might).
func (c *ClientConn) RemoteIP() string { return remoteIP(c.conn) }

// allowMessage consumes one token from the connection's message bucket.
// Called only from ReadLoop's goroutine.
func (c *ClientConn) allowMessage() bool { return c.msgBucket.Allow() }

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

// Close shuts down the connection.
func (c *ClientConn) Close() {
	c.once.Do(func() {
		close(c.done)
		c.conn.Close()
	})
}

// Done returns a channel that is closed when the connection ends.
func (c *ClientConn) Done() <-chan struct{} {
	return c.done
}

// ReadLoop reads envelopes from the TCP connection and calls handler for each.
func (c *ClientConn) ReadLoop(handler func(conn *ClientConn, env messages.Envelope)) {
	defer c.Close()
	for {
		env, err := messages.Decode(c.conn)
		if err != nil {
			if err != io.EOF {
				c.logger.Debug("read error", "user", c.UserID, "err", err)
			}
			return
		}
		handler(c, env)
	}
}

// WriteLoop sends envelopes from the send channel to the TCP connection.
func (c *ClientConn) WriteLoop() {
	defer c.Close()
	for {
		select {
		case env := <-c.sendCh:
			data, err := messages.Encode(env)
			if err != nil {
				c.logger.Error("encode error", "user", c.UserID, "err", err)
				return
			}
			if _, err := c.conn.Write(data); err != nil {
				c.logger.Debug("write error", "user", c.UserID, "err", err)
				return
			}
			// Deferred close requested (rate limiter, protocol abort): leave
			// once the queue is drained so the client sees the reason.
			if c.closeAfterFlush.Load() && len(c.sendCh) == 0 {
				return
			}
		case <-c.done:
			return
		}
	}
}
