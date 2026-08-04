package integration

import (
	"context"
	"encoding/json"
	"log/slog"
	"os"
	"sync"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/redis/go-redis/v9"

	"github.com/duycuong/rpg-mmo/shared/config"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"

	gsevents "github.com/duycuong/rpg-mmo/gameserver/events"
	"github.com/duycuong/rpg-mmo/gameserver/game"
	"github.com/duycuong/rpg-mmo/gameserver/server"

	gwevents "github.com/duycuong/rpg-mmo/gateway/events"
	gwregistry "github.com/duycuong/rpg-mmo/gateway/registry"
	gwserver "github.com/duycuong/rpg-mmo/gateway/server"
	gwsession "github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/gateway/transfer"

	nauth "github.com/duycuong/rpg-mmo/nakama/auth"
)

// --- helpers ---

func testLogger(level slog.Level) *slog.Logger {
	return slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: level}))
}

// sharedRedis starts an in-process Redis (miniredis) that stands in for the
// single Redis instance every backend process talks to in production.
func sharedRedis(t *testing.T) *miniredis.Miniredis {
	t.Helper()
	return miniredis.RunT(t)
}

// newRedisClient creates a NEW client/pool per component, mirroring separate
// OS processes: nothing but Redis itself is shared between gateway and
// game servers.
func newRedisClient(t *testing.T, mr *miniredis.Miniredis) *redis.Client {
	t.Helper()
	c := redis.NewClient(&redis.Options{Addr: mr.Addr()})
	t.Cleanup(func() { _ = c.Close() })
	return c
}

// waitAddr polls a listener address until it is bound (no fixed sleeps).
func waitAddr(t *testing.T, name string, fn func() string) string {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if addr := fn(); addr != "" {
			return addr
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("%s did not start within 3s", name)
	return ""
}

// waitFor polls cond until it returns true or the deadline passes.
func waitFor(t *testing.T, what string, timeout time.Duration, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s (%s)", what, timeout)
}

// startGameServer boots a game server on a random port with its own stores.
func startGameServer(t *testing.T, opts server.ServerOpts) (*server.Server, string) {
	t.Helper()
	if opts.Logger == nil {
		opts.Logger = testLogger(slog.LevelWarn)
	}
	gs := server.New(opts)
	go func() { _ = gs.Run(":0") }()
	t.Cleanup(gs.Shutdown)
	return gs, waitAddr(t, "game server "+opts.ServerID, gs.Addr)
}

// authAndEnterWorld drives the gateway half of the flow: MsgAuth then
// MsgEnterWorld. Returns the assigned server address and join token.
func authAndEnterWorld(t *testing.T, gwAddr, userID, secret, mapID string) (string, string) {
	t.Helper()
	c, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("connect gateway: %v", err)
	}
	defer c.Close()

	token, err := jwt.Sign(userID, secret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	if err := c.Send(authEnv); err != nil {
		t.Fatalf("send auth: %v", err)
	}
	authRespEnv, err := c.Receive()
	if err != nil {
		t.Fatalf("receive auth resp: %v", err)
	}
	var authResp messages.AuthResponse
	if err := messages.UnmarshalPayload(authRespEnv.Payload, &authResp); err != nil {
		t.Fatalf("unmarshal auth resp: %v", err)
	}
	if !authResp.OK {
		t.Fatalf("auth failed: %s", authResp.Error)
	}

	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: mapID})
	if err := c.Send(enterEnv); err != nil {
		t.Fatalf("send enter world: %v", err)
	}
	enterRespEnv, err := c.Receive()
	if err != nil {
		t.Fatalf("receive enter world resp: %v", err)
	}
	var enterResp messages.EnterWorldResponse
	if err := messages.UnmarshalPayload(enterRespEnv.Payload, &enterResp); err != nil {
		t.Fatalf("unmarshal enter world resp: %v", err)
	}
	if enterResp.Error != "" {
		t.Fatalf("enter world error: %s", enterResp.Error)
	}
	if enterResp.ServerAddr == "" || enterResp.JoinToken == "" {
		t.Fatalf("enter world incomplete: addr=%q token=%q", enterResp.ServerAddr, enterResp.JoinToken)
	}
	return enterResp.ServerAddr, enterResp.JoinToken
}

// joinGameServer connects to a game server and presents a join token.
func joinGameServer(t *testing.T, addr, token string) (*MockClient, messages.JoinTokenResponse) {
	t.Helper()
	c, err := NewMockClient(addr)
	if err != nil {
		t.Fatalf("connect game server %s: %v", addr, err)
	}
	joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
	if err := c.Send(joinEnv); err != nil {
		c.Close()
		t.Fatalf("send join token: %v", err)
	}
	respEnv, err := c.Receive()
	if err != nil {
		c.Close()
		t.Fatalf("receive join resp: %v", err)
	}
	var resp messages.JoinTokenResponse
	if err := messages.UnmarshalPayload(respEnv.Payload, &resp); err != nil {
		c.Close()
		t.Fatalf("unmarshal join resp: %v", err)
	}
	return c, resp
}

// awaitEntity reads snapshots until the given entity id appears (or timeout).
func awaitEntity(t *testing.T, c *MockClient, id string) messages.EntitySnapshot {
	t.Helper()
	for i := 0; i < 10; i++ {
		env, err := c.Receive()
		if err != nil {
			t.Fatalf("receive snapshot: %v", err)
		}
		if env.Type != messages.MsgSnapshot {
			continue
		}
		var snap messages.SnapshotMessage
		if err := messages.UnmarshalPayload(env.Payload, &snap); err != nil {
			t.Fatalf("unmarshal snapshot: %v", err)
		}
		for _, e := range snap.Entities {
			if e.ID == id {
				return e
			}
		}
	}
	t.Fatalf("entity %s never appeared in a snapshot", id)
	return messages.EntitySnapshot{}
}

// --- 1. Redis-backed cross-process flow ---

// TestRedisBackedFullFlow runs the complete core flow with the gateway and TWO
// game servers wired to ONE Redis instance, each constructing its own
// redisstore clients — no in-memory structs are shared between components, so
// this exercises the real multi-process story:
// auth -> enter world -> registry lookup finds a self-registered game server
// -> join with the issued token -> input -> snapshot.
func TestRedisBackedFullFlow(t *testing.T) {
	mr := sharedRedis(t)
	logger := testLogger(slog.LevelWarn)
	cfg := config.Config{JWTSecret: testJWTSecret, TickRate: 10}

	// --- Game server A (own Redis client) ---
	gsAClient := newRedisClient(t, mr)
	gsA, addrA := startGameServer(t, server.ServerOpts{
		Config:      cfg,
		PlayerStore: storage.NewMemoryPlayerStore(),
		Registry:    redisstore.NewServerRegistryWithClient(gsAClient, constants.ServerHeartbeatTTL),
		EventStream: redisstore.NewEventStreamWithClient(gsAClient, "gameserver", "gs-redis-A"),
		ServerID:    "gs-redis-A",
		MapID:       "map_01",
		Capacity:    100,
		Logger:      logger,
	})
	_ = gsA

	// --- Game server B (own Redis client, same map) ---
	gsBClient := newRedisClient(t, mr)
	_, addrB := startGameServer(t, server.ServerOpts{
		Config:      cfg,
		PlayerStore: storage.NewMemoryPlayerStore(),
		Registry:    redisstore.NewServerRegistryWithClient(gsBClient, constants.ServerHeartbeatTTL),
		EventStream: redisstore.NewEventStreamWithClient(gsBClient, "gameserver", "gs-redis-B"),
		ServerID:    "gs-redis-B",
		MapID:       "map_01",
		Capacity:    100,
		Logger:      logger,
	})

	// --- Gateway (own Redis client for session store + registry) ---
	gwClient := newRedisClient(t, mr)
	gwReg := redisstore.NewServerRegistryWithClient(gwClient, constants.ServerHeartbeatTTL)
	gw := gwserver.New(
		gwsession.NewSessionManager(redisstore.NewSessionStoreWithClient(gwClient)),
		gwregistry.NewRegistryService(gwReg),
		testJWTSecret, logger,
	)
	go func() { _ = gw.Run(":0") }()
	defer gw.Shutdown()
	gwAddr := waitAddr(t, "gateway", gw.Addr)

	// Both game servers must be visible to the gateway THROUGH REDIS.
	waitFor(t, "both game servers registered in redis", 3*time.Second, func() bool {
		servers, err := gwReg.FindByMapID(context.Background(), "map_01")
		return err == nil && len(servers) == 2
	})
	t.Log("gateway sees 2 self-registered game servers via redis registry")

	// --- Client flow ---
	const userID = "player-redis-001"
	serverAddr, joinToken := authAndEnterWorld(t, gwAddr, userID, testJWTSecret, "map_01")
	if serverAddr != addrA && serverAddr != addrB {
		t.Fatalf("assigned addr %q is neither %q nor %q", serverAddr, addrA, addrB)
	}
	t.Logf("gateway assigned %s", serverAddr)

	// The join token must be pinned to the assigned server (sid claim).
	_, sid, err := transfer.ValidateJoinToken(joinToken, testJWTSecret)
	if err != nil {
		t.Fatalf("validate join token: %v", err)
	}
	if sid == "" {
		t.Fatal("join token has no sid claim")
	}
	t.Logf("join token sid=%s", sid)

	gsClient, joinResp := joinGameServer(t, serverAddr, joinToken)
	defer gsClient.Close()
	if !joinResp.OK {
		t.Fatalf("join rejected: %s", joinResp.Error)
	}
	if joinResp.UserID != userID {
		t.Fatalf("join user = %q, want %q", joinResp.UserID, userID)
	}

	inputEnv, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 2.0})
	if err := gsClient.Send(inputEnv); err != nil {
		t.Fatalf("send input: %v", err)
	}
	ent := awaitEntity(t, gsClient, userID)
	if ent.X < 1.9 {
		t.Errorf("player X = %.2f, want >= 1.9 after move", ent.X)
	}
	t.Logf("redis-backed E2E OK: player at (%.2f, %.2f) HP=%d", ent.X, ent.Y, ent.HP)

	// Player count propagated back through Redis to the gateway's view.
	waitFor(t, "player count visible via redis", 2*time.Second, func() bool {
		info, err := gwReg.GetServer(context.Background(), sid)
		return err == nil && info.PlayerCount == 1
	})
	t.Log("player count propagated through redis registry")
}

// --- 2. sid enforcement across servers ---

// TestSidEnforcement_CrossServer proves a join token minted for game server A
// is rejected by game server B, and accepted by A — both sharing one Redis.
func TestSidEnforcement_CrossServer(t *testing.T) {
	mr := sharedRedis(t)
	logger := testLogger(slog.LevelWarn)
	cfg := config.Config{JWTSecret: testJWTSecret, TickRate: 10}

	newGS := func(id string) (*server.Server, string) {
		client := newRedisClient(t, mr)
		return startGameServer(t, server.ServerOpts{
			Config:      cfg,
			PlayerStore: storage.NewMemoryPlayerStore(),
			Registry:    redisstore.NewServerRegistryWithClient(client, constants.ServerHeartbeatTTL),
			ServerID:    id,
			MapID:       "map_01",
			Capacity:    100,
			Logger:      logger,
		})
	}
	_, addrA := newGS("gs-sid-A")
	_, addrB := newGS("gs-sid-B")

	tokenForA, err := transfer.GenerateJoinToken("player-sid-001", "gs-sid-A", testJWTSecret)
	if err != nil {
		t.Fatalf("generate join token: %v", err)
	}

	tests := []struct {
		name   string
		addr   string
		wantOK bool
	}{
		{"token for A accepted by A", addrA, true},
		{"token for A rejected by B", addrB, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			c, resp := joinGameServer(t, tt.addr, tokenForA)
			defer c.Close()
			if resp.OK != tt.wantOK {
				t.Fatalf("join OK = %v (err=%q), want %v", resp.OK, resp.Error, tt.wantOK)
			}
			if !tt.wantOK && resp.Error == "" {
				t.Error("rejection carried no error message")
			}
			if !tt.wantOK {
				t.Logf("cross-server token correctly rejected: %s", resp.Error)
			}
		})
	}
}

// --- 3. Reconnect inside the hold window ---

// TestReconnect_HoldWindow proves that dropping the TCP connection keeps the
// entity alive for the hold window and a reconnect reattaches to the SAME
// in-world entity with position preserved.
//
// The persisted player record is deleted while the player is held, so a
// preserved position can only come from the held entity, not from a store
// reload.
func TestReconnect_HoldWindow(t *testing.T) {
	logger := testLogger(slog.LevelWarn)
	playerStore := storage.NewMemoryPlayerStore()
	const userID = "player-reconnect-001"
	const serverID = "gs-hold-01"

	gs, addr := startGameServer(t, server.ServerOpts{
		Config:      config.Config{JWTSecret: testJWTSecret, TickRate: 20},
		PlayerStore: playerStore,
		Registry:    storage.NewMemoryServerRegistry(),
		ServerID:    serverID,
		MapID:       "map_01",
		Capacity:    100,
		// Short hold window so the test never waits on the 30s production TTL.
		HoldTTL: 5 * time.Second,
		Logger:  logger,
	})

	token, err := transfer.GenerateJoinToken(userID, serverID, testJWTSecret)
	if err != nil {
		t.Fatalf("generate join token: %v", err)
	}

	// --- First session: join and move ---
	c1, resp := joinGameServer(t, addr, token)
	if !resp.OK {
		c1.Close()
		t.Fatalf("first join rejected: %s", resp.Error)
	}
	moveEnv, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 4.0, MoveY: 3.0})
	if err := c1.Send(moveEnv); err != nil {
		t.Fatalf("send move: %v", err)
	}
	before := awaitEntity(t, c1, userID)
	if before.X < 3.9 || before.Y < 2.9 {
		t.Fatalf("expected player near (4,3), got (%.2f, %.2f)", before.X, before.Y)
	}
	t.Logf("pre-drop position: (%.2f, %.2f) HP=%d", before.X, before.Y, before.HP)

	// --- Drop the TCP connection ---
	c1.Close()
	waitFor(t, "entity placed in hold window", 3*time.Second, func() bool {
		return gs.HeldCount() == 1
	})
	if e := gs.World().GetEntity(userID); e == nil {
		t.Fatal("entity removed immediately on disconnect, expected hold")
	}
	t.Log("entity held after disconnect")

	// Remove the persisted record: only the held entity can supply the position now.
	if err := playerStore.DeletePlayer(context.Background(), userID); err != nil {
		t.Fatalf("DeletePlayer: %v", err)
	}

	// --- Reconnect inside the hold window ---
	c2, resp2 := joinGameServer(t, addr, token)
	defer c2.Close()
	if !resp2.OK {
		t.Fatalf("reconnect rejected: %s", resp2.Error)
	}
	after := awaitEntity(t, c2, userID)
	if after.X != before.X || after.Y != before.Y {
		t.Errorf("position not preserved across reconnect: before (%.2f, %.2f), after (%.2f, %.2f)",
			before.X, before.Y, after.X, after.Y)
	}
	if after.HP != before.HP {
		t.Errorf("HP not preserved: before %d, after %d", before.HP, after.HP)
	}
	if gs.HeldCount() != 0 {
		t.Errorf("HeldCount = %d after reconnect, want 0", gs.HeldCount())
	}
	t.Logf("reconnect reattached to held entity at (%.2f, %.2f) HP=%d", after.X, after.Y, after.HP)

	// NOTE: hold *expiry* (entity removed after the window elapses) is covered by
	// gameserver unit tests; asserting it here would mean waiting out the timer,
	// so this E2E only proves the reattach path.
}

// --- 4. Cross-server event delivery ---

// TestCrossServerEvent_DeathRelay proves a death on a game server travels
// through Redis Streams and is delivered to the gateway's event relay sink.
//
// The relay is constructed on the stream the game server publishes to
// (gsevents.GameStream). The gateway binary's default stream name is
// gwevents.DefaultStream ("global"), which does NOT match — see the report.
func TestCrossServerEvent_DeathRelay(t *testing.T) {
	mr := sharedRedis(t)
	logger := testLogger(slog.LevelWarn)
	cfg := config.Config{JWTSecret: testJWTSecret, TickRate: 20}
	const serverID = "gs-event-01"
	const userID = "player-killer-001"

	// --- Gateway side: its own Redis client + consumer group ---
	gwClient := newRedisClient(t, mr)
	gwStream := redisstore.NewEventStreamWithClient(gwClient, "gateway", "gw-1")
	gwStream.SetBlockTimeout(50 * time.Millisecond)

	var (
		mu       sync.Mutex
		received []storage.Event
	)
	relay := gwevents.NewRelay(gwStream, gsevents.GameStream, gwevents.SinkFunc(func(ev storage.Event) {
		mu.Lock()
		received = append(received, ev)
		mu.Unlock()
	}), logger)

	gw := gwserver.New(
		gwsession.NewSessionManager(redisstore.NewSessionStoreWithClient(gwClient)),
		gwregistry.NewRegistryService(redisstore.NewServerRegistryWithClient(gwClient, constants.ServerHeartbeatTTL)),
		testJWTSecret, logger,
		gwserver.WithEventRelay(relay),
	)
	go func() { _ = gw.Run(":0") }()
	defer gw.Shutdown()
	waitAddr(t, "gateway", gw.Addr)

	// --- Game server side: its own Redis client + publisher ---
	gsClientRedis := newRedisClient(t, mr)
	gs, addr := startGameServer(t, server.ServerOpts{
		Config:      cfg,
		PlayerStore: storage.NewMemoryPlayerStore(),
		Registry:    redisstore.NewServerRegistryWithClient(gsClientRedis, constants.ServerHeartbeatTTL),
		EventStream: redisstore.NewEventStreamWithClient(gsClientRedis, "gameserver", serverID),
		ServerID:    serverID,
		MapID:       "map_01",
		Capacity:    100,
		Logger:      logger,
	})

	// A boss standing on the spawn point, one hit from death.
	gs.World().AddEntity(&game.Entity{
		ID: "boss-01", Type: "boss", X: 0, Y: 0, HP: 1, MaxHP: 500, Attack: 20, Defense: 0, Speed: 1,
	})

	token, err := transfer.GenerateJoinToken(userID, serverID, testJWTSecret)
	if err != nil {
		t.Fatalf("generate join token: %v", err)
	}
	c, resp := joinGameServer(t, addr, token)
	defer c.Close()
	if !resp.OK {
		t.Fatalf("join rejected: %s", resp.Error)
	}

	attackEnv, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
		Tick: 1, AttackTargetID: "boss-01",
	})
	if err := c.Send(attackEnv); err != nil {
		t.Fatalf("send attack: %v", err)
	}

	waitFor(t, "death event delivered to gateway relay", 5*time.Second, func() bool {
		mu.Lock()
		defer mu.Unlock()
		return len(received) > 0
	})

	mu.Lock()
	ev := received[0]
	mu.Unlock()

	if ev.Type != gsevents.TypeBossKilled {
		t.Errorf("event type = %q, want %q", ev.Type, gsevents.TypeBossKilled)
	}
	var payload gsevents.DeathPayload
	if err := json.Unmarshal(ev.Payload, &payload); err != nil {
		t.Fatalf("unmarshal death payload: %v", err)
	}
	if payload.VictimID != "boss-01" {
		t.Errorf("victim = %q, want boss-01", payload.VictimID)
	}
	if payload.KillerID != userID {
		t.Errorf("killer = %q, want %q", payload.KillerID, userID)
	}
	if payload.ServerID != serverID {
		t.Errorf("server_id = %q, want %q", payload.ServerID, serverID)
	}
	if payload.MapID != "map_01" {
		t.Errorf("map_id = %q, want map_01", payload.MapID)
	}
	t.Logf("cross-server event delivered: %s victim=%s killer=%s from=%s/%s",
		ev.Type, payload.VictimID, payload.KillerID, payload.ServerID, payload.MapID)
}

// --- 5. Nakama-issued token compatibility ---

// TestNakamaTokenCompat proves a realtime token issued by the Nakama auth
// plugin (auth.IssueGatewayToken) verifies with the gateway's own verifier and
// with the game server's sid enforcement — the shared-secret contract holds
// end to end, with no Nakama roundtrip at runtime.
func TestNakamaTokenCompat(t *testing.T) {
	const userID = "player-nakama-001"
	const serverID = "gs-nakama-01"
	cfg := nauth.Config{JWTSecret: testJWTSecret, TokenTTL: 5 * time.Minute}

	tests := []struct {
		name     string
		serverID string
	}{
		{"token without sid (gateway auth)", ""},
		{"token with sid (join token)", serverID},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			resp, err := nauth.IssueGatewayToken(userID, tt.serverID, cfg)
			if err != nil {
				t.Fatalf("IssueGatewayToken: %v", err)
			}
			if resp.UserID != userID {
				t.Errorf("resp.UserID = %q, want %q", resp.UserID, userID)
			}
			if resp.ExpiresIn != int64(cfg.TokenTTL.Seconds()) {
				t.Errorf("resp.ExpiresIn = %d, want %d", resp.ExpiresIn, int64(cfg.TokenTTL.Seconds()))
			}

			// Gateway-side verification (session.VerifyClientJWT).
			gotUser, err := gwsession.VerifyClientJWT(resp.Token, testJWTSecret)
			if err != nil {
				t.Fatalf("gateway VerifyClientJWT: %v", err)
			}
			if gotUser != userID {
				t.Errorf("gateway verified user = %q, want %q", gotUser, userID)
			}

			// Game-server-side validation (join token path).
			gotUser, gotSid, err := transfer.ValidateJoinToken(resp.Token, testJWTSecret)
			if err != nil {
				t.Fatalf("ValidateJoinToken: %v", err)
			}
			if gotUser != userID || gotSid != tt.serverID {
				t.Errorf("join token claims = (%q, %q), want (%q, %q)", gotUser, gotSid, userID, tt.serverID)
			}

			// A different secret must not verify.
			if _, err := gwsession.VerifyClientJWT(resp.Token, "other-secret"); err == nil {
				t.Error("token verified with the wrong secret")
			}
		})
	}
}

// TestNakamaToken_AcceptedByGameServer runs the sid-bearing Nakama token
// through a live game server handshake.
func TestNakamaToken_AcceptedByGameServer(t *testing.T) {
	const userID = "player-nakama-002"
	const serverID = "gs-nakama-live"

	_, addr := startGameServer(t, server.ServerOpts{
		Config:      config.Config{JWTSecret: testJWTSecret, TickRate: 10},
		PlayerStore: storage.NewMemoryPlayerStore(),
		Registry:    storage.NewMemoryServerRegistry(),
		ServerID:    serverID,
		MapID:       "map_01",
		Capacity:    100,
	})

	resp, err := nauth.IssueGatewayToken(userID, serverID, nauth.Config{
		JWTSecret: testJWTSecret, TokenTTL: 5 * time.Minute,
	})
	if err != nil {
		t.Fatalf("IssueGatewayToken: %v", err)
	}

	c, joinResp := joinGameServer(t, addr, resp.Token)
	defer c.Close()
	if !joinResp.OK {
		t.Fatalf("game server rejected nakama-issued token: %s", joinResp.Error)
	}
	if joinResp.UserID != userID {
		t.Errorf("join user = %q, want %q", joinResp.UserID, userID)
	}
	t.Log("nakama-issued token accepted by game server")
}
