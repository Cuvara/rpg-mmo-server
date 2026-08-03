package server

import (
	"io"
	"log/slog"
	"net"
	"sync"

	"github.com/duycuong/rpg-mmo/shared/messages"
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
}

// NewClientConn creates a new client connection wrapper.
func NewClientConn(conn net.Conn, logger *slog.Logger) *ClientConn {
	return &ClientConn{
		conn:   conn,
		State:  StateConnected,
		sendCh: make(chan messages.Envelope, 64),
		done:   make(chan struct{}),
		logger: logger,
	}
}

// Send enqueues a message to be sent to the client.
func (c *ClientConn) Send(env messages.Envelope) {
	select {
	case c.sendCh <- env:
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
		case <-c.done:
			return
		}
	}
}
