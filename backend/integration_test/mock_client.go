package integration

import (
	"net"
	"os"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// MockClient simulates a game client speaking length-prefixed envelopes. Used
// in integration tests to drive the full flow: gateway auth -> enter world ->
// gameserver join -> input/snapshot cycle. The two hops use fixed transports,
// exactly as the shipped client does: the gateway hop is TCP
// (NewGatewayClient) and the game-server hop is KCP over UDP (NewGameClient,
// NewGameClientFor). There is no TCP game-server client.
type MockClient struct {
	conn net.Conn
}

// NewGatewayClient dials the gateway over TCP with a 2-second timeout.
func NewGatewayClient(addr string) (*MockClient, error) {
	conn, err := transport.Dial(transport.KindTCP, addr, 2*time.Second)
	if err != nil {
		return nil, err
	}
	return &MockClient{conn: conn}, nil
}

// NewGameClient dials a game server over KCP/UDP, the only gameplay
// transport, using TRANSPORT_KEY from the environment (the spawned game
// servers inherit the same environment). KCP has no handshake, so a dead port
// surfaces as the first Receive timing out, not as a dial error.
func NewGameClient(addr string) (*MockClient, error) {
	return NewGameClientAdvertised(transport.Gameplay, addr)
}

// NewGameClientFor dials the game server an EnterWorldResponse names, over the
// transport it names. Anything but kcp fails here, which is how every gateway
// flow test also proves the gateway advertised KCP.
func NewGameClientFor(resp messages.EnterWorldResponse) (*MockClient, error) {
	return NewGameClientAdvertised(resp.Transport, resp.ServerAddr)
}

// NewGameClientAdvertised dials addr after checking advertised is the gameplay
// transport (no fallback).
func NewGameClientAdvertised(advertised, addr string) (*MockClient, error) {
	conn, err := transport.DialGameplay(advertised, addr, 2*time.Second,
		transport.WithKey(os.Getenv(transport.KeyEnvVar)))
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

// Close shuts down the underlying connection.
func (c *MockClient) Close() error {
	return c.conn.Close()
}
