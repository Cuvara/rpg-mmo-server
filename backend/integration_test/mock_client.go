package integration

import (
	"net"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// MockClient simulates a game client speaking length-prefixed JSON envelopes
// over any transport kind. Used in integration tests to drive the full flow:
// gateway auth -> enter world -> gameserver join -> input/snapshot cycle.
type MockClient struct {
	conn net.Conn
}

// NewMockClient dials the given address over TCP with a 2-second timeout.
func NewMockClient(addr string) (*MockClient, error) {
	return NewMockClientTransport(transport.KindTCP, addr)
}

// NewMockClientTransport dials addr over the given transport kind ("tcp" or
// "kcp"; empty means tcp) with a 2-second timeout.
func NewMockClientTransport(kind, addr string) (*MockClient, error) {
	conn, err := transport.Dial(kind, addr, 2*time.Second)
	if err != nil {
		return nil, err
	}
	return &MockClient{conn: conn}, nil
}

// Send encodes and writes a length-prefixed Envelope to the connection.
func (c *MockClient) Send(env messages.Envelope) error {
	data, err := messages.Encode(env)
	if err != nil {
		return err
	}
	_, err = c.conn.Write(data)
	return err
}

// Receive reads one length-prefixed Envelope from the connection.
// Returns an error if no message arrives within 5 seconds.
func (c *MockClient) Receive() (messages.Envelope, error) {
	c.conn.SetReadDeadline(time.Now().Add(5 * time.Second))
	return messages.Decode(c.conn)
}

// Close shuts down the underlying TCP connection.
func (c *MockClient) Close() error {
	return c.conn.Close()
}
