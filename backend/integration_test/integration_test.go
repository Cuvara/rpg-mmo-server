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

	"github.com/duycuong/rpg-mmo/gameserver/server"

	gwregistry "github.com/duycuong/rpg-mmo/gateway/registry"
	gwserver "github.com/duycuong/rpg-mmo/gateway/server"
	gwsession "github.com/duycuong/rpg-mmo/gateway/session"
)

const testJWTSecret = "integration-test-secret"

// TestFullFlow_GameServerDirect tests the gameserver flow without the gateway.
// This is the subset that works immediately: connect -> join token -> input -> snapshot.
func TestFullFlow_GameServerDirect(t *testing.T) {
	// --- Shared stores ---
	playerStore := storage.NewMemoryPlayerStore()
	registry := storage.NewMemoryServerRegistry()
	eventStream := storage.NewMemoryEventStream()
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelDebug}))

	cfg := config.Config{
		JWTSecret: testJWTSecret,
		TickRate:  10,
	}

	// --- Start game server on random port ---
	gs := server.New(server.ServerOpts{
		Config:      cfg,
		PlayerStore: playerStore,
		Registry:    registry,
		EventStream: eventStream,
		ServerID:    "gs-test-01",
		MapID:       "map_01",
		Capacity:    100,
		Logger:      logger,
	})

	serverErrCh := make(chan error, 1)
	go func() {
		serverErrCh <- gs.Run(":0")
	}()
	defer gs.Shutdown()

	// Wait for server to be ready (listener bound)
	var gsAddr string
	for i := 0; i < 50; i++ {
		gsAddr = gs.Addr()
		if gsAddr != "" {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if gsAddr == "" {
		t.Fatal("game server did not start within 1s")
	}
	t.Logf("game server listening on %s", gsAddr)

	// Verify the server registered itself in the registry
	servers, err := registry.FindByMapID(context.Background(), "map_01")
	if err != nil {
		t.Fatalf("FindByMapID: %v", err)
	}
	if len(servers) != 1 {
		t.Fatalf("expected 1 server in registry, got %d", len(servers))
	}
	if servers[0].Addr != gsAddr {
		t.Fatalf("registry addr mismatch: want %s, got %s", gsAddr, servers[0].Addr)
	}
	t.Log("server registered in registry OK")

	// --- Create a valid JWT for testing ---
	userID := "player-001"
	token, err := jwt.Sign(userID, testJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}

	// --- Connect to game server and send JoinToken ---
	client, err := NewMockClient(gsAddr)
	if err != nil {
		t.Fatalf("connect to game server: %v", err)
	}
	defer client.Close()

	joinEnv, err := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{
		Token: token,
	})
	if err != nil {
		t.Fatalf("NewEnvelope(MsgJoinToken): %v", err)
	}
	if err := client.Send(joinEnv); err != nil {
		t.Fatalf("send join token: %v", err)
	}

	// Receive join response
	respEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive join response: %v", err)
	}
	if respEnv.Type != messages.MsgJoinTokenResp {
		t.Fatalf("expected MsgJoinTokenResp, got type %d", respEnv.Type)
	}
	var joinResp messages.JoinTokenResponse
	if err := messages.UnmarshalPayload(respEnv.Payload, &joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("join rejected: %s", joinResp.Error)
	}
	if joinResp.UserID != userID {
		t.Fatalf("join user mismatch: want %s, got %s", userID, joinResp.UserID)
	}
	t.Log("join accepted OK")

	// --- Send movement input ---
	inputEnv, err := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
		Tick:  1,
		MoveX: 1.0,
		MoveY: 0.0,
	})
	if err != nil {
		t.Fatalf("NewEnvelope(MsgInput): %v", err)
	}
	if err := client.Send(inputEnv); err != nil {
		t.Fatalf("send input: %v", err)
	}

	// Wait for at least one tick to process and send snapshot
	// At 10Hz that is 100ms; we wait up to 500ms for safety.
	var snapshot messages.SnapshotMessage
	received := false
	for i := 0; i < 5; i++ {
		snapEnv, err := client.Receive()
		if err != nil {
			t.Logf("receive attempt %d: %v", i, err)
			continue
		}
		if snapEnv.Type == messages.MsgSnapshot {
			if err := messages.UnmarshalPayload(snapEnv.Payload, &snapshot); err != nil {
				t.Fatalf("unmarshal snapshot: %v", err)
			}
			received = true
			break
		}
	}
	if !received {
		t.Fatal("did not receive a snapshot within timeout")
	}
	t.Logf("received snapshot tick=%d with %d entities", snapshot.Tick, len(snapshot.Entities))

	// Verify the player entity is in the snapshot with moved position
	found := false
	for _, e := range snapshot.Entities {
		if e.ID == userID {
			found = true
			if e.X < 0.5 {
				t.Errorf("expected player X >= 0.5 after move, got %f", e.X)
			}
			t.Logf("player entity: X=%.2f Y=%.2f HP=%d", e.X, e.Y, e.HP)
			break
		}
	}
	if !found {
		t.Fatal("player entity not found in snapshot")
	}
	t.Log("snapshot contains moved player OK")

	// --- Disconnect and verify state persistence ---
	client.Close()

	// Give the server time to save (disconnect triggers SaveAll)
	time.Sleep(200 * time.Millisecond)

	state, err := playerStore.LoadPlayer(context.Background(), userID)
	if err != nil {
		t.Fatalf("LoadPlayer after disconnect: %v", err)
	}
	if state.X < 0.5 {
		t.Errorf("saved player X should be >= 0.5, got %f", state.X)
	}
	t.Logf("player state persisted: X=%.2f Y=%.2f HP=%d MapID=%s", state.X, state.Y, state.HP, state.MapID)
	t.Log("persistence verified OK")
}

// TestFullFlow tests the complete E2E path through gateway and gameserver.
func TestFullFlow(t *testing.T) {
	// --- Shared stores ---
	playerStore := storage.NewMemoryPlayerStore()
	sessionStore := storage.NewMemorySessionStore()
	reg := storage.NewMemoryServerRegistry()
	eventStream := storage.NewMemoryEventStream()
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelDebug}))

	cfg := config.Config{
		JWTSecret: testJWTSecret,
		TickRate:  10,
	}

	// --- Start game server on random port ---
	gs := server.New(server.ServerOpts{
		Config:      cfg,
		PlayerStore: playerStore,
		Registry:    reg,
		EventStream: eventStream,
		ServerID:    "gs-e2e-01",
		MapID:       "map_01",
		Capacity:    100,
		Logger:      logger,
	})
	go gs.Run(":0")
	defer gs.Shutdown()

	var gsAddr string
	for i := 0; i < 50; i++ {
		gsAddr = gs.Addr()
		if gsAddr != "" {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if gsAddr == "" {
		t.Fatal("game server did not start")
	}
	t.Logf("game server listening on %s", gsAddr)

	// --- Start gateway on random port with same shared stores ---
	sessionMgr := gwsession.NewSessionManager(sessionStore)
	registrySvc := gwregistry.NewRegistryService(reg)
	gw := gwserver.New(sessionMgr, registrySvc, testJWTSecret, logger)
	go gw.Run(":0")
	defer gw.Shutdown()

	var gwAddr string
	for i := 0; i < 50; i++ {
		gwAddr = gw.Addr()
		if gwAddr != "" {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if gwAddr == "" {
		t.Fatal("gateway did not start")
	}
	t.Logf("gateway listening on %s", gwAddr)

	// Step 1: Connect to gateway and authenticate
	gwClient, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("connect to gateway: %v", err)
	}

	userID := "player-e2e-001"
	token, err := jwt.Sign(userID, testJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}

	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	if err := gwClient.Send(authEnv); err != nil {
		t.Fatalf("send auth: %v", err)
	}

	authRespEnv, err := gwClient.Receive()
	if err != nil {
		t.Fatalf("receive auth response: %v", err)
	}
	if authRespEnv.Type != messages.MsgAuthResp {
		t.Fatalf("expected MsgAuthResp, got type %d", authRespEnv.Type)
	}
	var authResp messages.AuthResponse
	if err := messages.UnmarshalPayload(authRespEnv.Payload, &authResp); err != nil {
		t.Fatalf("unmarshal auth response: %v", err)
	}
	if !authResp.OK {
		t.Fatalf("auth failed: %s", authResp.Error)
	}
	if authResp.UserID != userID {
		t.Fatalf("auth user mismatch: want %s, got %s", userID, authResp.UserID)
	}
	t.Log("gateway auth OK")

	// Step 2: Enter world — gateway looks up map_01 in registry, issues join token
	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_01"})
	if err := gwClient.Send(enterEnv); err != nil {
		t.Fatalf("send enter world: %v", err)
	}

	enterRespEnv, err := gwClient.Receive()
	if err != nil {
		t.Fatalf("receive enter world response: %v", err)
	}
	if enterRespEnv.Type != messages.MsgEnterWorldResp {
		t.Fatalf("expected MsgEnterWorldResp, got type %d", enterRespEnv.Type)
	}
	var enterResp messages.EnterWorldResponse
	if err := messages.UnmarshalPayload(enterRespEnv.Payload, &enterResp); err != nil {
		t.Fatalf("unmarshal enter world response: %v", err)
	}
	if enterResp.Error != "" {
		t.Fatalf("enter world error: %s", enterResp.Error)
	}
	if enterResp.ServerAddr == "" || enterResp.JoinToken == "" {
		t.Fatalf("enter world missing addr/token: addr=%q token=%q", enterResp.ServerAddr, enterResp.JoinToken)
	}
	t.Logf("enter world OK: server=%s", enterResp.ServerAddr)

	gwClient.Close()

	// Step 3: Connect to game server with the join token from gateway
	gsClient, err := NewMockClient(enterResp.ServerAddr)
	if err != nil {
		t.Fatalf("connect to game server: %v", err)
	}
	defer gsClient.Close()

	joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: enterResp.JoinToken})
	if err := gsClient.Send(joinEnv); err != nil {
		t.Fatalf("send join token: %v", err)
	}

	joinRespEnv, err := gsClient.Receive()
	if err != nil {
		t.Fatalf("receive join response: %v", err)
	}
	var joinResp messages.JoinTokenResponse
	if err := messages.UnmarshalPayload(joinRespEnv.Payload, &joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("join rejected: %s", joinResp.Error)
	}
	t.Log("game server join via gateway token OK")

	// Step 4: Send movement and wait for snapshot
	inputEnv, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
		Tick:  1,
		MoveX: 1.0,
		MoveY: 0.0,
	})
	if err := gsClient.Send(inputEnv); err != nil {
		t.Fatalf("send input: %v", err)
	}

	var snapshot messages.SnapshotMessage
	received := false
	for i := 0; i < 5; i++ {
		snapEnv, err := gsClient.Receive()
		if err != nil {
			t.Logf("receive attempt %d: %v", i, err)
			continue
		}
		if snapEnv.Type == messages.MsgSnapshot {
			if err := messages.UnmarshalPayload(snapEnv.Payload, &snapshot); err != nil {
				t.Fatalf("unmarshal snapshot: %v", err)
			}
			received = true
			break
		}
	}
	if !received {
		t.Fatal("did not receive a snapshot within timeout")
	}
	t.Logf("received snapshot tick=%d with %d entities", snapshot.Tick, len(snapshot.Entities))

	found := false
	for _, e := range snapshot.Entities {
		if e.ID == userID {
			found = true
			if e.X < 0.5 {
				t.Errorf("player X should be >= 0.5, got %f", e.X)
			}
			t.Logf("player entity: X=%.2f Y=%.2f HP=%d", e.X, e.Y, e.HP)
			break
		}
	}
	if !found {
		t.Fatal("player not found in snapshot")
	}

	// Step 5: Disconnect and verify persistence
	gsClient.Close()
	time.Sleep(200 * time.Millisecond)

	state, err := playerStore.LoadPlayer(context.Background(), userID)
	if err != nil {
		t.Fatalf("LoadPlayer: %v", err)
	}
	if state.X < 0.5 {
		t.Errorf("saved X should be >= 0.5, got %f", state.X)
	}
	t.Logf("E2E flow passed: player at (%.2f, %.2f) HP=%d", state.X, state.Y, state.HP)
}

// TestJoinToken_InvalidToken verifies that the game server rejects a bad JWT.
func TestJoinToken_InvalidToken(t *testing.T) {
	playerStore := storage.NewMemoryPlayerStore()
	registry := storage.NewMemoryServerRegistry()
	eventStream := storage.NewMemoryEventStream()
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelWarn}))

	cfg := config.Config{
		JWTSecret: testJWTSecret,
		TickRate:  10,
	}

	gs := server.New(server.ServerOpts{
		Config:      cfg,
		PlayerStore: playerStore,
		Registry:    registry,
		EventStream: eventStream,
		ServerID:    "gs-test-02",
		MapID:       "map_01",
		Capacity:    100,
		Logger:      logger,
	})

	go gs.Run(":0")
	defer gs.Shutdown()

	var gsAddr string
	for i := 0; i < 50; i++ {
		gsAddr = gs.Addr()
		if gsAddr != "" {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if gsAddr == "" {
		t.Fatal("game server did not start")
	}

	client, err := NewMockClient(gsAddr)
	if err != nil {
		t.Fatalf("connect: %v", err)
	}
	defer client.Close()

	// Send a JWT signed with the wrong secret
	badToken, _ := jwt.Sign("hacker", "wrong-secret", 5*time.Minute)
	joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{
		Token: badToken,
	})
	if err := client.Send(joinEnv); err != nil {
		t.Fatalf("send: %v", err)
	}

	respEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive: %v", err)
	}
	var resp messages.JoinTokenResponse
	if err := messages.UnmarshalPayload(respEnv.Payload, &resp); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if resp.OK {
		t.Fatal("expected join to be rejected with invalid token, but got OK")
	}
	t.Logf("invalid token correctly rejected: %s", resp.Error)
}

// TestMultiplePlayers verifies two players can connect, move, and see each other.
func TestMultiplePlayers(t *testing.T) {
	playerStore := storage.NewMemoryPlayerStore()
	registry := storage.NewMemoryServerRegistry()
	eventStream := storage.NewMemoryEventStream()
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelWarn}))

	cfg := config.Config{
		JWTSecret: testJWTSecret,
		TickRate:  10,
	}

	gs := server.New(server.ServerOpts{
		Config:      cfg,
		PlayerStore: playerStore,
		Registry:    registry,
		EventStream: eventStream,
		ServerID:    "gs-test-03",
		MapID:       "map_01",
		Capacity:    100,
		Logger:      logger,
	})

	go gs.Run(":0")
	defer gs.Shutdown()

	var gsAddr string
	for i := 0; i < 50; i++ {
		gsAddr = gs.Addr()
		if gsAddr != "" {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if gsAddr == "" {
		t.Fatal("game server did not start")
	}

	// Helper to connect and join
	connectAndJoin := func(userID string) *MockClient {
		t.Helper()
		token, err := jwt.Sign(userID, testJWTSecret, 5*time.Minute)
		if err != nil {
			t.Fatalf("jwt.Sign(%s): %v", userID, err)
		}
		client, err := NewMockClient(gsAddr)
		if err != nil {
			t.Fatalf("connect(%s): %v", userID, err)
		}
		joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
		if err := client.Send(joinEnv); err != nil {
			t.Fatalf("send join(%s): %v", userID, err)
		}
		respEnv, err := client.Receive()
		if err != nil {
			t.Fatalf("receive join resp(%s): %v", userID, err)
		}
		var resp messages.JoinTokenResponse
		messages.UnmarshalPayload(respEnv.Payload, &resp)
		if !resp.OK {
			t.Fatalf("join rejected(%s): %s", userID, resp.Error)
		}
		return client
	}

	c1 := connectAndJoin("player-A")
	defer c1.Close()
	c2 := connectAndJoin("player-B")
	defer c2.Close()

	// Player A moves right, Player B moves up
	inputA, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 5.0, MoveY: 0.0})
	inputB, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 0.0, MoveY: 3.0})
	c1.Send(inputA)
	c2.Send(inputB)

	// Wait for snapshots — both players should see each other
	time.Sleep(300 * time.Millisecond)

	// Read snapshot from player A's perspective
	snapEnv, err := c1.Receive()
	if err != nil {
		t.Fatalf("receive snapshot for player-A: %v", err)
	}
	if snapEnv.Type != messages.MsgSnapshot {
		t.Fatalf("expected snapshot, got type %d", snapEnv.Type)
	}
	var snap messages.SnapshotMessage
	messages.UnmarshalPayload(snapEnv.Payload, &snap)

	if len(snap.Entities) < 2 {
		t.Fatalf("expected at least 2 entities in snapshot, got %d", len(snap.Entities))
	}

	seenA, seenB := false, false
	for _, e := range snap.Entities {
		switch e.ID {
		case "player-A":
			seenA = true
			if e.X < 1.0 {
				t.Errorf("player-A X should be >= 1.0, got %f", e.X)
			}
		case "player-B":
			seenB = true
			if e.Y < 1.0 {
				t.Errorf("player-B Y should be >= 1.0, got %f", e.Y)
			}
		}
	}
	if !seenA || !seenB {
		t.Fatalf("expected both players in snapshot: A=%v B=%v", seenA, seenB)
	}
	t.Log("both players visible to each other OK")
}
