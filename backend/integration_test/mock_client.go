package integration_test

import (
	"net"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
)

// MockClient simulates a game client connecting over TCP with length-prefixed
// JSON envelopes. Used in integration tests to drive the full flow:
// gateway auth -> enter world -> gameserver join -> input/snapshot cycle.
type MockClient struct {
	conn net.Conn
}

// NewMockClient dials the given TCP address with a 2-second timeout.
func NewMockClient(addr string) (*MockClient, error) {
	conn, err := net.DialTimeout("tcp", addr, 2*time.Second)
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
