package server

import (
	"net"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

const testSecret = "test-secret"

// startTestGateway creates and starts a gateway on a random port for testing.
func startTestGateway(t *testing.T) *Gateway {
	t.Helper()

	sessionStore := storage.NewMemorySessionStore()
	serverRegistry := storage.NewMemoryServerRegistry()

	// Pre-register a game server with capacity.
	serverRegistry.Register(nil, storage.ServerInfo{
		ServerID:    "srv1",
		MapID:       "map_forest",
		Addr:        "10.0.0.1:9000",
		Capacity:    100,
		PlayerCount: 10,
	})

	sessions := session.NewSessionManager(sessionStore)
	reg := registry.NewRegistryService(serverRegistry)
	log := logger.New("error") // quiet during tests

	gw := New(sessions, reg, testSecret, log,
		WithJoinTokenSecret(testSecret))

	go func() {
		if err := gw.Run("127.0.0.1:0"); err != nil {
			// Only log if not a clean shutdown.
			select {
			case <-gw.done:
			default:
				t.Logf("gateway run error: %v", err)
			}
		}
	}()

	// Wait for the listener to be ready.
	for i := 0; i < 50; i++ {
		if gw.Addr() != "" {
			return gw
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("gateway did not start in time")
	return nil
}

func dialGateway(t *testing.T, gw *Gateway) net.Conn {
	t.Helper()
	conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
	if err != nil {
		t.Fatalf("dial gateway: %v", err)
	}
	return conn
}

func sendEnvelope(t *testing.T, conn net.Conn, env messages.Envelope) {
	t.Helper()
	data, err := messages.Encode(env)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	conn.SetWriteDeadline(time.Now().Add(2 * time.Second))
	if _, err := conn.Write(data); err != nil {
		t.Fatalf("write: %v", err)
	}
}

func readEnvelope(t *testing.T, conn net.Conn) messages.Envelope {
	t.Helper()
	conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	env, err := messages.Decode(conn)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}
	return env
}

func TestGateway_AuthSuccess(t *testing.T) {
	gw := startTestGateway(t)
	defer gw.Shutdown()

	conn := dialGateway(t, gw)
	defer conn.Close()

	token, _ := jwt.Sign("user1", testSecret, 1*time.Hour)
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn, authEnv)

	resp := readEnvelope(t, conn)
	if resp.Type != messages.MsgAuthResp {
		t.Fatalf("expected MsgAuthResp, got %d", resp.Type)
	}

	var authResp messages.AuthResponse
	if err := resp.UnmarshalPayload(&authResp); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if !authResp.OK {
		t.Errorf("auth failed: %s", authResp.Error)
	}
	if authResp.UserID != "user1" {
		t.Errorf("userID = %q, want %q", authResp.UserID, "user1")
	}
}

func TestGateway_AuthFailure(t *testing.T) {
	gw := startTestGateway(t)
	defer gw.Shutdown()

	conn := dialGateway(t, gw)
	defer conn.Close()

	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: "bad-token"})
	sendEnvelope(t, conn, authEnv)

	resp := readEnvelope(t, conn)
	var authResp messages.AuthResponse
	resp.UnmarshalPayload(&authResp)
	if authResp.OK {
		t.Error("auth should have failed")
	}
	if authResp.Error == "" {
		t.Error("error message should not be empty")
	}
}

func TestGateway_EnterWorld(t *testing.T) {
	gw := startTestGateway(t)
	defer gw.Shutdown()

	conn := dialGateway(t, gw)
	defer conn.Close()

	// First authenticate.
	token, _ := jwt.Sign("user1", testSecret, 1*time.Hour)
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn, authEnv)
	readEnvelope(t, conn) // consume auth response

	// Then enter world.
	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_forest"})
	sendEnvelope(t, conn, enterEnv)

	resp := readEnvelope(t, conn)
	if resp.Type != messages.MsgEnterWorldResp {
		t.Fatalf("expected MsgEnterWorldResp, got %d", resp.Type)
	}

	var ewResp messages.EnterWorldResponse
	if err := resp.UnmarshalPayload(&ewResp); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if ewResp.Error != "" {
		t.Errorf("unexpected error: %s", ewResp.Error)
	}
	if ewResp.ServerAddr != "10.0.0.1:9000" {
		t.Errorf("ServerAddr = %q, want %q", ewResp.ServerAddr, "10.0.0.1:9000")
	}
	if ewResp.JoinToken == "" {
		t.Error("JoinToken should not be empty")
	}
}

func TestGateway_EnterWorldBeforeAuth(t *testing.T) {
	gw := startTestGateway(t)
	defer gw.Shutdown()

	conn := dialGateway(t, gw)
	defer conn.Close()

	// Try to enter world without authenticating first.
	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_forest"})
	sendEnvelope(t, conn, enterEnv)

	resp := readEnvelope(t, conn)
	var ewResp messages.EnterWorldResponse
	resp.UnmarshalPayload(&ewResp)
	if ewResp.Error == "" {
		t.Error("should fail when not authenticated")
	}
}
