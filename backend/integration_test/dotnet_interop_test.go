//go:build integration

package integration

import (
	"bytes"
	"bufio"
	"context"
	"fmt"
	"log/slog"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"

	gwregistry "github.com/duycuong/rpg-mmo/gateway/registry"
	gwserver "github.com/duycuong/rpg-mmo/gateway/server"
	gwsession "github.com/duycuong/rpg-mmo/gateway/session"
)

const dotnetJWTSecret = "test-secret-key-for-integration"
const dotnetJoinTokenSecret = "test-join-secret-32chars-minimum"
const dotnetServerID = "test-dotnet-gs"
const dotnetMapID = "map_test"

// mergeSnapshots reads snapshots from the client and merges them into reconstructed
// world state, exactly as the Unity client must: the game server sends a full keyframe
// on join and then deltas carrying only what changed, so no single snapshot is the
// whole world.
//
// It stops after `want` snapshots or when the client stops producing them, and fails
// the test if none arrived at all.
func mergeSnapshots(t *testing.T, c *MockClient, want int) *messages.SnapshotState {
	t.Helper()

	state := messages.NewSnapshotState()
	got := 0
	failures := 0
	for attempts := 0; attempts < want*3 && got < want; attempts++ {
		env, err := c.Receive()
		if err != nil {
			t.Logf("receive attempt %d: %v", attempts, err)
			// Each Receive burns a 5s deadline; bail rather than blow the test timeout.
			if failures++; failures >= 2 {
				break
			}
			continue
		}
		if env.Type != messages.MsgSnapshot {
			continue
		}
		var snap messages.SnapshotMessage
		if err := env.UnmarshalPayload(&snap); err != nil {
			t.Fatalf("unmarshal snapshot: %v", err)
		}
		state.Apply(snap)
		got++
	}
	if got == 0 {
		t.Fatal("did not receive a snapshot from C# gameserver within timeout")
	}
	// The first snapshot on a connection is always a keyframe; without it the client
	// would be applying deltas onto nothing.
	if state.Keyframes == 0 {
		t.Error("no keyframe received: client cannot seed its world state")
	}
	return state
}

// gameServerBinary builds the C# game server once per test binary and returns the
// path to the produced native apphost.
//
// The build is deliberately separated from the run, and the run launches the built
// assembly as `dotnet GameServer.dll` rather than `dotnet run`. `dotnet run` spawns
// the game server as a *grandchild* and hands it its own stdout/stderr: killing
// `dotnet run` leaves the real server alive holding the pipe, so `go test` hangs at
// exit with `Test I/O incomplete 30s after exiting` even though every test passed.
// `dotnet <dll>` makes the server the direct child that Kill actually kills.
//
// The native apphost next to the dll is deliberately NOT used: it resolves the
// runtime through DOTNET_ROOT / a registered install location and dies with
// "You must install .NET to run this application" whenever the SDK lives somewhere
// non-default. The muxer already knows where its own runtime is.
var (
	gameServerBuildOnce sync.Once
	gameServerDotnet    string
	gameServerDll       string
	gameServerBuildErr  error
	gameServerSkip      string
)

func buildDotnetGameServer() {
	dotnetPath, err := exec.LookPath("dotnet")
	if err != nil {
		home, _ := os.UserHomeDir()
		dotnetPath = filepath.Join(home, ".dotnet", "dotnet")
		if _, statErr := os.Stat(dotnetPath); statErr != nil {
			gameServerSkip = "dotnet not found, skipping .NET interop tests"
			return
		}
	}

	projectDir := filepath.Join("..", "gameserver-dotnet", "GameServer")

	build := exec.Command(dotnetPath, "build", projectDir, "-c", "Release", "-v", "q")
	build.Env = append(os.Environ(), "DOTNET_CLI_TELEMETRY_OPTOUT=1")
	if out, err := build.CombinedOutput(); err != nil {
		gameServerBuildErr = fmt.Errorf("dotnet build failed: %w\n%s", err, string(out))
		return
	}

	dll := filepath.Join(projectDir, "bin", "Release", "net10.0", "GameServer.dll")
	if _, err := os.Stat(dll); err != nil {
		gameServerBuildErr = fmt.Errorf("game server assembly not found at %s after build: %w", dll, err)
		return
	}
	gameServerDotnet = dotnetPath
	gameServerDll = dll
}

// startDotnetGameServer launches the C# gameserver as a subprocess and returns
// the actual listening address parsed from stdout. The caller must call the
// returned cleanup function to kill the process.
func startDotnetGameServer(t *testing.T) (addr string, cleanup func()) {
	t.Helper()

	gameServerBuildOnce.Do(buildDotnetGameServer)
	if gameServerSkip != "" {
		t.Skip(gameServerSkip)
	}
	if gameServerBuildErr != nil {
		t.Fatal(gameServerBuildErr)
	}

	cmd := exec.Command(gameServerDotnet, gameServerDll,
		"--addr", "127.0.0.1:0",
		"--map-id", dotnetMapID,
		"--server-id", dotnetServerID,
		"--jwt-secret", dotnetJWTSecret,
		// Metrics off: the tests only exercise the game wire, and a fixed port
		// (default :9101) would collide with any locally running server and with
		// the next test's server in this same suite.
		"--metrics-addr", "",
	)
	cmd.Env = append(os.Environ(),
		"DOTNET_CLI_TELEMETRY_OPTOUT=1",
		"JOIN_TOKEN_SECRET="+dotnetJoinTokenSecret,
	)

	// Merge stderr into the same pipe so nothing is written to the test process's
	// own stderr after the test finishes.
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatalf("stdout pipe: %v", err)
	}
	cmd.Stderr = cmd.Stdout

	if err := cmd.Start(); err != nil {
		t.Fatalf("start dotnet gameserver: %v", err)
	}

	// Parse the actual listen address from stdout.
	// The .NET console logger uses a multi-line format:
	//   info: GameServer.Server.GameServerHost[0]
	//         Game server listening on 127.0.0.1:XXXXX (mode=map, ...)
	var (
		addrCh   = make(chan string, 1)
		scanDone = make(chan struct{})
		logMu    sync.Mutex
		logging  = true
	)
	go func() {
		defer close(scanDone)
		scanner := bufio.NewScanner(stdout)
		sent := false
		for scanner.Scan() {
			line := scanner.Text()
			// t.Logf after the test returns panics, so stop logging once cleanup starts.
			logMu.Lock()
			if logging {
				t.Logf("[dotnet] %s", line)
			}
			logMu.Unlock()

			if !sent {
				if idx := strings.Index(line, "Game server listening on "); idx >= 0 {
					rest := line[idx+len("Game server listening on "):]
					// The address is the next token before " ("
					if spIdx := strings.Index(rest, " "); spIdx > 0 {
						addrCh <- rest[:spIdx]
					} else {
						addrCh <- strings.TrimSpace(rest)
					}
					sent = true
				}
			}
		}
	}()

	cleanupFn := func() {
		logMu.Lock()
		logging = false
		logMu.Unlock()

		if cmd.Process != nil {
			_ = cmd.Process.Kill()
			_ = cmd.Wait() // reaps the process and closes the stdout pipe
		}
		// Wait for the scanner to drain, so no goroutine outlives the test.
		select {
		case <-scanDone:
		case <-time.After(5 * time.Second):
		}
	}

	select {
	case addr = <-addrCh:
		t.Logf("dotnet gameserver listening on %s", addr)
	case <-scanDone:
		// stdout closed before the listen line: the server died on startup.
		// Fail now with whatever it logged instead of stalling for the timeout.
		cleanupFn()
		t.Fatal("dotnet gameserver exited during startup (see [dotnet] log lines above)")
	case <-time.After(60 * time.Second):
		cleanupFn()
		t.Fatal("timed out waiting for dotnet gameserver to start")
	}

	return addr, cleanupFn
}

// startGatewayForDotnet starts the Go gateway in-process, pre-registers the
// C# gameserver in the registry, and returns the gateway address.
func startGatewayForDotnet(t *testing.T, gsAddr string) (gwAddr string, cleanup func()) {
	t.Helper()

	sessionStore := storage.NewMemorySessionStore()
	reg := storage.NewMemoryServerRegistry()
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelDebug}))

	// Pre-register the C# gameserver in the registry so the gateway can find it
	err := reg.Register(context.Background(), storage.ServerInfo{
		ServerID: dotnetServerID,
		MapID:    dotnetMapID,
		Addr:     gsAddr,
		Capacity: 100,
	})
	if err != nil {
		t.Fatalf("register dotnet gameserver: %v", err)
	}

	sessionMgr := gwsession.NewSessionManager(sessionStore)
	registrySvc := gwregistry.NewRegistryService(reg)
	gw := gwserver.New(sessionMgr, registrySvc, dotnetJWTSecret, logger,
		gwserver.WithJoinTokenSecret(dotnetJoinTokenSecret))

	go gw.Run(":0")

	// Wait for gateway to bind
	var addr string
	for i := 0; i < 50; i++ {
		addr = gw.Addr()
		if addr != "" {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if addr == "" {
		t.Fatal("gateway did not start")
	}
	t.Logf("gateway listening on %s", addr)

	return addr, func() { gw.Shutdown() }
}

// TestDotnetInterop_FullFlow tests the complete client -> gateway -> C# gameserver flow.
// TestDotnetInterop_FullFlow walks the whole handshake against the real C# game
// server in BOTH wire encodings.
//
// This is the test that proves the two independently generated implementations
// of wire.proto — protoc-gen-go on the Go side, protoc's C# generator on the
// server — actually agree. Unit tests on either side can only prove each is
// self-consistent.
func TestDotnetInterop_FullFlow(t *testing.T) {
	for _, enc := range []messages.Encoding{messages.EncodingJSON, messages.EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) { runDotnetFullFlow(t, enc) })
	}
}

func runDotnetFullFlow(t *testing.T, enc messages.Encoding) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}

	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()

	gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
	defer gwCleanup()

	// --- Step a/b: Client -> Gateway: MsgAuth { JWT } ---
	gwClient, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("connect to gateway: %v", err)
	}

	userID := "dotnet-player-" + enc.String()
	token, err := jwt.Sign(userID, dotnetJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}

	authEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgAuth, messages.AuthRequest{Token: token})
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
	if err := authRespEnv.UnmarshalPayload(&authResp); err != nil {
		t.Fatalf("unmarshal auth response: %v", err)
	}
	if !authResp.OK {
		t.Fatalf("auth failed: %s", authResp.Error)
	}
	if authResp.UserID != userID {
		t.Fatalf("auth user mismatch: want %s, got %s", userID, authResp.UserID)
	}
	t.Log("gateway auth OK")

	// --- Step c/d: Client -> Gateway: MsgEnterWorld { MapID } ---
	enterEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: dotnetMapID})
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
	if err := enterRespEnv.UnmarshalPayload(&enterResp); err != nil {
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

	// --- Step e/f: Client -> C# GameServer: MsgJoinToken { Token } ---
	gsClient, err := NewMockClient(enterResp.ServerAddr)
	if err != nil {
		t.Fatalf("connect to C# game server: %v", err)
	}
	defer gsClient.Close()

	joinEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgJoinToken, messages.JoinTokenRequest{Token: enterResp.JoinToken})
	if err := gsClient.Send(joinEnv); err != nil {
		t.Fatalf("send join token: %v", err)
	}

	joinRespEnv, err := gsClient.Receive()
	if err != nil {
		t.Fatalf("receive join response: %v", err)
	}
	if joinRespEnv.Type != messages.MsgJoinTokenResp {
		t.Fatalf("expected MsgJoinTokenResp, got type %d", joinRespEnv.Type)
	}
	var joinResp messages.JoinTokenResponse
	if err := joinRespEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("join rejected: %s", joinResp.Error)
	}
	if joinResp.UserID != userID {
		t.Fatalf("join user mismatch: want %s, got %s", userID, joinResp.UserID)
	}
	// The reply must come back in the encoding the client spoke. If the server
	// ever answered in its own preferred encoding instead, a half-migrated fleet
	// would break here rather than in production.
	if joinRespEnv.Enc != enc {
		t.Fatalf("join response encoding = %v, want %v (server must answer in the client's encoding)", joinRespEnv.Enc, enc)
	}
	t.Log("C# gameserver join accepted OK")

	// --- Step g: Client -> C# GameServer: MsgInput { Tick:1, MoveX:1, MoveY:0 } ---
	inputEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgInput, messages.InputMessage{
		Tick:  1,
		MoveX: 1.0,
		MoveY: 0.0,
	})
	if err := gsClient.Send(inputEnv); err != nil {
		t.Fatalf("send input: %v", err)
	}

	// --- Step h/i: Wait for MsgSnapshot and verify entity position ---
	//
	// Snapshots are delta-encoded (keyframe on join, then only what changed), so a
	// single snapshot is not the world — merge the stream the way a real client does.
	state := mergeSnapshots(t, gsClient, 5)
	t.Logf("merged %d keyframes + %d deltas: tick=%d ack_tick=%d entities=%d",
		state.Keyframes, state.Deltas, state.Tick, state.AckTick, state.Len())

	// Verify the player entity is in the reconstructed state
	found := false
	for _, e := range state.Entities {
		if e.Type == "player" {
			found = true
			t.Logf("player entity: id=%s pos=(%.2f, %.2f) hp=%d/%d", e.ID, e.X, e.Y, e.HP, e.MaxHP)
			// After MoveX=1, X should be > 0 (exact value depends on tick rate and speed)
			if e.X <= 0 {
				t.Errorf("expected player X > 0 after move, got %.2f", e.X)
			}
			break
		}
	}
	if !found {
		t.Fatal("no player entity found in snapshot")
	}
	// The server must acknowledge the input tick it accepted, otherwise the client
	// has nothing to reconcile its prediction against.
	if state.AckTick != 1 {
		t.Errorf("ack_tick = %d, want 1 (the only input sent)", state.AckTick)
	}

	t.Logf("PASS: full flow Go gateway <-> C# gameserver interop over %s", enc)
}

// TestDotnetInterop_InvalidJWT verifies that the C# gameserver rejects an invalid JWT.
func TestDotnetInterop_InvalidJWT(t *testing.T) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}

	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()

	client, err := NewMockClient(gsAddr)
	if err != nil {
		t.Fatalf("connect to C# game server: %v", err)
	}
	defer client.Close()

	// Send a join token with an invalid JWT
	joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{
		Token: "invalid.jwt.token",
	})
	if err := client.Send(joinEnv); err != nil {
		t.Fatalf("send invalid join token: %v", err)
	}

	respEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive response: %v", err)
	}
	if respEnv.Type != messages.MsgJoinTokenResp {
		t.Fatalf("expected MsgJoinTokenResp, got type %d", respEnv.Type)
	}
	var joinResp messages.JoinTokenResponse
	if err := respEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if joinResp.OK {
		t.Fatal("expected join to be rejected for invalid JWT, but it was accepted")
	}
	t.Logf("invalid JWT correctly rejected: %s", joinResp.Error)
}

// TestDotnetInterop_WrongServerID verifies that the C# gameserver rejects a
// join token signed for a different server ID.
func TestDotnetInterop_WrongServerID(t *testing.T) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}

	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()

	client, err := NewMockClient(gsAddr)
	if err != nil {
		t.Fatalf("connect to C# game server: %v", err)
	}
	defer client.Close()

	// Sign a join token for a DIFFERENT server ID
	wrongToken, err := jwt.SignWithServer("player-wrong", "wrong-server-id", dotnetJoinTokenSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.SignWithServer: %v", err)
	}

	joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{
		Token: wrongToken,
	})
	if err := client.Send(joinEnv); err != nil {
		t.Fatalf("send join token: %v", err)
	}

	respEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive response: %v", err)
	}
	var joinResp messages.JoinTokenResponse
	if err := respEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if joinResp.OK {
		t.Fatal("expected join to be rejected for wrong server ID, but it was accepted")
	}
	t.Logf("wrong server ID correctly rejected: %s", joinResp.Error)
}

// TestDotnetInterop_MultipleClients verifies that multiple clients can connect
// to the C# gameserver and each gets their own snapshots.
func TestDotnetInterop_MultipleClients(t *testing.T) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}

	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()

	// Connect two clients
	connectAndJoin := func(playerID string) *MockClient {
		t.Helper()
		client, err := NewMockClient(gsAddr)
		if err != nil {
			t.Fatalf("connect client %s: %v", playerID, err)
		}

		token, err := jwt.SignWithServer(playerID, dotnetServerID, dotnetJoinTokenSecret, 5*time.Minute)
		if err != nil {
			t.Fatalf("sign token for %s: %v", playerID, err)
		}

		joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
		if err := client.Send(joinEnv); err != nil {
			t.Fatalf("send join %s: %v", playerID, err)
		}

		respEnv, err := client.Receive()
		if err != nil {
			t.Fatalf("receive join resp %s: %v", playerID, err)
		}
		var joinResp messages.JoinTokenResponse
		if err := respEnv.UnmarshalPayload(&joinResp); err != nil {
			t.Fatalf("unmarshal join resp %s: %v", playerID, err)
		}
		if !joinResp.OK {
			t.Fatalf("join rejected for %s: %s", playerID, joinResp.Error)
		}
		return client
	}

	client1 := connectAndJoin("multi-player-1")
	defer client1.Close()
	client2 := connectAndJoin("multi-player-2")
	defer client2.Close()

	// Send different inputs
	input1, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 1, MoveY: 0})
	input2, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 0, MoveY: 1})
	client1.Send(input1)
	client2.Send(input2)

	// Both should receive snapshots, and each merges its own delta stream.
	state1 := mergeSnapshots(t, client1, 5)
	state2 := mergeSnapshots(t, client2, 5)

	// Both reconstructed worlds should contain both players (they see each other)
	if state1.Len() < 2 {
		t.Logf("client1 state has %d entities (expected >= 2)", state1.Len())
	}
	if state2.Len() < 2 {
		t.Logf("client2 state has %d entities (expected >= 2)", state2.Len())
	}

	t.Logf("client1 state: tick=%d entities=%d keyframes=%d deltas=%d",
		state1.Tick, state1.Len(), state1.Keyframes, state1.Deltas)
	t.Logf("client2 state: tick=%d entities=%d keyframes=%d deltas=%d",
		state2.Tick, state2.Len(), state2.Keyframes, state2.Deltas)
	t.Log("PASS: multiple clients receive snapshots from C# gameserver")
}

// TestDotnetInterop_ClientDisconnect verifies that the C# gameserver handles
// client disconnect cleanly without crashing.
func TestDotnetInterop_ClientDisconnect(t *testing.T) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}

	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()

	// Connect, join, then immediately disconnect
	client, err := NewMockClient(gsAddr)
	if err != nil {
		t.Fatalf("connect: %v", err)
	}

	token, err := jwt.SignWithServer("disconnect-player", dotnetServerID, dotnetJoinTokenSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("sign token: %v", err)
	}

	joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
	if err := client.Send(joinEnv); err != nil {
		t.Fatalf("send join: %v", err)
	}

	respEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive join response: %v", err)
	}
	var joinResp messages.JoinTokenResponse
	respEnv.UnmarshalPayload(&joinResp)
	if !joinResp.OK {
		t.Fatalf("join rejected: %s", joinResp.Error)
	}

	// Close the connection abruptly
	client.Close()

	// Wait briefly, then verify the server is still accepting connections
	time.Sleep(500 * time.Millisecond)

	client2, err := NewMockClient(gsAddr)
	if err != nil {
		t.Fatalf("server stopped accepting connections after client disconnect: %v", err)
	}
	defer client2.Close()

	// New client can still join
	token2, _ := jwt.SignWithServer("disconnect-player-2", dotnetServerID, dotnetJoinTokenSecret, 5*time.Minute)
	joinEnv2, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token2})
	if err := client2.Send(joinEnv2); err != nil {
		t.Fatalf("send join after disconnect: %v", err)
	}

	respEnv2, err := client2.Receive()
	if err != nil {
		t.Fatalf("receive join response after disconnect: %v", err)
	}
	var joinResp2 messages.JoinTokenResponse
	respEnv2.UnmarshalPayload(&joinResp2)
	if !joinResp2.OK {
		t.Fatalf("join rejected after disconnect: %s", joinResp2.Error)
	}
	t.Log("PASS: server handles client disconnect cleanly")
}

// TestDotnetInterop_GatewayInvalidJWT verifies that the Go gateway rejects
// an invalid JWT at the auth step.
func TestDotnetInterop_GatewayInvalidJWT(t *testing.T) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}

	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()

	gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
	defer gwCleanup()

	gwClient, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("connect to gateway: %v", err)
	}
	defer gwClient.Close()

	// Send auth with invalid JWT
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: "bogus.invalid.jwt"})
	if err := gwClient.Send(authEnv); err != nil {
		t.Fatalf("send auth: %v", err)
	}

	authRespEnv, err := gwClient.Receive()
	if err != nil {
		t.Fatalf("receive auth response: %v", err)
	}
	var authResp messages.AuthResponse
	if err := authRespEnv.UnmarshalPayload(&authResp); err != nil {
		t.Fatalf("unmarshal auth response: %v", err)
	}
	if authResp.OK {
		t.Fatal("expected auth to be rejected for invalid JWT")
	}
	t.Logf("gateway correctly rejected invalid JWT: %s", authResp.Error)

	// Verify that trying to enter world without auth also fails
	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: dotnetMapID})
	if err := gwClient.Send(enterEnv); err != nil {
		// Connection may be closed, which is also acceptable
		t.Logf("send enter world after failed auth: %v (expected)", err)
		return
	}

	// If we can still send, we should get an error response
	enterRespEnv, err := gwClient.Receive()
	if err != nil {
		t.Logf("receive enter world after failed auth: %v (connection closed, expected)", err)
		return
	}

	var enterResp messages.EnterWorldResponse
	enterRespEnv.UnmarshalPayload(&enterResp)
	if enterResp.Error == "" {
		t.Fatal("expected error for enter world without auth")
	}
	t.Logf("gateway correctly rejected enter world without auth: %s", enterResp.Error)
}

// TestDotnetInterop_WireProtocolCompat is a focused test that verifies the
// JSON wire format is compatible between Go and C# by sending and receiving
// messages directly to the C# gameserver.
func TestDotnetInterop_WireProtocolCompat(t *testing.T) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}

	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()

	client, err := NewMockClient(gsAddr)
	if err != nil {
		t.Fatalf("connect: %v", err)
	}
	defer client.Close()

	// Sign a valid join token
	token, err := jwt.SignWithServer("wire-test-player", dotnetServerID, dotnetJoinTokenSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("sign token: %v", err)
	}

	// Test 1: Verify JoinToken envelope round-trip
	joinEnv, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
	if err := client.Send(joinEnv); err != nil {
		t.Fatalf("send join: %v", err)
	}

	respEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive join response: %v", err)
	}

	// Verify envelope type field matches
	if respEnv.Type != messages.MsgJoinTokenResp {
		t.Fatalf("envelope type mismatch: want %d (MsgJoinTokenResp), got %d", messages.MsgJoinTokenResp, respEnv.Type)
	}

	// Verify payload JSON fields
	var joinResp messages.JoinTokenResponse
	if err := respEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("payload unmarshal failed (field name mismatch?): %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("join failed: %s", joinResp.Error)
	}

	// Test 2: Verify Input -> Snapshot round-trip
	inputEnv, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
		Tick:  42,
		MoveX: 0.5,
		MoveY: -0.5,
	})
	if err := client.Send(inputEnv); err != nil {
		t.Fatalf("send input: %v", err)
	}

	// Wait for snapshots and merge them: this also exercises the delta fields
	// (full / removed / ack_tick) across the Go<->C# boundary.
	state := mergeSnapshots(t, client, 5)
	t.Logf("wire compat OK: tick=%d ack_tick=%d entities=%d keyframes=%d deltas=%d",
		state.Tick, state.AckTick, state.Len(), state.Keyframes, state.Deltas)

	// Verify entity fields can be read
	for _, e := range state.Entities {
		_ = fmt.Sprintf("id=%s type=%s x=%.2f y=%.2f hp=%d max_hp=%d",
			e.ID, e.Type, e.X, e.Y, e.HP, e.MaxHP)
	}
	if state.AckTick != 42 {
		t.Errorf("ack_tick = %d, want 42 (the input tick that was sent)", state.AckTick)
	}
	t.Log("PASS: wire protocol fully compatible between Go and C#")
}

// TestDotnetInterop_MixedEncodingsOnOneServer is the migration test.
//
// A JSON client and a Protobuf client join the SAME running C# server at the
// same time and both must be served correctly. This is the state a fleet is
// actually in mid-rollout — gateway, game servers and Unity clients all deploy
// independently — and it is what the first-byte encoding sniff exists to make
// safe. If the server ever latched one encoding globally instead of per
// connection, this test fails where a single-encoding test would not.
//
// It also measures the snapshot payload saving on the real wire, so the
// bandwidth claim in docs/BENCHMARK.md is checked by CI rather than trusted.
func TestDotnetInterop_MixedEncodingsOnOneServer(t *testing.T) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}

	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()

	// Payload bytes of the first keyframe each client receives. Both clients see
	// the same world, so the two numbers are directly comparable.
	keyframeBytes := map[messages.Encoding]int{}
	var protoKeyframe []byte
	var protoDeltaWithEntities []byte
	var userIDForInterning string

	for _, enc := range []messages.Encoding{messages.EncodingJSON, messages.EncodingProto} {
		client, err := NewMockClient(gsAddr)
		if err != nil {
			t.Fatalf("%s: connect: %v", enc, err)
		}
		defer client.Close()

		userID := "mixed-" + enc.String()
		if enc == messages.EncodingProto {
			userIDForInterning = userID
		}
		token, err := jwt.SignWithServer(userID, dotnetServerID, dotnetJoinTokenSecret, 5*time.Minute)
		if err != nil {
			t.Fatalf("%s: sign token: %v", enc, err)
		}

		joinEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
		if err := client.Send(joinEnv); err != nil {
			t.Fatalf("%s: send join: %v", enc, err)
		}

		respEnv, err := client.Receive()
		if err != nil {
			t.Fatalf("%s: receive join response: %v", enc, err)
		}
		if respEnv.Enc != enc {
			t.Fatalf("%s: server replied in %v — it must answer in the client's encoding", enc, respEnv.Enc)
		}
		var joinResp messages.JoinTokenResponse
		if err := respEnv.UnmarshalPayload(&joinResp); err != nil {
			t.Fatalf("%s: unmarshal join response: %v", enc, err)
		}
		if !joinResp.OK {
			t.Fatalf("%s: join rejected: %s", enc, joinResp.Error)
		}

		inputEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgInput, messages.InputMessage{Tick: 7, MoveX: 1})
		if err := client.Send(inputEnv); err != nil {
			t.Fatalf("%s: send input: %v", enc, err)
		}

		// First keyframe carrying at least one entity.
		deadline := time.Now().Add(10 * time.Second)
		for time.Now().Before(deadline) {
			env, err := client.Receive()
			if err != nil {
				t.Fatalf("%s: receive snapshot: %v", enc, err)
			}
			if env.Type != messages.MsgSnapshot || env.Enc != enc {
				continue
			}
			var snap messages.SnapshotMessage
			if err := env.UnmarshalPayload(&snap); err != nil {
				t.Fatalf("%s: unmarshal snapshot: %v", enc, err)
			}
			if !snap.Full || len(snap.Entities) == 0 {
				continue
			}
			keyframeBytes[enc] = len(env.Payload)
			if enc == messages.EncodingProto {
				protoKeyframe = append([]byte(nil), env.Payload...)
				// Keep reading past the keyframe for a delta that actually carries
				// entities — that is where interning shows up.
				protoDeltaWithEntities = firstDeltaWithEntities(t, client, enc)
			}
			break
		}
		if keyframeBytes[enc] == 0 {
			t.Fatalf("%s: no keyframe with entities arrived", enc)
		}
	}

	// The C# server must actually be using the entity-type enum, not just
	// agreeing with Go about what the string means. A protobuf keyframe carrying
	// only known entity types should contain no literal type string at all, so
	// this checks the bytes rather than the decoded value — decoding would look
	// identical either way, which is exactly how a silently-unused optimisation
	// survives.
	// Interning: after the keyframe introduces the bindings, later protobuf
	// deltas must stop carrying entity ids. Asserted on the BYTES rather than the
	// decoded value, because a decoded snapshot looks identical whether or not
	// the ids were actually elided — which is how an optimisation silently stops
	// working. mergeSnapshots already applied the stream, so a desync would have
	// surfaced as an error there.
	if protoDeltaWithEntities != nil && bytes.Contains(protoDeltaWithEntities, []byte(userIDForInterning)) {
		t.Errorf("protobuf delta still carries the entity id %q; interning is not in effect:\n%q",
			userIDForInterning, protoDeltaWithEntities)
	}

	if bytes.Contains(protoKeyframe, []byte("player")) {
		t.Errorf("protobuf keyframe still contains the literal type string %q; "+
			"the C# server is falling back to type_name instead of the enum:\n%q",
			"player", protoKeyframe)
	}

	jsonBytes, protoBytes := keyframeBytes[messages.EncodingJSON], keyframeBytes[messages.EncodingProto]
	saving := 100 * float64(jsonBytes-protoBytes) / float64(jsonBytes)
	t.Logf("keyframe payload: json=%dB proto=%dB saving=%.1f%%", jsonBytes, protoBytes, saving)

	if protoBytes >= jsonBytes {
		t.Errorf("protobuf keyframe (%dB) is not smaller than JSON (%dB)", protoBytes, jsonBytes)
	}
}

// firstDeltaWithEntities reads on until a non-keyframe snapshot carrying at
// least one entity arrives, and returns its raw payload. Returns nil if none
// appears — the caller treats that as "nothing to assert" rather than a failure,
// because a perfectly still world legitimately produces empty deltas.
func firstDeltaWithEntities(t *testing.T, client *MockClient, enc messages.Encoding) []byte {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		env, err := client.Receive()
		if err != nil {
			return nil
		}
		if env.Type != messages.MsgSnapshot || env.Enc != enc {
			continue
		}
		var snap messages.SnapshotMessage
		if err := env.UnmarshalPayload(&snap); err != nil {
			return nil
		}
		if !snap.Full && len(snap.Entities) > 0 {
			return append([]byte(nil), env.Payload...)
		}
	}
	return nil
}
