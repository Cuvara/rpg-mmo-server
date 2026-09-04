package server

import (
	"context"
	"encoding/json"
	"net"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// kickTestRig is a gateway wired the way main.go wires it for the kick path:
// a kick stream (memory implementation here) passed via WithKickStream, plus a
// capture subscription on the kick stream so tests can assert what was
// published without a real consumer.
type kickTestRig struct {
	gw       *Gateway
	sessions *session.SessionManager
	events   chan storage.Event
}

func startKickTestGateway(t *testing.T) *kickTestRig {
	t.Helper()

	sessionStore := storage.NewMemorySessionStore()
	reg := storage.NewMemoryServerRegistry()
	if err := reg.Register(context.Background(), storage.ServerInfo{
		ServerID: "srv1", MapID: "map_forest", Addr: "10.0.0.1:9000", Capacity: 100,
	}); err != nil {
		t.Fatalf("register server: %v", err)
	}

	stream := storage.NewMemoryEventStream()
	t.Cleanup(func() { _ = stream.Close() })
	events := make(chan storage.Event, 16)
	if err := stream.Subscribe(context.Background(), constants.KickEventStream, func(ev storage.Event) {
		events <- ev
	}); err != nil {
		t.Fatalf("subscribe kick stream: %v", err)
	}

	sessions := session.NewSessionManager(sessionStore, "gw-test")
	gw := New(sessions, registry.NewRegistryService(reg), testSecret, logger.New("error"),
		WithJoinTokenSecret(testSecret),
		WithKickStream(stream))
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
			return &kickTestRig{gw: gw, sessions: sessions, events: events}
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("gateway did not start in time")
	return nil
}

func (r *kickTestRig) expectEvent(t *testing.T) SessionSupersededEvent {
	t.Helper()
	select {
	case ev := <-r.events:
		if ev.Type != constants.EventSessionSuperseded {
			t.Fatalf("event type = %q, want %q", ev.Type, constants.EventSessionSuperseded)
		}
		var p SessionSupersededEvent
		if err := json.Unmarshal(ev.Payload, &p); err != nil {
			t.Fatalf("unmarshal supersede payload: %v (raw %s)", err, ev.Payload)
		}
		return p
	case <-time.After(2 * time.Second):
		t.Fatal("no supersede event published within 2s")
	}
	return SessionSupersededEvent{}
}

func (r *kickTestRig) expectNoEvent(t *testing.T) {
	t.Helper()
	select {
	case ev := <-r.events:
		t.Fatalf("unexpected event published: type=%q payload=%s", ev.Type, ev.Payload)
	case <-time.After(150 * time.Millisecond):
	}
}

// login dials, authenticates and (optionally) enters the world, returning the
// connection. enterWorld is what attaches ServerID + JoinTokenJTI to the
// session — the precondition for a supersede publish.
func (r *kickTestRig) login(t *testing.T, userID string, doEnterWorld bool) net.Conn {
	t.Helper()
	conn := dialGateway(t, r.gw)
	t.Cleanup(func() { conn.Close() })
	resp := authenticate(t, conn, userID)
	if !resp.OK {
		t.Fatalf("auth failed: %s", resp.Error)
	}
	if doEnterWorld {
		ew := enterWorld(t, conn, "map_forest")
		if ew.Error != "" {
			t.Fatalf("enter world failed: %s", ew.Error)
		}
	}
	return conn
}

// TestKickPublish_TableDriven pins when a duplicate login publishes a
// supersede event and when it must not.
func TestKickPublish_TableDriven(t *testing.T) {
	tests := []struct {
		name string
		run  func(t *testing.T, r *kickTestRig)
	}{
		{
			// The core case: the old login joined a game server (session has
			// server_id + jti), the user logs in again on a new socket.
			name: "publishes_after_old_login_entered_world",
			run: func(t *testing.T, r *kickTestRig) {
				r.login(t, "user-a", true)
				sd, err := r.sessions.GetSession(context.Background(), "user-a")
				if err != nil {
					t.Fatalf("GetSession: %v", err)
				}
				if sd.JoinTokenJTI == "" {
					t.Fatal("session has no JoinTokenJTI after EnterWorld — the discriminator the kick relies on is missing")
				}

				r.login(t, "user-a", false)
				ev := r.expectEvent(t)
				if ev.UserID != "user-a" {
					t.Errorf("user_id = %q, want user-a", ev.UserID)
				}
				if ev.ServerID != "srv1" {
					t.Errorf("server_id = %q, want srv1", ev.ServerID)
				}
				if ev.JTI != sd.JoinTokenJTI {
					t.Errorf("jti = %q, want the OLD session's %q", ev.JTI, sd.JoinTokenJTI)
				}
			},
		},
		{
			// No map assignment on the old session: there is no game-server
			// connection to kick and no jti to match, so publishing anything
			// would be noise at best and a mis-kick at worst.
			name: "no_publish_when_old_login_never_entered_world",
			run: func(t *testing.T, r *kickTestRig) {
				r.login(t, "user-b", false)
				r.login(t, "user-b", false)
				r.expectNoEvent(t)
			},
		},
		{
			// First login for a user: no existing session, nothing to supersede.
			name: "no_publish_on_first_login",
			run: func(t *testing.T, r *kickTestRig) {
				r.login(t, "user-c", true)
				r.expectNoEvent(t)
			},
		},
		{
			// Re-auth on the SAME socket is the same login refreshing itself,
			// mirroring TestGateway_ReauthSameConnNoDuplicate: kicking its own
			// game-server connection would disconnect the player it belongs to.
			name: "no_publish_on_reauth_same_conn",
			run: func(t *testing.T, r *kickTestRig) {
				conn := r.login(t, "user-d", true)
				resp := authenticate(t, conn, "user-d")
				if !resp.OK {
					t.Fatalf("re-auth failed: %s", resp.Error)
				}
				r.expectNoEvent(t)
			},
		},
		{
			// A session owned by ANOTHER gateway instance still publishes: the
			// stream is reachable from every replica even though the socket is
			// not. This is the cross-instance half #211 deleted-as-unwired.
			name: "publishes_for_session_owned_by_other_gateway",
			run: func(t *testing.T, r *kickTestRig) {
				r.login(t, "user-e", true)
				// Rewrite the session so it reads as owned by another replica.
				if err := r.sessions.UpdateSession(context.Background(), "user-e", func(sd *session.SessionData) {
					sd.GatewayID = "gw-other"
				}); err != nil {
					t.Fatalf("UpdateSession: %v", err)
				}
				r.login(t, "user-e", false)
				ev := r.expectEvent(t)
				if ev.OldGateway != "gw-other" || ev.NewGateway != "gw-test" {
					t.Errorf("gateways = %q -> %q, want gw-other -> gw-test", ev.OldGateway, ev.NewGateway)
				}
			},
		},
		{
			// Newest-wins across a chain: the SECOND duplicate must carry the
			// jti of the session state at that moment, i.e. whatever the store
			// says — never a stale value cached in the process.
			name: "publish_reads_jti_from_store_not_cache",
			run: func(t *testing.T, r *kickTestRig) {
				r.login(t, "user-f", true)
				if err := r.sessions.UpdateSession(context.Background(), "user-f", func(sd *session.SessionData) {
					sd.JoinTokenJTI = "rotated-jti"
				}); err != nil {
					t.Fatalf("UpdateSession: %v", err)
				}
				r.login(t, "user-f", false)
				if ev := r.expectEvent(t); ev.JTI != "rotated-jti" {
					t.Errorf("jti = %q, want rotated-jti", ev.JTI)
				}
			},
		},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			tc.run(t, startKickTestGateway(t))
		})
	}
}

// TestKickPublish_LocalKickStillHappens proves the new publish path did not
// replace the same-gateway socket eviction — both halves fire on one duplicate.
func TestKickPublish_LocalKickStillHappens(t *testing.T) {
	r := startKickTestGateway(t)

	conn1 := r.login(t, "user-both", true)
	r.login(t, "user-both", false)

	// The supersede event for the game-server half…
	if ev := r.expectEvent(t); ev.UserID != "user-both" {
		t.Errorf("user_id = %q, want user-both", ev.UserID)
	}
	// …and the local MsgKick for the gateway-socket half.
	conn1.SetReadDeadline(time.Now().Add(2 * time.Second))
	for {
		env, err := messages.Decode(conn1)
		if err != nil {
			t.Fatalf("old gateway socket never saw MsgKick: %v", err)
		}
		if env.Type == messages.MsgKick {
			return
		}
	}
}
