package server

import (
	"io"
	"log/slog"
	"net"
	"sync"

	"github.com/duycuong/rpg-mmo/shared/messages"
)

// Connection wraps a TCP connection for a single player.
type Connection struct {
	conn   net.Conn
	UserID string
	sendCh chan messages.Envelope
	done   chan struct{}
	once   sync.Once
	logger *slog.Logger
}

// NewConnection creates a connection wrapper.
func NewConnection(conn net.Conn, userID string, logger *slog.Logger) *Connection {
	return &Connection{
		conn:   conn,
		UserID: userID,
		sendCh: make(chan messages.Envelope, 64),
		done:   make(chan struct{}),
		logger: logger,
	}
}

// Send enqueues a message to be sent to the client.
func (c *Connection) Send(env messages.Envelope) {
	select {
	case c.sendCh <- env:
	case <-c.done:
	}
}

// Close shuts down the connection.
func (c *Connection) Close() {
	c.once.Do(func() {
		close(c.done)
		c.conn.Close()
	})
}

// Done returns a channel that's closed when the connection is done.
func (c *Connection) Done() <-chan struct{} {
	return c.done
}

// ReadLoop reads envelopes from the TCP connection and calls handler for each.
func (c *Connection) ReadLoop(handler func(conn *Connection, env messages.Envelope)) {
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
func (c *Connection) WriteLoop() {
	defer c.Close()
	for {
		select {
		case env := <-c.sendCh:
			data, err := messages.Encode(env)
			if err != nil {
				c.logger.Error("encode error", "user", c.UserID, "err", err)
				continue
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
