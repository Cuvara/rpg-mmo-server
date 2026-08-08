package server

import (
	"net"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/ratelimit"
)

func TestGateway_PingReturnsMatchingPong(t *testing.T) {
	gw := startTestGateway(t)
	defer gw.Shutdown()

	conn := dialGateway(t, gw)
	defer conn.Close()

	// Send a ping.
	ping := messages.PingMessage{Timestamp: 42}
	env, _ := messages.NewEnvelope(messages.MsgPing, ping)
	sendEnvelope(t, conn, env)

	// Read pong — it may be preceded by a server-initiated ping, so skip any
	// MsgPing frames that arrive first.
	conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	for {
		resp, err := messages.Decode(conn)
		if err != nil {
			t.Fatalf("decode: %v", err)
		}
		if resp.Type == messages.MsgPing {
			continue // server-initiated ping, skip
		}
		if resp.Type != messages.MsgPong {
			t.Fatalf("expected MsgPong, got %d", resp.Type)
		}

		var pong messages.PongMessage
		if err := resp.UnmarshalPayload(&pong); err != nil {
			t.Fatalf("unmarshal pong: %v", err)
		}
		if pong.Timestamp != 42 {
			t.Errorf("Timestamp = %d, want 42", pong.Timestamp)
		}
		if pong.ServerTime == 0 {
			t.Error("ServerTime should not be zero")
		}
		break
	}
}

func TestHeartbeat_TimeoutClosesConnection(t *testing.T) {
	// Use a pipe so we can control exactly what the peer sends.
	clientConn, serverConn := net.Pipe()
	defer clientConn.Close()
	defer serverConn.Close()

	cc := NewClientConn(serverConn, logger.New("error"), ratelimit.Bucket{})

	// Set lastPong far in the past — beyond pongTimeout.
	cc.lastPong.Store(time.Now().Add(-pongTimeout - time.Second).UnixMilli())

	// Start the heartbeat loop; it should detect the timeout on the first tick.
	go cc.HeartbeatLoop()

	// The done channel should close within a couple of ticks.
	select {
	case <-cc.Done():
		// success — connection was closed by the heartbeat loop.
	case <-time.After(pingInterval + 2*time.Second):
		t.Fatal("heartbeat did not close the connection after pong timeout")
	}
}

func TestRecordPong_RefreshesTimestamp(t *testing.T) {
	clientConn, serverConn := net.Pipe()
	defer clientConn.Close()
	defer serverConn.Close()

	cc := NewClientConn(serverConn, logger.New("error"), ratelimit.Bucket{})

	before := cc.lastPong.Load()
	time.Sleep(2 * time.Millisecond)
	cc.RecordPong()
	after := cc.lastPong.Load()

	if after <= before {
		t.Errorf("RecordPong did not advance lastPong: before=%d after=%d", before, after)
	}
}
