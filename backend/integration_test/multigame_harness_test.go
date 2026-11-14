//go:build integration

package integration

import (
	"context"
	"path/filepath"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"

	gwregistry "github.com/duycuong/rpg-mmo/gateway/registry"
	gwserver "github.com/duycuong/rpg-mmo/gateway/server"
	gwsession "github.com/duycuong/rpg-mmo/gateway/session"
)

// Shared harness for the ADR-33 multi-game e2e tests (legs 2-3): one in-process
// gateway admitting two games, a C# server per game on the SAME map id, and
// client tokens minted directly with shared/jwt (no Nakama).

const (
	// mgGame is the second, non-default game. Not reserved (gs/map/dungeon/other).
	mgGame = "arena"
	// mgArenaServerID carries the "<game>-" prefix the C# server requires of a
	// non-default game (GameServer/Games/ServerGame.cs).
	mgArenaServerID = "arena-gs-1"
	// mgCuvaraServerID is the default server id startDotnetGameServerRaw passes.
	mgCuvaraServerID = dotnetServerID
	// mgMapID is the map both games serve: routing must split on game, not map.
	mgMapID = "map_01"
	// mgArenaJoinSecret is arena's own join-token keyring, deliberately different
	// from dotnetJoinTokenSecret so a cross-game token cannot verify by accident.
	mgArenaJoinSecret = "arena-join-secret-for-multigame-e2e-0123"
	// mgGames is the gateway GAMES allow-list.
	mgGames = "cuvara," + mgGame
)

// mgArenaContentDir is the absolute path of the arena content tree. A non-default
// game will not boot without a game.json declaring it (GameManifest.cs).
func mgArenaContentDir(t *testing.T) string {
	t.Helper()
	dir, err := filepath.Abs(filepath.Join("testdata", "multigame", mgGame))
	if err != nil {
		t.Fatalf("arena content dir: %v", err)
	}
	return dir
}

// mgStartArenaServer launches a C# server for game arena on map_01. extraArgs go
// first so they win (Program.cs GetArg returns the first match), and the arena
// args are prepended to the shared defaults for the same reason.
func mgStartArenaServer(t *testing.T, extraArgs, extraEnv []string) (addr string, cleanup func()) {
	t.Helper()
	args := append([]string{}, extraArgs...)
	args = append(args,
		"--server-id", mgArenaServerID,
		"--map-id", mgMapID,
		"--game-id", mgGame,
		"--join-token-secret", mgArenaJoinSecret,
		"--content-dir", mgArenaContentDir(t),
	)
	return startDotnetGameServerWith(t, args, extraEnv)
}

// mgStartCuvaraServer launches a default-game C# server on map_01 with the
// default server id, join secret and content.
func mgStartCuvaraServer(t *testing.T, extraArgs, extraEnv []string) (addr string, cleanup func()) {
	t.Helper()
	args := append([]string{}, extraArgs...)
	args = append(args, "--map-id", mgMapID)
	return startDotnetGameServerWith(t, args, extraEnv)
}

// mgArenaServerInfo and mgCuvaraServerInfo are the registry entries for the two
// servers above.
func mgArenaServerInfo(addr string) storage.ServerInfo {
	return storage.ServerInfo{
		ServerID: mgArenaServerID, MapID: mgMapID, GameID: mgGame,
		Addr: addr, Transport: "kcp", Capacity: 100,
	}
}

func mgCuvaraServerInfo(addr string) storage.ServerInfo {
	return storage.ServerInfo{
		ServerID: mgCuvaraServerID, MapID: mgMapID, GameID: "cuvara",
		Addr: addr, Transport: "kcp", Capacity: 100,
	}
}

// mgStartGateway starts an in-process gateway admitting cuvara and arena, each
// with its own join-token keyring, and pre-registers every given server.
func mgStartGateway(t *testing.T, servers []storage.ServerInfo, opts ...gwserver.Option) (gwAddr string, cleanup func()) {
	t.Helper()

	reg := storage.NewMemoryServerRegistry()
	for _, s := range servers {
		if err := reg.Register(context.Background(), s); err != nil {
			t.Fatalf("register %s (game %s): %v", s.ServerID, s.GameID, err)
		}
	}
	games, err := gwsession.ParseGames(mgGames)
	if err != nil {
		t.Fatalf("parse games %q: %v", mgGames, err)
	}

	all := []gwserver.Option{
		gwserver.WithGames(games),
		gwserver.WithJoinTokenSecret(dotnetJoinTokenSecret),
		gwserver.WithGameJoinTokenSecret(mgGame, mgArenaJoinSecret),
	}
	all = append(all, opts...)
	gw := gwserver.New(
		gwsession.NewSessionManager(storage.NewMemorySessionStore(), "gw-mg"),
		gwregistry.NewRegistryService(reg),
		dotnetJWTSecret, logger.New("debug"), all...)

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
	t.Logf("multi-game gateway listening on %s (games %s)", addr, mgGames)
	return addr, gw.Shutdown
}

// mgClientToken mints the gateway (client) token for userID in gameID. An empty
// gameID, like "cuvara", produces a token with no gid claim.
func mgClientToken(t *testing.T, userID, gameID string) string {
	t.Helper()
	token, err := jwt.SignWithGame(userID, "", "", gameID, dotnetJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.SignWithGame(%q, game %q): %v", userID, gameID, err)
	}
	return token
}

// mgAuth dials the gateway, sends MsgAuth with token and returns the client and
// the auth response, whatever it says.
func mgAuth(t *testing.T, gwAddr, token string, enc messages.Encoding) (*MockClient, messages.AuthResponse) {
	t.Helper()
	c, err := NewGatewayClient(gwAddr)
	if err != nil {
		t.Fatalf("connect to gateway: %v", err)
	}
	env, _ := messages.NewEnvelopeAs(enc, messages.MsgAuth, messages.AuthRequest{Token: token})
	if err := c.Send(env); err != nil {
		c.Close()
		t.Fatalf("send auth: %v", err)
	}
	respEnv := readUntilType(t, c, messages.MsgAuthResp, "waiting for MsgAuthResp")
	var resp messages.AuthResponse
	if err := respEnv.UnmarshalPayload(&resp); err != nil {
		c.Close()
		t.Fatalf("unmarshal auth response: %v", err)
	}
	return c, resp
}

// mgDialAndAuthGame authenticates userID in gameID and requires success.
func mgDialAndAuthGame(t *testing.T, gwAddr, userID, gameID string, enc messages.Encoding) *MockClient {
	t.Helper()
	c, resp := mgAuth(t, gwAddr, mgClientToken(t, userID, gameID), enc)
	if !resp.OK {
		c.Close()
		t.Fatalf("auth %q in game %q rejected: %s", userID, gameID, resp.Error)
	}
	return c
}

// mgEnterWorld sends MsgEnterWorld for mapID and requires an assignment.
func mgEnterWorld(t *testing.T, c *MockClient, mapID string, enc messages.Encoding) messages.EnterWorldResponse {
	t.Helper()
	env, _ := messages.NewEnvelopeAs(enc, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: mapID})
	if err := c.Send(env); err != nil {
		t.Fatalf("send enter world: %v", err)
	}
	respEnv := readUntilType(t, c, messages.MsgEnterWorldResp, "waiting for MsgEnterWorldResp")
	var resp messages.EnterWorldResponse
	if err := respEnv.UnmarshalPayload(&resp); err != nil {
		t.Fatalf("unmarshal enter world: %v", err)
	}
	if resp.Error != "" {
		t.Fatalf("enter world %q rejected: %s", mapID, resp.Error)
	}
	return resp
}

// mgJoinClaims reads the join token's claims. Unverified on purpose: the test
// asserts what the gateway put in it; the game server verifies it for real.
func mgJoinClaims(t *testing.T, token string) jwt.Claims {
	t.Helper()
	claims, err := jwt.ParseUnverified(token)
	if err != nil {
		t.Fatalf("parse join token: %v", err)
	}
	return claims
}

// mgJoinExpectRefused presents token to the game server at addr on a fresh
// connection and returns the refusal reason; it fails the test if the join is
// accepted.
func mgJoinExpectRefused(t *testing.T, transport, addr, token string, enc messages.Encoding, who string) string {
	t.Helper()
	c, err := NewGameClientAdvertised(transport, addr)
	if err != nil {
		t.Fatalf("%s connect to game server %s: %v", who, addr, err)
	}
	defer c.Close()
	env, _ := messages.NewEnvelopeAs(enc, messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
	if err := c.Send(env); err != nil {
		t.Fatalf("%s send join: %v", who, err)
	}
	respEnv := readUntilType(t, c, messages.MsgJoinTokenResp, who+" waiting for MsgJoinTokenResp")
	var resp messages.JoinTokenResponse
	if err := respEnv.UnmarshalPayload(&resp); err != nil {
		t.Fatalf("%s unmarshal join: %v", who, err)
	}
	if resp.OK {
		t.Fatalf("%s: join accepted, want refused", who)
	}
	return resp.Error
}
