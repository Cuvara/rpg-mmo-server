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

type kickTestRig struct {
	gw            *Gateway
	sessions      *session.SessionManager
	events        chan storage.Event
	gatewayEvents chan storage.Event
}

func startKickTestGateway(t *testing.T) *kickTestRig {
	t.Helper()
	sessionStore := storage.NewMemorySessionStore()
	reg := storage.NewMemoryServerRegistry()
	if err := reg.Register(context.Background(), storage.ServerInfo{
		ServerID: "srv1", MapID: "map_forest", Addr: "10.0.0.1:9000", Capacity: 100,
	}); err != nil { t.Fatalf("register: %v", err) }
	stream := storage.NewMemoryEventStream()
	t.Cleanup(func() { _ = stream.Close() })
	events := make(chan storage.Event, 16)
	_ = stream.Subscribe(context.Background(), constants.KickEventStream, func(ev storage.Event) { events <- ev })
	gatewayEvents := make(chan storage.Event, 16)
	_ = stream.Subscribe(context.Background(), constants.GatewayKickStream, func(ev storage.Event) { gatewayEvents <- ev })
	sessions := session.NewSessionManager(sessionStore, "gw-test")
	gw := New(sessions, registry.NewRegistryService(reg), testSecret, logger.New("error"),
		WithJoinTokenSecret(testSecret), WithKickStream(stream))
	go func() { _ = gw.Run("127.0.0.1:0") }()
	for i := 0; i < 100; i++ {
		if gw.Addr() != "" {
			t.Cleanup(gw.Shutdown)
			return &kickTestRig{gw: gw, sessions: sessions, events: events, gatewayEvents: gatewayEvents}
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("gateway did not start"); return nil
}

func (r *kickTestRig) expectEvent(t *testing.T) SessionSupersededEvent {
	t.Helper()
	select {
	case ev := <-r.events:
		var p SessionSupersededEvent; _ = json.Unmarshal(ev.Payload, &p); return p
	case <-time.After(2 * time.Second): t.Fatal("no event within 2s")
	}; return SessionSupersededEvent{}
}
func (r *kickTestRig) expectNoEvent(t *testing.T) {
	t.Helper()
	select { case ev := <-r.events: t.Fatalf("unexpected: %q", ev.Type); case <-time.After(150*time.Millisecond): }
}
func (r *kickTestRig) expectGatewayKickEvent(t *testing.T) SessionSupersededEvent {
	t.Helper()
	select {
	case ev := <-r.gatewayEvents:
		var p SessionSupersededEvent; _ = json.Unmarshal(ev.Payload, &p); return p
	case <-time.After(2 * time.Second): t.Fatal("no gateway kick within 2s")
	}; return SessionSupersededEvent{}
}
func (r *kickTestRig) expectNoGatewayKickEvent(t *testing.T) {
	t.Helper()
	select { case ev := <-r.gatewayEvents: t.Fatalf("unexpected gw kick: %q", ev.Type); case <-time.After(150*time.Millisecond): }
}
func (r *kickTestRig) login(t *testing.T, userID string, doEnterWorld bool) net.Conn {
	t.Helper()
	conn := dialGateway(t, r.gw); t.Cleanup(func() { conn.Close() })
	resp := authenticate(t, conn, userID)
	if !resp.OK { t.Fatalf("auth: %s", resp.Error) }
	if doEnterWorld { ew := enterWorld(t, conn, "map_forest"); if ew.Error != "" { t.Fatalf("ew: %s", ew.Error) } }
	return conn
}

func TestKickPublish_TableDriven(t *testing.T) {
	tests := []struct { name string; run func(*testing.T, *kickTestRig) }{
		{"publishes_after_old_login_entered_world", func(t *testing.T, r *kickTestRig) {
			r.login(t, "a", true); sd, _ := r.sessions.GetSession(context.Background(), "a")
			r.login(t, "a", false); ev := r.expectEvent(t)
			if ev.UserID != "a" { t.Errorf("uid=%q", ev.UserID) }
			if ev.JTI != sd.JoinTokenJTI { t.Errorf("jti=%q want %q", ev.JTI, sd.JoinTokenJTI) }
		}},
		{"no_publish_when_never_entered_world", func(t *testing.T, r *kickTestRig) {
			r.login(t, "b", false); r.login(t, "b", false); r.expectNoEvent(t)
		}},
		{"no_publish_on_first_login", func(t *testing.T, r *kickTestRig) {
			r.login(t, "c", true); r.expectNoEvent(t)
		}},
		{"no_publish_on_reauth_same_conn", func(t *testing.T, r *kickTestRig) {
			conn := r.login(t, "d", true); authenticate(t, conn, "d"); r.expectNoEvent(t)
		}},
		{"publishes_for_other_gateway", func(t *testing.T, r *kickTestRig) {
			r.login(t, "e", true)
			r.sessions.UpdateSession(context.Background(), "e", func(sd *session.SessionData) { sd.GatewayID = "gw-other" })
			r.login(t, "e", false); ev := r.expectEvent(t)
			if ev.OldGateway != "gw-other" { t.Errorf("old=%q", ev.OldGateway) }
		}},
		{"gateway_kick_for_cross_gateway", func(t *testing.T, r *kickTestRig) {
			r.login(t, "gw1", true)
			r.sessions.UpdateSession(context.Background(), "gw1", func(sd *session.SessionData) { sd.GatewayID = "gw-remote" })
			r.login(t, "gw1", false); r.expectEvent(t)
			gwEv := r.expectGatewayKickEvent(t)
			if gwEv.UserID != "gw1" { t.Errorf("uid=%q", gwEv.UserID) }
			if gwEv.OldGateway != "gw-remote" { t.Errorf("old=%q", gwEv.OldGateway) }
			if gwEv.NewGateway != "gw-test" { t.Errorf("new=%q", gwEv.NewGateway) }
		}},
		{"no_gateway_kick_for_same_gateway", func(t *testing.T, r *kickTestRig) {
			r.login(t, "sg", true); r.login(t, "sg", false)
			r.expectEvent(t); r.expectNoGatewayKickEvent(t)
		}},
		{"reads_jti_from_store", func(t *testing.T, r *kickTestRig) {
			r.login(t, "f", true)
			r.sessions.UpdateSession(context.Background(), "f", func(sd *session.SessionData) { sd.JoinTokenJTI = "rotated" })
			r.login(t, "f", false)
			if ev := r.expectEvent(t); ev.JTI != "rotated" { t.Errorf("jti=%q", ev.JTI) }
		}},
	}
	for _, tc := range tests { t.Run(tc.name, func(t *testing.T) { tc.run(t, startKickTestGateway(t)) }) }
}

func TestKickPublish_LocalKickStillHappens(t *testing.T) {
	r := startKickTestGateway(t); conn1 := r.login(t, "both", true)
	r.login(t, "both", false); r.expectEvent(t)
	conn1.SetReadDeadline(time.Now().Add(2 * time.Second))
	for { env, err := messages.Decode(conn1); if err != nil { t.Fatalf("no kick: %v", err) }; if env.Type == messages.MsgKick { return } }
}
