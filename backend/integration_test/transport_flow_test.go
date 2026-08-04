package integration

import (
	"context"
	"log/slog"
	"os"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/transport"

	"github.com/duycuong/rpg-mmo/gameserver/server"

	gwregistry "github.com/duycuong/rpg-mmo/gateway/registry"
	gwserver "github.com/duycuong/rpg-mmo/gateway/server"
	gwsession "github.com/duycuong/rpg-mmo/gateway/session"
)

// TestFullFlow_TransportMatrix runs the complete client -> gateway ->
// gameserver flow over every combination of per-hop transports.
//
// The mixed cases matter most: each hop is negotiated independently, and the
// client only learns the game server's transport from
// EnterWorldResponse.Transport, which the gateway copies out of the target
// server's registry entry.
func TestFullFlow_TransportMatrix(t *testing.T) {
	tests := []struct {
		name          string
		gwTransport   string
		gsTransport   string
		wantAnnounced string // expected EnterWorldResponse.Transport
	}{
		{
			name:          "kcp gateway and kcp gameserver",
			gwTransport:   transport.KindKCP,
			gsTransport:   transport.KindKCP,
			wantAnnounced: transport.KindKCP,
		},
		{
			name:          "mixed: tcp gateway, kcp gameserver",
			gwTransport:   transport.KindTCP,
			gsTransport:   transport.KindKCP,
			wantAnnounced: transport.KindKCP,
		},
		{
			name:          "mixed: kcp gateway, tcp gameserver",
			gwTransport:   transport.KindKCP,
			gsTransport:   transport.KindTCP,
			wantAnnounced: transport.KindTCP,
		},
		{
			name:          "tcp gateway and tcp gameserver (regression)",
			gwTransport:   transport.KindTCP,
			gsTransport:   transport.KindTCP,
			wantAnnounced: transport.KindTCP,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			runTransportFlow(t, tt.gwTransport, tt.gsTransport, tt.wantAnnounced)
		})
	}
}

func runTransportFlow(t *testing.T, gwTransport, gsTransport, wantAnnounced string) {
	t.Helper()

	playerStore := storage.NewMemoryPlayerStore()
	sessionStore := storage.NewMemorySessionStore()
	reg := storage.NewMemoryServerRegistry()
	eventStream := storage.NewMemoryEventStream()
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))

	cfg := config.Config{JWTSecret: testJWTSecret, TickRate: 10}

	// --- Game server on a random loopback port ---
	gs := server.New(server.ServerOpts{
		Config:      cfg,
		PlayerStore: playerStore,
		Registry:    reg,
		EventStream: eventStream,
		ServerID:    "gs-transport-" + gwTransport + "-" + gsTransport,
		MapID:       "map_01",
		Transport:   gsTransport,
		Capacity:    100,
		Logger:      logger,
	})
	go gs.Run("127.0.0.1:0")
	defer gs.Shutdown()

	gsAddr := waitAddr(t, "game server", gs.Addr)
	t.Logf("game server listening on %s (%s)", gsAddr, gsTransport)

	// The registry entry must carry the transport — this is what the gateway
	// forwards to the client.
	servers, err := reg.FindByMapID(context.Background(), "map_01")
	if err != nil {
		t.Fatalf("FindByMapID: %v", err)
	}
	if len(servers) != 1 {
		t.Fatalf("registry has %d servers, want 1", len(servers))
	}
	if got := transport.Normalize(servers[0].Transport); got != gsTransport {
		t.Fatalf("registry transport = %q, want %q", got, gsTransport)
	}

	// --- Gateway on a random loopback port, sharing the registry ---
	gw := gwserver.New(
		gwsession.NewSessionManager(sessionStore),
		gwregistry.NewRegistryService(reg),
		testJWTSecret,
		logger,
		gwserver.WithTransport(gwTransport),
	)
	go gw.Run("127.0.0.1:0")
	defer gw.Shutdown()

	gwAddr := waitAddr(t, "gateway", gw.Addr)
	t.Logf("gateway listening on %s (%s)", gwAddr, gwTransport)

	// --- Hop 1: client -> gateway over gwTransport ---
	gwClient, err := NewMockClientTransport(gwTransport, gwAddr)
	if err != nil {
		t.Fatalf("dial gateway over %s: %v", gwTransport, err)
	}

	userID := "player-" + gwTransport + "-" + gsTransport
	token, err := jwt.Sign(userID, testJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}

	var authResp messages.AuthResponse
	if err := clientRoundTrip(gwClient, messages.MsgAuth, messages.AuthRequest{Token: token},
		messages.MsgAuthResp, &authResp); err != nil {
		t.Fatalf("gateway auth: %v", err)
	}
	if !authResp.OK || authResp.UserID != userID {
		t.Fatalf("auth failed: ok=%v user=%q err=%q", authResp.OK, authResp.UserID, authResp.Error)
	}

	var enterResp messages.EnterWorldResponse
	if err := clientRoundTrip(gwClient, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_01"},
		messages.MsgEnterWorldResp, &enterResp); err != nil {
		t.Fatalf("enter world: %v", err)
	}
	if enterResp.Error != "" {
		t.Fatalf("enter world rejected: %s", enterResp.Error)
	}
	if enterResp.ServerAddr == "" || enterResp.JoinToken == "" {
		t.Fatalf("enter world missing addr/token: %+v", enterResp)
	}
	// The announced transport is what the client must dial with.
	if got := transport.Normalize(enterResp.Transport); got != wantAnnounced {
		t.Fatalf("EnterWorldResponse.Transport = %q (raw %q), want %q",
			got, enterResp.Transport, wantAnnounced)
	}
	gwClient.Close()
	t.Logf("gateway announced transport=%q server=%s", enterResp.Transport, enterResp.ServerAddr)

	// --- Hop 2: client -> game server, dialing what the gateway announced ---
	gsClient, err := NewMockClientTransport(enterResp.Transport, enterResp.ServerAddr)
	if err != nil {
		t.Fatalf("dial game server over %q: %v", enterResp.Transport, err)
	}
	defer gsClient.Close()

	var joinResp messages.JoinTokenResponse
	if err := clientRoundTrip(gsClient, messages.MsgJoinToken, messages.JoinTokenRequest{Token: enterResp.JoinToken},
		messages.MsgJoinTokenResp, &joinResp); err != nil {
		t.Fatalf("join: %v", err)
	}
	if !joinResp.OK || joinResp.UserID != userID {
		t.Fatalf("join rejected: ok=%v user=%q err=%q", joinResp.OK, joinResp.UserID, joinResp.Error)
	}

	// --- Gameplay: inputs up, snapshots down ---
	for i := 0; i < 3; i++ {
		inputEnv, err := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
			Tick: uint64(i + 1), MoveX: 1.0,
		})
		if err != nil {
			t.Fatalf("NewEnvelope(MsgInput): %v", err)
		}
		if err := gsClient.Send(inputEnv); err != nil {
			t.Fatalf("send input %d: %v", i, err)
		}
		time.Sleep(120 * time.Millisecond)
	}

	var (
		snapshots int
		lastX     float32
		seen      bool
	)
	for i := 0; i < 20 && !(seen && snapshots >= 2 && lastX >= 1.0); i++ {
		env, err := gsClient.Receive()
		if err != nil {
			break
		}
		if env.Type != messages.MsgSnapshot {
			continue
		}
		var snap messages.SnapshotMessage
		if err := messages.UnmarshalPayload(env.Payload, &snap); err != nil {
			t.Fatalf("unmarshal snapshot: %v", err)
		}
		snapshots++
		for _, e := range snap.Entities {
			if e.ID == userID {
				seen, lastX = true, e.X
			}
		}
	}
	if snapshots == 0 {
		t.Fatal("no snapshot received")
	}
	if !seen {
		t.Fatalf("player %s never appeared in %d snapshots", userID, snapshots)
	}
	if lastX < 1.0 {
		t.Errorf("player X = %.2f after 3 move inputs, want >= 1.0", lastX)
	}
	t.Logf("gameplay OK over %s/%s: snapshots=%d final_x=%.2f",
		gwTransport, gsTransport, snapshots, lastX)

	// --- Disconnect persists state, same as on TCP ---
	//
	// The explicit MsgDisconnect matters on KCP: UDP has no FIN, so a client
	// that just drops the socket is only noticed when the reconnect hold
	// expires. A well-behaved client always sends MsgDisconnect first.
	if env, err := messages.NewEnvelope(messages.MsgDisconnect, struct{}{}); err == nil {
		_ = gsClient.Send(env)
	}
	// KCP flushes on its 10ms update tick and Close() does not drain pending
	// output, so give the frame a moment to leave before tearing the session down.
	time.Sleep(200 * time.Millisecond)
	gsClient.Close()
	time.Sleep(500 * time.Millisecond)

	state, err := playerStore.LoadPlayer(context.Background(), userID)
	if err != nil {
		t.Fatalf("LoadPlayer: %v", err)
	}
	if state.X < 1.0 {
		t.Errorf("persisted X = %.2f, want >= 1.0", state.X)
	}
}

// clientRoundTrip sends one envelope and waits for a reply of wantType,
// skipping interleaved frames (e.g. an early snapshot).
func clientRoundTrip(c *MockClient, reqType messages.MsgType, reqPayload any,
	wantType messages.MsgType, out any) error {
	env, err := messages.NewEnvelope(reqType, reqPayload)
	if err != nil {
		return err
	}
	if err := c.Send(env); err != nil {
		return err
	}
	for i := 0; i < 16; i++ {
		resp, err := c.Receive()
		if err != nil {
			return err
		}
		if resp.Type != wantType {
			continue
		}
		return messages.UnmarshalPayload(resp.Payload, out)
	}
	return errNoFrame
}

var errNoFrame = errNoFrameType{}

type errNoFrameType struct{}

func (errNoFrameType) Error() string { return "no frame of the expected type received" }

// TestEnterWorld_LegacyRegistryEntryIsTCP proves backward compatibility: a
// registry entry written before the transport field existed (empty string)
// makes the gateway announce an empty transport, which every client — old or
// new — reads as TCP.
func TestEnterWorld_LegacyRegistryEntryIsTCP(t *testing.T) {
	sessionStore := storage.NewMemorySessionStore()
	reg := storage.NewMemoryServerRegistry()
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelError}))

	// Legacy entry: no Transport field at all.
	if err := reg.Register(context.Background(), storage.ServerInfo{
		ServerID: "gs-legacy", MapID: "map_legacy", Addr: "127.0.0.1:9999", Capacity: 10,
	}); err != nil {
		t.Fatalf("Register: %v", err)
	}

	gw := gwserver.New(
		gwsession.NewSessionManager(sessionStore),
		gwregistry.NewRegistryService(reg),
		testJWTSecret,
		logger,
	)
	go gw.Run("127.0.0.1:0")
	defer gw.Shutdown()
	gwAddr := waitAddr(t, "gateway", gw.Addr)

	client, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("dial gateway: %v", err)
	}
	defer client.Close()

	token, err := jwt.Sign("legacy-player", testJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}
	var authResp messages.AuthResponse
	if err := clientRoundTrip(client, messages.MsgAuth, messages.AuthRequest{Token: token},
		messages.MsgAuthResp, &authResp); err != nil {
		t.Fatalf("auth: %v", err)
	}
	if !authResp.OK {
		t.Fatalf("auth rejected: %s", authResp.Error)
	}

	var enterResp messages.EnterWorldResponse
	if err := clientRoundTrip(client, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_legacy"},
		messages.MsgEnterWorldResp, &enterResp); err != nil {
		t.Fatalf("enter world: %v", err)
	}
	if enterResp.Error != "" {
		t.Fatalf("enter world rejected: %s", enterResp.Error)
	}
	if enterResp.Transport != "" {
		t.Errorf("Transport = %q, want the field omitted for a legacy entry", enterResp.Transport)
	}
	if got := transport.Normalize(enterResp.Transport); got != transport.KindTCP {
		t.Errorf("Normalize(%q) = %q, want tcp", enterResp.Transport, got)
	}
}
