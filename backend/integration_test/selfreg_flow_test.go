//go:build integration

package integration

import (
	"context"
	"log/slog"
	"net"
	"os"
	"os/exec"
	"strconv"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/redis/go-redis/v9"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"

	gwregistry "github.com/duycuong/rpg-mmo/gateway/registry"
	gwserver "github.com/duycuong/rpg-mmo/gateway/server"
	gwsession "github.com/duycuong/rpg-mmo/gateway/session"
)

// TestFullFlow_SelfRegistration is the deployed-topology flow test.
//
// Why it exists next to TestDotnetInterop_FullFlow, which walks the same nine
// messages: that test PRE-REGISTERS the game server into an in-memory registry
// itself. The registry entry is therefore constructed by the test, so the test
// cannot fail the way a real deployment fails. Every bring-up failure this
// project has actually hit lives in the gap that pre-registration papers over:
//
//   - the game server never self-registers (REDIS_ADDR unset or unreachable),
//     and the gateway answers MsgEnterWorld with "no available server";
//   - it registers an address that is its LISTEN address rather than one a
//     client can dial, so MsgEnterWorldResp hands back something undialable;
//   - it registers under an id that differs from its own --server-id, so the
//     gateway mints a join token whose `sid` claim the server then rejects.
//
// This test removes the pre-registration: the C# server publishes itself into
// Redis, and the gateway reads the SAME Redis through the same redisstore code
// the `--backend=redis` production wiring uses. Then it walks the whole client
// path — auth, enter world, dial whatever address came back, join, input,
// snapshot, clean disconnect — asserting the registry contents in between.
//
// Redis is miniredis, in-process. That is deliberate: it keeps the test in the
// standard `-tags integration` CI job with no docker service, and it is a real
// TCP RESP server, so the C# side connects to it exactly as it would to Redis.
func TestFullFlow_SelfRegistration(t *testing.T) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}

	mr := miniredis.RunT(t)
	t.Logf("miniredis listening on %s", mr.Addr())

	// The game server advertises a fixed address, so it must LISTEN on a known
	// port: with `--addr 127.0.0.1:0` the advertised value would be the literal
	// ":0" string (GameServer/Program.cs falls back to the configured addr, not
	// the resolved one), which is exactly the undialable-address failure above.
	port := reserveTCPPort(t)
	listenAddr := net.JoinHostPort("127.0.0.1", strconv.Itoa(port))

	gsAddr, gsCleanup := startDotnetGameServerWith(t,
		[]string{"--addr", listenAddr},
		[]string{
			"REDIS_ADDR=" + mr.Addr(),
			"REDIS_PASSWORD=",
			"GAMESERVER_PUBLIC_ADDR=" + listenAddr,
		},
	)
	defer gsCleanup()

	if gsAddr != listenAddr {
		t.Fatalf("game server bound %s, want %s", gsAddr, listenAddr)
	}

	// ---- the registry entry must appear on its own -------------------------
	redisClient := redisstore.NewRedisClient(mr.Addr(), "")
	defer redisClient.Close()
	reg := redisstore.NewServerRegistryWithClient(redisClient, 0)

	ctx := context.Background()
	var registered bool
	deadline := time.Now().Add(30 * time.Second)
	for time.Now().Before(deadline) {
		servers, err := reg.FindByMapID(ctx, dotnetMapID)
		if err == nil && len(servers) > 0 {
			for _, s := range servers {
				t.Logf("registry entry: id=%s map=%s addr=%s capacity=%d",
					s.ServerID, s.MapID, s.Addr, s.Capacity)
				if s.ServerID != dotnetServerID {
					t.Errorf("registered server id = %q, want %q — the gateway mints "+
						"the join token's sid claim from this, and the game server "+
						"checks it against its own --server-id", s.ServerID, dotnetServerID)
				}
				if s.Addr != listenAddr {
					t.Errorf("registered addr = %q, want %q (must be dialable BY A CLIENT)",
						s.Addr, listenAddr)
				}
			}
			registered = true
			break
		}
		time.Sleep(200 * time.Millisecond)
	}
	if !registered {
		t.Fatal("game server never registered itself in Redis: the gateway would " +
			"answer MsgEnterWorld with \"no available server for map " + dotnetMapID + "\"")
	}

	// ---- gateway on the SAME Redis, with nothing pre-seeded ----------------
	gwAddr, gwCleanup := startRedisBackedGateway(t, redisClient)
	defer gwCleanup()

	// ---- 1/2: MsgAuth ------------------------------------------------------
	client, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("connect to gateway: %v", err)
	}

	userID := "selfreg-player"
	token, err := jwt.Sign(userID, dotnetJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	if err := client.Send(authEnv); err != nil {
		t.Fatalf("send auth: %v", err)
	}
	authRespEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive auth response: %v", err)
	}
	var authResp messages.AuthResponse
	if err := authRespEnv.UnmarshalPayload(&authResp); err != nil {
		t.Fatalf("unmarshal auth response: %v", err)
	}
	if !authResp.OK {
		t.Fatalf("gateway auth failed: %s", authResp.Error)
	}
	if authResp.UserID != userID {
		t.Fatalf("auth user = %q, want %q", authResp.UserID, userID)
	}

	// ---- 3/4: MsgEnterWorld, answered purely from the Redis registry -------
	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: dotnetMapID})
	if err := client.Send(enterEnv); err != nil {
		t.Fatalf("send enter world: %v", err)
	}
	enterRespEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive enter world response: %v", err)
	}
	var enterResp messages.EnterWorldResponse
	if err := enterRespEnv.UnmarshalPayload(&enterResp); err != nil {
		t.Fatalf("unmarshal enter world response: %v", err)
	}
	if enterResp.Error != "" {
		t.Fatalf("enter world error: %s", enterResp.Error)
	}
	// The whole point: the address the client is told to dial came out of the
	// registry the game server wrote, not out of this test.
	if enterResp.ServerAddr != listenAddr {
		t.Fatalf("enter world server addr = %q, want the self-registered %q",
			enterResp.ServerAddr, listenAddr)
	}
	if enterResp.JoinToken == "" {
		t.Fatal("enter world returned an empty join token")
	}
	client.Close()

	// ---- 5/6: dial the returned address and join ---------------------------
	gsClient, err := NewMockClient(enterResp.ServerAddr)
	if err != nil {
		t.Fatalf("dial the address the gateway handed back (%s): %v", enterResp.ServerAddr, err)
	}

	joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: enterResp.JoinToken})
	if err := gsClient.Send(joinEnv); err != nil {
		t.Fatalf("send join token: %v", err)
	}
	joinRespEnv, err := gsClient.Receive()
	if err != nil {
		t.Fatalf("receive join response: %v", err)
	}
	var joinResp messages.JoinTokenResponse
	if err := joinRespEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("game server rejected the gateway-minted join token: %s "+
			"(sid claim vs --server-id mismatch is the usual cause)", joinResp.Error)
	}
	if joinResp.UserID != userID {
		t.Fatalf("join user = %q, want %q", joinResp.UserID, userID)
	}

	// ---- 7/8: input -> snapshot -------------------------------------------
	inputEnv, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
		Tick: 1, MoveX: 1.0, MoveY: 0.0,
	})
	if err := gsClient.Send(inputEnv); err != nil {
		t.Fatalf("send input: %v", err)
	}

	state := mergeSnapshots(t, gsClient, 5)
	t.Logf("merged %d keyframes + %d deltas: tick=%d ack_tick=%d entities=%d",
		state.Keyframes, state.Deltas, state.Tick, state.AckTick, state.Len())

	var found bool
	for _, e := range state.Entities {
		if e.Type != "player" {
			continue
		}
		found = true
		if e.X <= 0 {
			t.Errorf("player X = %.2f after MoveX=1, want > 0", e.X)
		}
		break
	}
	if !found {
		t.Fatal("no player entity in the merged snapshot state")
	}
	if state.AckTick != 1 {
		t.Errorf("ack_tick = %d, want 1 (the only input sent)", state.AckTick)
	}

	// ---- 9: clean disconnect ----------------------------------------------
	// A client leaving must not take the server with it. The server holds the
	// entity for the reconnect window rather than dropping it, so the assertion
	// is that the process keeps serving: it still answers a fresh join, and it
	// is still registered in Redis (the heartbeat loop survived the drop).
	gsClient.Close()
	time.Sleep(500 * time.Millisecond)

	rejoin, err := NewMockClient(listenAddr)
	if err != nil {
		t.Fatalf("game server stopped accepting connections after a client left: %v", err)
	}
	defer rejoin.Close()

	token2, err := jwt.SignWithServer("selfreg-player-2", dotnetServerID, dotnetJoinTokenSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("sign second token: %v", err)
	}
	joinEnv2, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token2})
	if err := rejoin.Send(joinEnv2); err != nil {
		t.Fatalf("send join after disconnect: %v", err)
	}
	joinRespEnv2, err := rejoin.Receive()
	if err != nil {
		t.Fatalf("receive join response after disconnect: %v", err)
	}
	var joinResp2 messages.JoinTokenResponse
	if err := joinRespEnv2.UnmarshalPayload(&joinResp2); err != nil {
		t.Fatalf("unmarshal second join response: %v", err)
	}
	if !joinResp2.OK {
		t.Fatalf("join rejected after a clean disconnect: %s", joinResp2.Error)
	}

	servers, err := reg.FindByMapID(ctx, dotnetMapID)
	if err != nil {
		t.Fatalf("list registry after disconnect: %v", err)
	}
	if len(servers) == 0 {
		t.Error("game server dropped out of the registry after a client disconnected: " +
			"the heartbeat loop did not survive")
	}

	t.Log("PASS: self-registered game server serves the whole client flow through a Redis-backed gateway")
}

// startRedisBackedGateway starts the gateway in-process with the Redis session
// store and Redis server registry — the same redisstore types the production
// `--backend=redis` path wires up. Nothing is pre-registered: whatever the
// gateway finds, the game server put there.
func startRedisBackedGateway(t *testing.T, client redis.UniversalClient) (addr string, cleanup func()) {
	t.Helper()

	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelDebug}))
	sessionMgr := gwsession.NewSessionManager(redisstore.NewSessionStoreWithClient(client))
	registrySvc := gwregistry.NewRegistryService(redisstore.NewServerRegistryWithClient(client, 0))

	gw := gwserver.New(sessionMgr, registrySvc, dotnetJWTSecret, logger,
		gwserver.WithJoinTokenSecret(dotnetJoinTokenSecret))
	go gw.Run(":0")

	for i := 0; i < 100; i++ {
		if addr = gw.Addr(); addr != "" {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if addr == "" {
		t.Fatal("gateway did not bind")
	}
	t.Logf("redis-backed gateway listening on %s", addr)
	return addr, func() { gw.Shutdown() }
}

// reserveTCPPort returns a port that was free a moment ago. The listener is
// closed before returning, so this races with anything else on the box — which
// is acceptable in a test and unavoidable when a process must be told its own
// advertised address before it starts listening.
func reserveTCPPort(t *testing.T) int {
	t.Helper()
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("reserve port: %v", err)
	}
	port := l.Addr().(*net.TCPAddr).Port
	if err := l.Close(); err != nil {
		t.Fatalf("release reserved port: %v", err)
	}
	return port
}
