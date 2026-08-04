package server

import (
	"context"
	"net"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"
)

// backend builds a matching (SessionStore, ServerRegistry) pair so every
// lifecycle test runs identically against memory and Redis.
type backend struct {
	name     string
	sessions storage.SessionStore
	registry storage.ServerRegistry
}

func memoryBackend(t *testing.T) backend {
	t.Helper()
	return backend{
		name:     "memory",
		sessions: storage.NewMemorySessionStore(),
		registry: storage.NewMemoryServerRegistry(),
	}
}

func redisBackend(t *testing.T) backend {
	t.Helper()
	mr := miniredis.RunT(t)
	sess := redisstore.NewSessionStore(mr.Addr(), "")
	reg := redisstore.NewServerRegistry(mr.Addr(), "")
	t.Cleanup(func() {
		sess.Close()
		reg.Close()
	})
	return backend{name: "redis", sessions: sess, registry: reg}
}

func allBackends(t *testing.T) []backend {
	t.Helper()
	return []backend{memoryBackend(t), redisBackend(t)}
}

// startGatewayWith starts a gateway on a random port using the given stores.
func startGatewayWith(t *testing.T, b backend) (*Gateway, *session.SessionManager) {
	t.Helper()

	if err := b.registry.Register(context.Background(), storage.ServerInfo{
		ServerID:    "srv1",
		MapID:       "map_forest",
		Addr:        "10.0.0.1:9000",
		Capacity:    100,
		PlayerCount: 10,
	}); err != nil {
		t.Fatalf("register server: %v", err)
	}

	sessions := session.NewSessionManager(b.sessions)
	gw := New(sessions, registry.NewRegistryService(b.registry), testSecret, logger.New("error"))

	go func() {
		if err := gw.Run("127.0.0.1:0"); err != nil {
			select {
			case <-gw.done:
			default:
				t.Logf("gateway run error: %v", err)
			}
		}
	}()
	for i := 0; i < 100; i++ {
		if gw.Addr() != "" {
			t.Cleanup(gw.Shutdown)
			return gw, sessions
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("gateway did not start in time")
	return nil, nil
}

func authenticate(t *testing.T, conn net.Conn, userID string) messages.AuthResponse {
	t.Helper()
	token, err := jwt.Sign(userID, testSecret, time.Hour)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	env, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn, env)

	resp := readEnvelope(t, conn)
	var authResp messages.AuthResponse
	if err := messages.UnmarshalPayload(resp.Payload, &authResp); err != nil {
		t.Fatalf("unmarshal auth resp: %v", err)
	}
	return authResp
}

func enterWorld(t *testing.T, conn net.Conn, mapID string) messages.EnterWorldResponse {
	t.Helper()
	env, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: mapID})
	sendEnvelope(t, conn, env)

	resp := readEnvelope(t, conn)
	var ewResp messages.EnterWorldResponse
	if err := messages.UnmarshalPayload(resp.Payload, &ewResp); err != nil {
		t.Fatalf("unmarshal enter world resp: %v", err)
	}
	return ewResp
}

// sessionExists reports whether the store still holds the user's session.
func sessionExists(t *testing.T, mgr *session.SessionManager, userID string) bool {
	t.Helper()
	_, err := mgr.ValidateSession(context.Background(), session.SessionKey(userID))
	return err == nil
}

func TestGateway_SessionCreatedOnAuth(t *testing.T) {
	for _, b := range allBackends(t) {
		t.Run(b.name, func(t *testing.T) {
			gw, mgr := startGatewayWith(t, b)

			conn := dialGateway(t, gw)
			defer conn.Close()

			if resp := authenticate(t, conn, "user1"); !resp.OK {
				t.Fatalf("auth failed: %s", resp.Error)
			}
			if !sessionExists(t, mgr, "user1") {
				t.Error("session should exist after auth")
			}
		})
	}
}

func TestGateway_SessionValidatedOnEnterWorld(t *testing.T) {
	tests := []struct {
		name        string
		dropSession bool
		wantErr     string
	}{
		{name: "live session", dropSession: false, wantErr: ""},
		{name: "expired session", dropSession: true, wantErr: "session expired"},
	}

	for _, b := range allBackends(t) {
		for _, tt := range tests {
			t.Run(b.name+"/"+tt.name, func(t *testing.T) {
				gw, mgr := startGatewayWith(t, b)

				conn := dialGateway(t, gw)
				defer conn.Close()

				if resp := authenticate(t, conn, "user1"); !resp.OK {
					t.Fatalf("auth failed: %s", resp.Error)
				}
				if tt.dropSession {
					// Simulate TTL expiry / eviction from another instance.
					if err := mgr.DestroySession(context.Background(), session.SessionKey("user1")); err != nil {
						t.Fatalf("destroy session: %v", err)
					}
				}

				resp := enterWorld(t, conn, "map_forest")
				if tt.wantErr == "" {
					if resp.Error != "" {
						t.Fatalf("unexpected error: %s", resp.Error)
					}
					if resp.ServerAddr != "10.0.0.1:9000" || resp.JoinToken == "" {
						t.Errorf("bad assignment: %+v", resp)
					}
					return
				}
				if resp.Error != tt.wantErr {
					t.Errorf("Error = %q, want %q", resp.Error, tt.wantErr)
				}
			})
		}
	}
}

func TestGateway_SessionTTLRefreshedOnActivity(t *testing.T) {
	// Redis-only: miniredis lets us assert the TTL was re-armed.
	mr := miniredis.RunT(t)
	store := redisstore.NewSessionStore(mr.Addr(), "")
	t.Cleanup(func() { store.Close() })

	gw, _ := startGatewayWith(t, backend{
		name:     "redis",
		sessions: store,
		registry: storage.NewMemoryServerRegistry(),
	})

	conn := dialGateway(t, gw)
	defer conn.Close()

	if resp := authenticate(t, conn, "user1"); !resp.OK {
		t.Fatalf("auth failed: %s", resp.Error)
	}

	key := session.SessionKey("user1")
	// Burn most of the TTL, then send a message: the gateway must re-arm it.
	mr.FastForward(50 * time.Minute)
	before := mr.TTL(key)

	if resp := enterWorld(t, conn, "map_forest"); resp.Error == "" {
		t.Log("enter world succeeded")
	}

	after := mr.TTL(key)
	if after <= before {
		t.Errorf("TTL not refreshed: before=%v after=%v", before, after)
	}
}

func TestGateway_DisconnectDestroysSession(t *testing.T) {
	for _, b := range allBackends(t) {
		t.Run(b.name, func(t *testing.T) {
			gw, mgr := startGatewayWith(t, b)

			conn := dialGateway(t, gw)
			defer conn.Close()

			if resp := authenticate(t, conn, "user1"); !resp.OK {
				t.Fatalf("auth failed: %s", resp.Error)
			}

			env, _ := messages.NewEnvelope(messages.MsgDisconnect, struct{}{})
			sendEnvelope(t, conn, env)

			if !eventuallyFalse(func() bool { return sessionExists(t, mgr, "user1") }) {
				t.Error("session should be destroyed after MsgDisconnect")
			}
		})
	}
}

func TestGateway_SocketCloseDestroysSession(t *testing.T) {
	for _, b := range allBackends(t) {
		t.Run(b.name, func(t *testing.T) {
			gw, mgr := startGatewayWith(t, b)

			conn := dialGateway(t, gw)
			if resp := authenticate(t, conn, "user2"); !resp.OK {
				t.Fatalf("auth failed: %s", resp.Error)
			}
			conn.Close()

			if !eventuallyFalse(func() bool { return sessionExists(t, mgr, "user2") }) {
				t.Error("session should be destroyed when the socket drops")
			}
		})
	}
}

// eventuallyFalse polls cond until it returns false or the deadline elapses.
func eventuallyFalse(cond func() bool) bool {
	for i := 0; i < 100; i++ {
		if !cond() {
			return true
		}
		time.Sleep(10 * time.Millisecond)
	}
	return false
}
