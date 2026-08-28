package server

import (
	"context"
	"errors"
	"net"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"
)

// failRefreshStore wraps a SessionStore and makes Refresh fail, standing in for
// a Redis that goes away between auth and the first heartbeat.
type failRefreshStore struct {
	storage.SessionStore
}

func (f *failRefreshStore) Refresh(_ context.Context, _ string, _ time.Duration) error {
	return errors.New("dial tcp 127.0.0.1:6379: connect: connection refused")
}

// sendPongAndSync sends a MsgPong and then completes a ping/pong round trip.
// The gateway serves a connection with a single read loop, so the answered ping
// proves the pong before it was already dispatched — without it the TTL
// assertions would race the handler.
func sendPongAndSync(t *testing.T, conn net.Conn) {
	t.Helper()

	pong, _ := messages.NewEnvelope(messages.MsgPong, messages.PongMessage{})
	sendEnvelope(t, conn, pong)

	ping, _ := messages.NewEnvelope(messages.MsgPing, messages.PingMessage{Timestamp: 7})
	sendEnvelope(t, conn, ping)

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
		return
	}
}

// TestGateway_PongRefreshesSessionTTL is the regression guard for #231: a
// client holding the gateway socket open sending only heartbeats must keep its
// session alive ("refreshed by activity", gameserver-dotnet/docs/API.md), an
// unauthenticated pong must touch nothing, and a store failure must fail open
// rather than kill the connection.
func TestGateway_PongRefreshesSessionTTL(t *testing.T) {
	tests := []struct {
		name        string
		auth        bool // authenticate the connection before ponging
		failRefresh bool // wrap the store so Refresh errors
		wantRefresh bool // TTL must be longer after the pong
	}{
		{name: "authenticated pong refreshes", auth: true, wantRefresh: true},
		{name: "unauthenticated pong does not refresh", auth: false, wantRefresh: false},
		{name: "store error fails open", auth: true, failRefresh: true, wantRefresh: false},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			// Redis-only: miniredis lets us assert the TTL was (not) re-armed.
			mr := miniredis.RunT(t)
			redis := redisstore.NewSessionStore(mr.Addr(), "")
			t.Cleanup(func() { redis.Close() })

			var store storage.SessionStore = redis
			if tt.failRefresh {
				store = &failRefreshStore{SessionStore: redis}
			}

			gw, mgr := startGatewayWith(t, backend{
				name:     "redis",
				sessions: store,
				registry: storage.NewMemoryServerRegistry(),
			})

			conn := dialGateway(t, gw)
			defer conn.Close()

			if tt.auth {
				if resp := authenticate(t, conn, "user1"); !resp.OK {
					t.Fatalf("auth failed: %s", resp.Error)
				}
			} else {
				// A session exists in the store, but this connection never
				// authenticated as its owner — its pong must not touch it.
				if _, err := mgr.CreateSession(context.Background(), "user1"); err != nil {
					t.Fatalf("create session: %v", err)
				}
			}

			key := session.SessionKey("user1")
			// Burn most of the TTL so a refresh is unambiguous.
			mr.FastForward(50 * time.Minute)
			before := mr.TTL(key)

			sendPongAndSync(t, conn)

			after := mr.TTL(key)
			if tt.wantRefresh && after <= before {
				t.Errorf("TTL not refreshed by pong: before=%v after=%v", before, after)
			}
			if !tt.wantRefresh && after > before {
				t.Errorf("TTL refreshed but should not have been: before=%v after=%v", before, after)
			}

			// The connection must survive in every case — a pong never draws an
			// error frame, and a store failure fails open (like checkSession).
			// The failRefresh case would also log a kill here if the handler
			// closed the socket: prove it still answers a real frame.
			if resp := enterWorld(t, conn, "map_forest"); tt.auth && resp.Error != "" && !tt.failRefresh {
				t.Errorf("enter world after pong failed: %s", resp.Error)
			}
		})
	}
}

// TestShouldRefreshSession_RateLimited pins the store-write bound: pongs inside
// sessionRefreshInterval must not trigger another refresh, and one after the
// interval must.
func TestShouldRefreshSession_RateLimited(t *testing.T) {
	cc := &ClientConn{}
	base := time.Now()

	if !cc.shouldRefreshSession(base) {
		t.Fatal("first pong must refresh")
	}
	if cc.shouldRefreshSession(base.Add(sessionRefreshInterval / 2)) {
		t.Error("pong inside the interval must not refresh again")
	}
	if !cc.shouldRefreshSession(base.Add(sessionRefreshInterval)) {
		t.Error("pong after the interval must refresh")
	}
}
