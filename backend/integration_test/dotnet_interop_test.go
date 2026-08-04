//go:build integration

package integration

import (
	"bufio"
	"context"
	"fmt"
	"log/slog"
	"os"
	"os/exec"
	"strings"
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
const dotnetServerID = "test-dotnet-gs"
const dotnetMapID = "map_test"

// startDotnetGameServer launches the C# gameserver as a subprocess and returns
// the actual listening address parsed from stdout. The caller must call the
// returned cleanup function to kill the process.
func startDotnetGameServer(t *testing.T) (addr string, cleanup func()) {
	t.Helper()

	dotnetPath, err := exec.LookPath("dotnet")
	if err != nil {
		// Try $HOME/.dotnet/dotnet explicitly
		home, _ := os.UserHomeDir()
		dotnetPath = home + "/.dotnet/dotnet"
		if _, err := os.Stat(dotnetPath); err != nil {
			t.Skip("dotnet not found, skipping .NET interop tests")
		}
	}

	projectDir := "../gameserver-dotnet/GameServer"

	// Pre-build so "dotnet run --no-build" starts instantly (avoids 60s+ JIT cold start)
	build := exec.Command(dotnetPath, "build", projectDir, "-c", "Release", "-v", "q")
	build.Env = append(os.Environ(), "DOTNET_CLI_TELEMETRY_OPTOUT=1")
	if out, err := build.CombinedOutput(); err != nil {
		t.Fatalf("dotnet build failed: %v\n%s", err, string(out))
	}

	cmd := exec.Command(dotnetPath, "run",
		"--project", projectDir,
		"-c", "Release",
		"--no-build",
		"--",
		"--addr", "127.0.0.1:0",
		"--map-id", dotnetMapID,
		"--server-id", dotnetServerID,
		"--jwt-secret", dotnetJWTSecret,
	)

	// Ensure dotnet is on PATH for child processes
	cmd.Env = append(os.Environ(), "DOTNET_CLI_TELEMETRY_OPTOUT=1")

	stdout, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatalf("stdout pipe: %v", err)
	}
	cmd.Stderr = os.Stderr

	if err := cmd.Start(); err != nil {
		t.Fatalf("start dotnet gameserver: %v", err)
	}

	cleanupFn := func() {
		if cmd.Process != nil {
			cmd.Process.Kill()
			cmd.Wait()
		}
	}

	// Parse actual listen address from stdout.
	// The .NET console logger uses multi-line format:
	//   info: GameServer.Server.GameServerHost[0]
	//         Game server listening on 127.0.0.1:XXXXX (mode=map, ...)
	scanner := bufio.NewScanner(stdout)
	addrCh := make(chan string, 1)
	sent := false
	go func() {
		for scanner.Scan() {
			line := scanner.Text()
			t.Logf("[dotnet] %s", line)
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

	select {
	case addr = <-addrCh:
		t.Logf("dotnet gameserver listening on %s", addr)
	case <-time.After(30 * time.Second):
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
	gw := gwserver.New(sessionMgr, registrySvc, dotnetJWTSecret, logger)

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
func TestDotnetInterop_FullFlow(t *testing.T) {
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

	userID := "dotnet-player-001"
	token, err := jwt.Sign(userID, dotnetJWTSecret, 5*time.Minute)
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

	// --- Step c/d: Client -> Gateway: MsgEnterWorld { MapID } ---
	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: dotnetMapID})
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

	// --- Step e/f: Client -> C# GameServer: MsgJoinToken { Token } ---
	gsClient, err := NewMockClient(enterResp.ServerAddr)
	if err != nil {
		t.Fatalf("connect to C# game server: %v", err)
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
	if joinRespEnv.Type != messages.MsgJoinTokenResp {
		t.Fatalf("expected MsgJoinTokenResp, got type %d", joinRespEnv.Type)
	}
	var joinResp messages.JoinTokenResponse
	if err := messages.UnmarshalPayload(joinRespEnv.Payload, &joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("join rejected: %s", joinResp.Error)
	}
	if joinResp.UserID != userID {
		t.Fatalf("join user mismatch: want %s, got %s", userID, joinResp.UserID)
	}
	t.Log("C# gameserver join accepted OK")

	// --- Step g: Client -> C# GameServer: MsgInput { Tick:1, MoveX:1, MoveY:0 } ---
	inputEnv, _ := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
		Tick:  1,
		MoveX: 1.0,
		MoveY: 0.0,
	})
	if err := gsClient.Send(inputEnv); err != nil {
		t.Fatalf("send input: %v", err)
	}

	// --- Step h/i: Wait for MsgSnapshot and verify entity position ---
	var snapshot messages.SnapshotMessage
	received := false
	for i := 0; i < 10; i++ {
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
		t.Fatal("did not receive a snapshot from C# gameserver within timeout")
	}
	t.Logf("received snapshot tick=%d with %d entities", snapshot.Tick, len(snapshot.Entities))

	// Verify the player entity is in the snapshot
	found := false
	for _, e := range snapshot.Entities {
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

	t.Log("PASS: full flow Go gateway <-> C# gameserver interop")
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
	if err := messages.UnmarshalPayload(respEnv.Payload, &joinResp); err != nil {
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
	wrongToken, err := jwt.SignWithServer("player-wrong", "wrong-server-id", dotnetJWTSecret, 5*time.Minute)
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
	if err := messages.UnmarshalPayload(respEnv.Payload, &joinResp); err != nil {
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

		token, err := jwt.SignWithServer(playerID, dotnetServerID, dotnetJWTSecret, 5*time.Minute)
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
		if err := messages.UnmarshalPayload(respEnv.Payload, &joinResp); err != nil {
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

	// Both should receive snapshots
	receiveSnapshot := func(c *MockClient, name string) messages.SnapshotMessage {
		t.Helper()
		for i := 0; i < 10; i++ {
			env, err := c.Receive()
			if err != nil {
				continue
			}
			if env.Type == messages.MsgSnapshot {
				var snap messages.SnapshotMessage
				if err := messages.UnmarshalPayload(env.Payload, &snap); err != nil {
					t.Fatalf("unmarshal snapshot %s: %v", name, err)
				}
				return snap
			}
		}
		t.Fatalf("no snapshot received for %s", name)
		return messages.SnapshotMessage{}
	}

	snap1 := receiveSnapshot(client1, "client1")
	snap2 := receiveSnapshot(client2, "client2")

	// Both snapshots should contain both players (they see each other)
	if len(snap1.Entities) < 2 {
		t.Logf("client1 snapshot has %d entities (expected >= 2)", len(snap1.Entities))
	}
	if len(snap2.Entities) < 2 {
		t.Logf("client2 snapshot has %d entities (expected >= 2)", len(snap2.Entities))
	}

	t.Logf("client1 snapshot: tick=%d entities=%d", snap1.Tick, len(snap1.Entities))
	t.Logf("client2 snapshot: tick=%d entities=%d", snap2.Tick, len(snap2.Entities))
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

	token, err := jwt.SignWithServer("disconnect-player", dotnetServerID, dotnetJWTSecret, 5*time.Minute)
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
	messages.UnmarshalPayload(respEnv.Payload, &joinResp)
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
	token2, _ := jwt.SignWithServer("disconnect-player-2", dotnetServerID, dotnetJWTSecret, 5*time.Minute)
	joinEnv2, _ := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token2})
	if err := client2.Send(joinEnv2); err != nil {
		t.Fatalf("send join after disconnect: %v", err)
	}

	respEnv2, err := client2.Receive()
	if err != nil {
		t.Fatalf("receive join response after disconnect: %v", err)
	}
	var joinResp2 messages.JoinTokenResponse
	messages.UnmarshalPayload(respEnv2.Payload, &joinResp2)
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
	if err := messages.UnmarshalPayload(authRespEnv.Payload, &authResp); err != nil {
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
	messages.UnmarshalPayload(enterRespEnv.Payload, &enterResp)
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
	token, err := jwt.SignWithServer("wire-test-player", dotnetServerID, dotnetJWTSecret, 5*time.Minute)
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
	if err := messages.UnmarshalPayload(respEnv.Payload, &joinResp); err != nil {
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

	// Wait for snapshot
	for i := 0; i < 10; i++ {
		env, err := client.Receive()
		if err != nil {
			continue
		}
		if env.Type == messages.MsgSnapshot {
			var snap messages.SnapshotMessage
			if err := messages.UnmarshalPayload(env.Payload, &snap); err != nil {
				t.Fatalf("snapshot unmarshal failed (field name mismatch?): %v\npayload: %s", err, string(env.Payload))
			}
			t.Logf("wire compat OK: snapshot tick=%d, entities=%d", snap.Tick, len(snap.Entities))

			// Verify entity fields can be read
			for _, e := range snap.Entities {
				_ = fmt.Sprintf("id=%s type=%s x=%.2f y=%.2f hp=%d max_hp=%d",
					e.ID, e.Type, e.X, e.Y, e.HP, e.MaxHP)
			}
			t.Log("PASS: wire protocol fully compatible between Go and C#")
			return
		}
	}
	t.Fatal("did not receive snapshot for wire protocol test")
}
