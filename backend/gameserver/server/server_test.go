package server

import (
	"encoding/json"
	"io"
	"log/slog"
	"net"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/gameserver/events"
	"github.com/duycuong/rpg-mmo/gameserver/game"
)

const testSecret = "test-secret"

type testHarness struct {
	srv      *Server
	addr     string
	registry storage.ServerRegistry
	stream   storage.EventStream
}

// startServer boots a game server on a random loopback port.
func startServer(t *testing.T, opts ServerOpts) *testHarness {
	t.Helper()

	if opts.Logger == nil {
		opts.Logger = slog.New(slog.NewTextHandler(io.Discard, nil))
	}
	if opts.Config.TickRate == 0 {
		opts.Config = config.Config{TickRate: 20, JWTSecret: testSecret}
	}
	if opts.PlayerStore == nil {
		opts.PlayerStore = storage.NewMemoryPlayerStore()
	}
	if opts.Registry == nil {
		opts.Registry = storage.NewMemoryServerRegistry()
	}
	if opts.EventStream == nil {
		opts.EventStream = storage.NewMemoryEventStream()
	}
	if opts.ServerID == "" {
		opts.ServerID = "gs-test-a"
	}
	if opts.MapID == "" {
		opts.MapID = "map_01"
	}
	if opts.Capacity == 0 {
		opts.Capacity = 10
	}

	srv := New(opts)
	go srv.Run("127.0.0.1:0")
	t.Cleanup(srv.Shutdown)

	var addr string
	for i := 0; i < 200; i++ {
		if addr = srv.Addr(); addr != "" {
			break
		}
		time.Sleep(5 * time.Millisecond)
	}
	if addr == "" {
		t.Fatal("server did not start listening")
	}
	return &testHarness{srv: srv, addr: addr, registry: opts.Registry, stream: opts.EventStream}
}

// join performs the MsgJoinToken handshake and returns the open connection.
func join(t *testing.T, addr, userID, serverID string) (net.Conn, messages.JoinTokenResponse) {
	t.Helper()

	token, err := jwt.SignWithServer(userID, serverID, testSecret, time.Minute)
	if err != nil {
		t.Fatalf("SignWithServer: %v", err)
	}
	conn, err := net.Dial("tcp", addr)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	env, err := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
	if err != nil {
		t.Fatalf("NewEnvelope: %v", err)
	}
	data, err := messages.Encode(env)
	if err != nil {
		t.Fatalf("Encode: %v", err)
	}
	if _, err := conn.Write(data); err != nil {
		t.Fatalf("write: %v", err)
	}

	conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	respEnv, err := messages.Decode(conn)
	if err != nil {
		conn.Close()
		t.Fatalf("decode join resp: %v", err)
	}
	conn.SetReadDeadline(time.Time{})

	var resp messages.JoinTokenResponse
	if err := messages.UnmarshalPayload(respEnv.Payload, &resp); err != nil {
		conn.Close()
		t.Fatalf("unmarshal join resp: %v", err)
	}
	return conn, resp
}

// waitFor polls cond until it holds or the deadline passes.
func waitFor(t *testing.T, timeout time.Duration, desc string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("timeout waiting for %s", desc)
}

// --- 1. Join token sid enforcement ---

func TestJoinToken_ServerIDClaim(t *testing.T) {
	h := startServer(t, ServerOpts{ServerID: "gs-a"})

	tests := []struct {
		name     string
		tokenSID string
		wantOK   bool
	}{
		{name: "matching sid accepted", tokenSID: "gs-a", wantOK: true},
		{name: "foreign sid rejected", tokenSID: "gs-b", wantOK: false},
		{name: "empty sid accepted (legacy)", tokenSID: "", wantOK: true},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			conn, resp := join(t, h.addr, "user-"+tc.name, tc.tokenSID)
			defer conn.Close()
			if resp.OK != tc.wantOK {
				t.Fatalf("join OK = %v (err=%q), want %v", resp.OK, resp.Error, tc.wantOK)
			}
		})
	}
}

// --- 2. Registry self-registration / heartbeat / player count ---

func TestRegistry_SelfRegistrationAndHeartbeat(t *testing.T) {
	reg := storage.NewMemoryServerRegistry()
	h := startServer(t, ServerOpts{
		ServerID:          "gs-reg",
		MapID:             "map_07",
		Capacity:          42,
		Registry:          reg,
		HeartbeatInterval: 20 * time.Millisecond,
	})

	info, err := reg.GetServer(t.Context(), "gs-reg")
	if err != nil {
		t.Fatalf("GetServer after start: %v", err)
	}
	if info.MapID != "map_07" || info.Capacity != 42 || info.Addr != h.addr {
		t.Fatalf("registered info = %+v, want map_07/42/%s", info, h.addr)
	}

	// Player count follows connections.
	conn, resp := join(t, h.addr, "u1", "gs-reg")
	if !resp.OK {
		t.Fatalf("join failed: %s", resp.Error)
	}
	waitFor(t, 2*time.Second, "player_count=1", func() bool {
		i, err := reg.GetServer(t.Context(), "gs-reg")
		return err == nil && i.PlayerCount == 1
	})

	conn.Close()
	waitFor(t, 2*time.Second, "player_count=0", func() bool {
		i, err := reg.GetServer(t.Context(), "gs-reg")
		return err == nil && i.PlayerCount == 0
	})

	// Heartbeat keeps running (no error path observable on memory registry, so
	// assert the entry stays alive across several intervals).
	time.Sleep(100 * time.Millisecond)
	if _, err := reg.GetServer(t.Context(), "gs-reg"); err != nil {
		t.Fatalf("server entry vanished despite heartbeats: %v", err)
	}

	// Deregister on graceful shutdown.
	h.srv.Shutdown()
	waitFor(t, 2*time.Second, "deregistration", func() bool {
		_, err := reg.GetServer(t.Context(), "gs-reg")
		return err != nil
	})
}

// --- 3. Reconnect hold window ---

func TestReconnect_WithinHoldWindow_PreservesState(t *testing.T) {
	h := startServer(t, ServerOpts{ServerID: "gs-hold", HoldTTL: 2 * time.Second})

	conn, resp := join(t, h.addr, "u-hold", "gs-hold")
	if !resp.OK {
		t.Fatalf("join failed: %s", resp.Error)
	}

	// Mutate the live entity so we can tell a reattach from a respawn.
	waitFor(t, 2*time.Second, "entity spawn", func() bool {
		return h.srv.World().GetEntity("u-hold") != nil
	})
	e := h.srv.World().GetEntity("u-hold")
	e.X, e.Y, e.HP = 12.5, -3.25, 47

	conn.Close()

	// Entity must survive the disconnect and be marked as held.
	waitFor(t, 2*time.Second, "hold registered", func() bool { return h.srv.HeldCount() == 1 })
	if h.srv.World().GetEntity("u-hold") == nil {
		t.Fatal("entity removed immediately on disconnect, want hold")
	}

	// Reconnect inside the window.
	conn2, resp2 := join(t, h.addr, "u-hold", "gs-hold")
	defer conn2.Close()
	if !resp2.OK {
		t.Fatalf("rejoin failed: %s", resp2.Error)
	}
	waitFor(t, 2*time.Second, "hold cancelled", func() bool { return h.srv.HeldCount() == 0 })

	got := h.srv.World().GetEntity("u-hold")
	if got == nil {
		t.Fatal("entity missing after reconnect")
	}
	if got.X != 12.5 || got.Y != -3.25 || got.HP != 47 {
		t.Errorf("after reconnect entity = (%.2f,%.2f,hp=%d), want (12.50,-3.25,hp=47)", got.X, got.Y, got.HP)
	}
}

func TestReconnect_AfterHoldExpiry_EntityRemoved(t *testing.T) {
	store := storage.NewMemoryPlayerStore()
	h := startServer(t, ServerOpts{
		ServerID:    "gs-exp",
		PlayerStore: store,
		HoldTTL:     80 * time.Millisecond,
	})

	conn, resp := join(t, h.addr, "u-exp", "gs-exp")
	if !resp.OK {
		t.Fatalf("join failed: %s", resp.Error)
	}
	waitFor(t, 2*time.Second, "entity spawn", func() bool {
		return h.srv.World().GetEntity("u-exp") != nil
	})
	e := h.srv.World().GetEntity("u-exp")
	e.X, e.HP = 8, 33

	conn.Close()

	waitFor(t, 3*time.Second, "hold expiry", func() bool {
		return h.srv.World().GetEntity("u-exp") == nil
	})
	if h.srv.HeldCount() != 0 {
		t.Errorf("HeldCount() = %d after expiry, want 0", h.srv.HeldCount())
	}

	// Final save happened before removal.
	state, err := store.LoadPlayer(t.Context(), "u-exp")
	if err != nil {
		t.Fatalf("LoadPlayer after expiry: %v", err)
	}
	if state.X != 8 || state.HP != 33 {
		t.Errorf("final save = (x=%.1f, hp=%d), want (8.0, 33)", state.X, state.HP)
	}
}

func TestHoldTTL_DungeonMode(t *testing.T) {
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	tests := []struct {
		mode string
		want time.Duration
	}{
		{mode: "map", want: 30 * time.Second},
		{mode: ModeDungeon, want: 60 * time.Second},
	}
	for _, tc := range tests {
		t.Run(tc.mode, func(t *testing.T) {
			s := New(ServerOpts{Mode: tc.mode, Logger: logger})
			if s.holdTTL != tc.want {
				t.Errorf("mode %q holdTTL = %v, want %v", tc.mode, s.holdTTL, tc.want)
			}
		})
	}
}

// --- 4. Death event publishing ---

func TestDeathEvent_PublishedOnPlayerDeath(t *testing.T) {
	stream := storage.NewMemoryEventStream()
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))

	received := make(chan storage.Event, 4)
	if err := stream.Subscribe(t.Context(), events.GameStream, func(e storage.Event) {
		received <- e
	}); err != nil {
		t.Fatalf("Subscribe: %v", err)
	}

	srv := New(ServerOpts{
		Config:      config.Config{TickRate: 20, JWTSecret: testSecret},
		PlayerStore: storage.NewMemoryPlayerStore(),
		Registry:    storage.NewMemoryServerRegistry(),
		EventStream: stream,
		ServerID:    "gs-evt",
		MapID:       "map_evt",
		Logger:      logger,
	})

	victim := &game.Entity{ID: "victim", Type: "player", HP: 0, MaxHP: 100}
	killer := &game.Entity{ID: "killer", Type: "player", HP: 100, MaxHP: 100}
	srv.onEntityDeath(victim, killer)

	select {
	case ev := <-received:
		if ev.Type != events.TypePlayerDeath {
			t.Fatalf("event type = %q, want %q", ev.Type, events.TypePlayerDeath)
		}
		var p events.DeathPayload
		if err := json.Unmarshal(ev.Payload, &p); err != nil {
			t.Fatalf("unmarshal payload: %v", err)
		}
		if p.VictimID != "victim" || p.KillerID != "killer" || p.MapID != "map_evt" || p.ServerID != "gs-evt" {
			t.Errorf("payload = %+v, want victim/killer/map_evt/gs-evt", p)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("no player_death event published")
	}

	// Boss kill maps to boss_killed; mob deaths publish nothing.
	srv.onEntityDeath(&game.Entity{ID: "boss1", Type: "boss"}, killer)
	select {
	case ev := <-received:
		if ev.Type != events.TypeBossKilled {
			t.Fatalf("event type = %q, want %q", ev.Type, events.TypeBossKilled)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("no boss_killed event published")
	}

	srv.onEntityDeath(&game.Entity{ID: "mob1", Type: "mob"}, killer)
	select {
	case ev := <-received:
		t.Fatalf("unexpected event for mob death: %+v", ev)
	case <-time.After(150 * time.Millisecond):
	}
}

// --- 5. Death event flows through the tick loop ---

func TestDeathEvent_ViaTickLoop(t *testing.T) {
	stream := storage.NewMemoryEventStream()
	received := make(chan storage.Event, 4)
	if err := stream.Subscribe(t.Context(), events.GameStream, func(e storage.Event) {
		received <- e
	}); err != nil {
		t.Fatalf("Subscribe: %v", err)
	}

	srv := New(ServerOpts{
		Config:      config.Config{TickRate: 20, JWTSecret: testSecret},
		PlayerStore: storage.NewMemoryPlayerStore(),
		Registry:    storage.NewMemoryServerRegistry(),
		EventStream: stream,
		ServerID:    "gs-tick",
		MapID:       "map_tick",
		Logger:      slog.New(slog.NewTextHandler(io.Discard, nil)),
	})

	w := srv.World()
	w.AddEntity(&game.Entity{ID: "attacker", Type: "player", X: 0, Y: 0, HP: 100, MaxHP: 100, Attack: 500, Defense: 5, Speed: 1})
	w.AddEntity(&game.Entity{ID: "target", Type: "player", X: 1, Y: 0, HP: 10, MaxHP: 100, Attack: 5, Defense: 1, Speed: 1})

	runner := NewTickRunner(w, srv.handler, srv.conns, 20, srv.logger)
	w.PushInput("attacker", messages.InputMessage{Tick: 7, AttackTargetID: "target"})
	runner.TickOnce()

	if !w.GetEntity("target").Dead {
		t.Fatal("target not dead after lethal attack")
	}
	if got := w.LastInputTick("attacker"); got != 7 {
		t.Errorf("LastInputTick = %d, want 7", got)
	}

	select {
	case ev := <-received:
		if ev.Type != events.TypePlayerDeath {
			t.Fatalf("event type = %q, want %q", ev.Type, events.TypePlayerDeath)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("no player_death event from tick loop")
	}
}
