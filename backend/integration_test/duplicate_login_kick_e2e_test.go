//go:build integration

package integration

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"

	gwregistry "github.com/duycuong/rpg-mmo/gateway/registry"
	gwserver "github.com/duycuong/rpg-mmo/gateway/server"
	gwsession "github.com/duycuong/rpg-mmo/gateway/session"
)

// TestDotnetInterop_DuplicateLoginKick is the live end-to-end proof of the
// cross-instance duplicate-login kick (the gap ADR-17 recorded after #211
// deleted the unwired machinery): the direction that had nothing was
// gateway -> game server, and this walks the whole rebuilt chain with nothing
// mocked but the Redis process (miniredis, in-process — the
// redis_event_e2e_test.go pattern):
//
//	client A joins the C# game server through the real gateway handshake
//	client B authenticates as the same user on a new connection
//	gateway XADDs session_superseded (jti of A's join token) into events:kick
//	C# RedisKickConsumer (group gs:{server_id}) delivers it to KickPlayerAsync
//	A's connection gets MsgKick + MsgDisconnect (reason=duplicate_login), closes
//	A's entity is released with NO reconnect hold, players_kicked increments
//	B enters the world and joins cleanly, receiving a fresh keyframe
func TestDotnetInterop_DuplicateLoginKick(t *testing.T) {
	mr := miniredis.RunT(t)
	t.Logf("miniredis listening on %s", mr.Addr())

	// A real /status listener so the test can read the kick counter the way an
	// operator would. HttpListener cannot bind :0, so reserve a port the
	// bind-close-reuse way; the race window is tolerated in this one test.
	statusPort := freeTCPPort(t)

	gsAddr, gsCleanup := startDotnetGameServerWith(t,
		[]string{"--metrics-addr", fmt.Sprintf("127.0.0.1:%d", statusPort)},
		[]string{"REDIS_ADDR=" + mr.Addr()})
	defer gsCleanup()

	// Gateway with the production kick publisher: the SAME redisstore
	// EventStream implementation main.go wires via server.WithKickStream.
	kickStream := redisstore.NewEventStream(mr.Addr(), "", "gw-e2e", "gw-e2e-1")
	defer kickStream.Close()
	gwAddr, gwCleanup := startGatewayWithKickStream(t, gsAddr, kickStream)
	defer gwCleanup()

	const userID = "e2e-dup-login"
	enc := messages.EncodingProto

	// --- Client A: full handshake, join the game server, see a snapshot. ---
	gwA := dialAndAuth(t, gwAddr, userID, enc)
	defer gwA.Close()
	enterA := enterWorldE2E(t, gwA, enc)
	gsA, err := NewMockClient(enterA.ServerAddr)
	if err != nil {
		t.Fatalf("A connect to game server: %v", err)
	}
	defer gsA.Close()
	joinGameServer(t, gsA, enterA.JoinToken, enc, "A")
	waitForSnapshot(t, gsA, "A")
	t.Log("client A is in the world")

	// --- Client B: same user, new login. The gateway must publish the kick. ---
	gwB := dialAndAuth(t, gwAddr, userID, enc)
	defer gwB.Close()

	// --- A's game connection is evicted: MsgKick, then MsgDisconnect, both
	// reason=duplicate_login (the frames must never disagree), then EOF. ---
	kickEnv := readUntilType(t, gsA, messages.MsgKick, "A waiting for MsgKick")
	var kick messages.KickMessage
	if err := kickEnv.UnmarshalPayload(&kick); err != nil {
		t.Fatalf("unmarshal kick: %v", err)
	}
	if kick.Reason != "duplicate_login" {
		t.Errorf("kick reason = %q, want duplicate_login", kick.Reason)
	}
	discEnv := readUntilType(t, gsA, messages.MsgDisconnect, "A waiting for MsgDisconnect")
	var disc messages.DisconnectMessage
	if err := discEnv.UnmarshalPayload(&disc); err != nil {
		t.Fatalf("unmarshal disconnect: %v", err)
	}
	if disc.Reason != kick.Reason {
		t.Errorf("reason mismatch: kick %q, disconnect %q", kick.Reason, disc.Reason)
	}
	waitForClose(t, gsA, "A's game connection")
	t.Log("client A was kicked with duplicate_login and its connection closed")

	// --- Observability: players_kicked incremented, consumer reports redis. ---
	status := waitForKickCounter(t, statusPort, 1)
	if status.KickConsumer != "redis" {
		t.Errorf("kick_consumer = %q, want redis", status.KickConsumer)
	}

	// --- Client B joins cleanly and gets a fresh keyframe: the entity was
	// released (no reconnect hold blocking, no stale connection owning it). ---
	enterB := enterWorldE2E(t, gwB, enc)
	gsB, err := NewMockClient(enterB.ServerAddr)
	if err != nil {
		t.Fatalf("B connect to game server: %v", err)
	}
	defer gsB.Close()
	joinGameServer(t, gsB, enterB.JoinToken, enc, "B")
	snap := waitForSnapshot(t, gsB, "B")
	state := messages.NewSnapshotState()
	if err := state.Apply(snap); err != nil {
		t.Fatalf("apply B keyframe: %v", err)
	}
	if _, ok := state.Entities[userID]; !ok {
		t.Errorf("B's keyframe does not contain its own entity %q (entities: %d)",
			userID, state.Len())
	}
	t.Logf("PASS: newest login owns the user; old connection kicked, %d kick(s) counted",
		status.PlayersKicked)
}

// --- helpers -----------------------------------------------------------------

// startGatewayWithKickStream is startGatewayForDotnet plus the kick publisher —
// the exact option set cmd/gateway/main.go passes (WithKickStream).
func startGatewayWithKickStream(t *testing.T, gsAddr string, kickStream storage.EventStream) (string, func()) {
	t.Helper()

	sessionStore := storage.NewMemorySessionStore()
	reg := storage.NewMemoryServerRegistry()
	if err := reg.Register(context.Background(), storage.ServerInfo{
		ServerID: dotnetServerID,
		MapID:    dotnetMapID,
		Addr:     gsAddr,
		Capacity: 100,
	}); err != nil {
		t.Fatalf("register dotnet gameserver: %v", err)
	}

	gw := gwserver.New(
		gwsession.NewSessionManager(sessionStore, "gw-e2e"),
		gwregistry.NewRegistryService(reg),
		dotnetJWTSecret, logger.New("debug"),
		gwserver.WithJoinTokenSecret(dotnetJoinTokenSecret),
		gwserver.WithKickStream(kickStream))

	go gw.Run(":0")
	var addr string
	for i := 0; i < 50; i++ {
		if addr = gw.Addr(); addr != "" {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}
	if addr == "" {
		t.Fatal("gateway did not start")
	}
	t.Logf("gateway listening on %s", addr)
	return addr, gw.Shutdown
}

func dialAndAuth(t *testing.T, gwAddr, userID string, enc messages.Encoding) *MockClient {
	t.Helper()
	c, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("connect to gateway: %v", err)
	}
	token, err := jwt.Sign(userID, dotnetJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}
	env, _ := messages.NewEnvelopeAs(enc, messages.MsgAuth, messages.AuthRequest{Token: token})
	if err := c.Send(env); err != nil {
		t.Fatalf("send auth: %v", err)
	}
	respEnv, err := c.Receive()
	if err != nil {
		t.Fatalf("auth response: %v", err)
	}
	var resp messages.AuthResponse
	if err := respEnv.UnmarshalPayload(&resp); err != nil {
		t.Fatalf("unmarshal auth response: %v", err)
	}
	if !resp.OK {
		t.Fatalf("auth rejected: %s", resp.Error)
	}
	return c
}

func enterWorldE2E(t *testing.T, c *MockClient, enc messages.Encoding) messages.EnterWorldResponse {
	t.Helper()
	env, _ := messages.NewEnvelopeAs(enc, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: dotnetMapID})
	if err := c.Send(env); err != nil {
		t.Fatalf("send enter world: %v", err)
	}
	respEnv, err := c.Receive()
	if err != nil {
		t.Fatalf("enter world response: %v", err)
	}
	var resp messages.EnterWorldResponse
	if err := respEnv.UnmarshalPayload(&resp); err != nil {
		t.Fatalf("unmarshal enter world: %v", err)
	}
	if resp.Error != "" {
		t.Fatalf("enter world rejected: %s", resp.Error)
	}
	return resp
}

func joinGameServer(t *testing.T, c *MockClient, joinToken string, enc messages.Encoding, who string) {
	t.Helper()
	env, _ := messages.NewEnvelopeAs(enc, messages.MsgJoinToken, messages.JoinTokenRequest{Token: joinToken})
	if err := c.Send(env); err != nil {
		t.Fatalf("%s send join: %v", who, err)
	}
	respEnv, err := c.Receive()
	if err != nil {
		t.Fatalf("%s join response: %v", who, err)
	}
	var resp messages.JoinTokenResponse
	if err := respEnv.UnmarshalPayload(&resp); err != nil {
		t.Fatalf("%s unmarshal join: %v", who, err)
	}
	if !resp.OK {
		t.Fatalf("%s join rejected: %s", who, resp.Error)
	}
}

// waitForSnapshot reads until the first MsgSnapshot and returns it decoded.
func waitForSnapshot(t *testing.T, c *MockClient, who string) messages.SnapshotMessage {
	t.Helper()
	env := readUntilType(t, c, messages.MsgSnapshot, who+" waiting for a snapshot")
	var snap messages.SnapshotMessage
	if err := env.UnmarshalPayload(&snap); err != nil {
		t.Fatalf("%s unmarshal snapshot: %v", who, err)
	}
	return snap
}

// readUntilType drains frames (snapshots, heartbeats) until the wanted type
// arrives. The per-Receive deadline (5s inside MockClient) bounds each read and
// the overall deadline bounds the scan.
func readUntilType(t *testing.T, c *MockClient, want messages.MsgType, what string) messages.Envelope {
	t.Helper()
	deadline := time.Now().Add(15 * time.Second)
	for time.Now().Before(deadline) {
		env, err := c.Receive()
		if err != nil {
			t.Fatalf("%s: %v", what, err)
		}
		if env.Type == want {
			return env
		}
	}
	t.Fatalf("%s: not received within 15s", what)
	return messages.Envelope{}
}

// waitForClose asserts the peer actually closes the connection (EOF/reset)
// once the eviction frames are flushed.
func waitForClose(t *testing.T, c *MockClient, what string) {
	t.Helper()
	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		_, err := c.Receive()
		if err == nil {
			continue // late snapshot still in flight
		}
		if errors.Is(err, io.EOF) || errors.Is(err, net.ErrClosed) || !isTimeout(err) {
			return // closed (EOF, reset, ...) — anything but a bare timeout
		}
	}
	t.Fatalf("%s was never closed by the server", what)
}

func isTimeout(err error) bool {
	var ne net.Error
	return errors.As(err, &ne) && ne.Timeout()
}

// dotnetStatus is the slice of /status this test reads.
type dotnetStatus struct {
	PlayersKicked int64  `json:"players_kicked"`
	KickConsumer  string `json:"kick_consumer"`
}

// waitForKickCounter polls /status until players_kicked reaches want.
func waitForKickCounter(t *testing.T, port int, want int64) dotnetStatus {
	t.Helper()
	url := fmt.Sprintf("http://127.0.0.1:%d/status", port)
	deadline := time.Now().Add(10 * time.Second)
	var last dotnetStatus
	for time.Now().Before(deadline) {
		resp, err := http.Get(url)
		if err == nil {
			body, rerr := io.ReadAll(resp.Body)
			resp.Body.Close()
			if rerr == nil && json.Unmarshal(body, &last) == nil && last.PlayersKicked >= want {
				return last
			}
		}
		time.Sleep(100 * time.Millisecond)
	}
	t.Fatalf("players_kicked never reached %d (last status: %+v)", want, last)
	return last
}

// freeTCPPort reserves an ephemeral port the bind-close-reuse way. Racy by
// nature; used only for the /status listener, which cannot bind :0.
func freeTCPPort(t *testing.T) int {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("reserve port: %v", err)
	}
	port := ln.Addr().(*net.TCPAddr).Port
	ln.Close()
	return port
}
