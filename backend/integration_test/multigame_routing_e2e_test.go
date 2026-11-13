//go:build integration

package integration

import (
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// ADR-33 legs 2-3 end to end: the gateway composes (game, map_id) from the
// authenticated gid, mints the join token with that game's keyring and gid, and
// the C# server refuses a token for another game. Harness: multigame_harness_test.go.

// TestMultiGame_DefaultGameFlowUnchanged: a token with no gid still authenticates,
// is routed to the cuvara server and gets a join token with no gid, on a gateway
// that admits a second game.
func TestMultiGame_DefaultGameFlowUnchanged(t *testing.T) {
	enc := messages.EncodingProto

	cuvaraAddr, cuvaraCleanup := mgStartCuvaraServer(t, nil, nil)
	defer cuvaraCleanup()
	gwAddr, gwCleanup := mgStartGateway(t, []storage.ServerInfo{mgCuvaraServerInfo(cuvaraAddr)})
	defer gwCleanup()

	const userID = "mg-default-player"
	token := mgClientToken(t, userID, "")
	if gid := mgJoinClaims(t, token).GameID; gid != "" {
		t.Fatalf("client token gid = %q, want absent", gid)
	}
	c, resp := mgAuth(t, gwAddr, token, enc)
	defer c.Close()
	if !resp.OK {
		t.Fatalf("auth without gid rejected: %s", resp.Error)
	}

	enter := mgEnterWorld(t, c, mgMapID, enc)
	if enter.ServerAddr != cuvaraAddr {
		t.Errorf("enter world addr = %q, want the cuvara server %q", enter.ServerAddr, cuvaraAddr)
	}
	claims := mgJoinClaims(t, enter.JoinToken)
	if claims.GameID != "" {
		t.Errorf("join token gid = %q, want absent for the default game", claims.GameID)
	}
	if claims.ServerID != mgCuvaraServerID {
		t.Errorf("join token sid = %q, want %q", claims.ServerID, mgCuvaraServerID)
	}

	gs, err := NewGameClientFor(enter)
	if err != nil {
		t.Fatalf("connect to cuvara server: %v", err)
	}
	defer gs.Close()
	joinGameServer(t, gs, enter.JoinToken, enc, "default")
	waitForSnapshot(t, gs, "default")
	t.Log("PASS: default game (no gid) flow unchanged")
}

// TestMultiGame_SecondGameRoutedToItsOwnServer: with cuvara and arena servers both
// registered on map_01, an arena token is routed only to the arena server with an
// arena join token, and a cuvara token on the same gateway still goes to cuvara.
func TestMultiGame_SecondGameRoutedToItsOwnServer(t *testing.T) {
	enc := messages.EncodingProto

	cuvaraAddr, cuvaraCleanup := mgStartCuvaraServer(t, nil, nil)
	defer cuvaraCleanup()
	arenaAddr, arenaCleanup := mgStartArenaServer(t, nil, nil)
	defer arenaCleanup()
	gwAddr, gwCleanup := mgStartGateway(t, []storage.ServerInfo{
		mgCuvaraServerInfo(cuvaraAddr),
		mgArenaServerInfo(arenaAddr),
	})
	defer gwCleanup()

	// --- arena ---
	arena := mgDialAndAuthGame(t, gwAddr, "mg-arena-player", mgGame, enc)
	defer arena.Close()
	arenaEnter := mgEnterWorld(t, arena, mgMapID, enc)
	if arenaEnter.ServerAddr != arenaAddr {
		t.Fatalf("arena enter world addr = %q, want the arena server %q (cuvara is %q)",
			arenaEnter.ServerAddr, arenaAddr, cuvaraAddr)
	}
	arenaClaims := mgJoinClaims(t, arenaEnter.JoinToken)
	if arenaClaims.GameID != mgGame {
		t.Errorf("arena join token gid = %q, want %q", arenaClaims.GameID, mgGame)
	}
	if arenaClaims.ServerID != mgArenaServerID {
		t.Errorf("arena join token sid = %q, want %q", arenaClaims.ServerID, mgArenaServerID)
	}
	arenaGS, err := NewGameClientFor(arenaEnter)
	if err != nil {
		t.Fatalf("connect to arena server: %v", err)
	}
	defer arenaGS.Close()
	joinGameServer(t, arenaGS, arenaEnter.JoinToken, enc, "arena")
	waitForSnapshot(t, arenaGS, "arena")

	// --- cuvara, same gateway, same map id ---
	cuvara := mgDialAndAuthGame(t, gwAddr, "mg-cuvara-player", "", enc)
	defer cuvara.Close()
	cuvaraEnter := mgEnterWorld(t, cuvara, mgMapID, enc)
	if cuvaraEnter.ServerAddr != cuvaraAddr {
		t.Fatalf("cuvara enter world addr = %q, want the cuvara server %q (arena is %q)",
			cuvaraEnter.ServerAddr, cuvaraAddr, arenaAddr)
	}
	cuvaraClaims := mgJoinClaims(t, cuvaraEnter.JoinToken)
	if cuvaraClaims.GameID != "" || cuvaraClaims.ServerID != mgCuvaraServerID {
		t.Errorf("cuvara join token gid=%q sid=%q, want gid absent and sid %q",
			cuvaraClaims.GameID, cuvaraClaims.ServerID, mgCuvaraServerID)
	}
	cuvaraGS, err := NewGameClientFor(cuvaraEnter)
	if err != nil {
		t.Fatalf("connect to cuvara server: %v", err)
	}
	defer cuvaraGS.Close()
	joinGameServer(t, cuvaraGS, cuvaraEnter.JoinToken, enc, "cuvara")
	waitForSnapshot(t, cuvaraGS, "cuvara")
	t.Log("PASS: same map id routed per game: arena -> arena server, cuvara -> cuvara server")
}

// TestMultiGame_CrossGameJoinTokenRefused: a join token for one game never
// admits a player to another game's server, and an unlisted game is refused at
// gateway auth.
func TestMultiGame_CrossGameJoinTokenRefused(t *testing.T) {
	enc := messages.EncodingProto

	cuvaraAddr, cuvaraCleanup := mgStartCuvaraServer(t, nil, nil)
	defer cuvaraCleanup()
	arenaAddr, arenaCleanup := mgStartArenaServer(t, nil, nil)
	defer arenaCleanup()
	gwAddr, gwCleanup := mgStartGateway(t, []storage.ServerInfo{
		mgCuvaraServerInfo(cuvaraAddr),
		mgArenaServerInfo(arenaAddr),
	})
	defer gwCleanup()

	// (i) The gateway-issued arena join token presented to the cuvara server: it
	// is signed with the arena keyring and names the arena sid, so it fails
	// before the gid check; only the refusal itself is asserted.
	arena := mgDialAndAuthGame(t, gwAddr, "mg-cross-arena", mgGame, enc)
	defer arena.Close()
	arenaEnter := mgEnterWorld(t, arena, mgMapID, enc)
	reason := mgJoinExpectRefused(t, arenaEnter.Transport, cuvaraAddr, arenaEnter.JoinToken, enc,
		"arena token at cuvara server")
	t.Logf("(i) arena join token refused by the cuvara server: %q", reason)

	// (ii) Right secret, right sid, wrong game: only the gid check can refuse
	// it, and the server checks sid before gid (GameServer.cs Step 3/3a).
	forged, err := jwt.SignWithGame("mg-cross-forged", mgCuvaraServerID, "", mgGame,
		dotnetJoinTokenSecret, 30*time.Second)
	if err != nil {
		t.Fatalf("sign gid=%s join token: %v", mgGame, err)
	}
	const wantReason = "Token is for a different game" // GameServer.ReasonDifferentGame
	if reason := mgJoinExpectRefused(t, "kcp", cuvaraAddr, forged, enc,
		"gid=arena token at cuvara server"); reason != wantReason {
		t.Errorf("(ii) refusal = %q, want %q", reason, wantReason)
	}

	// (iii) A gid outside GAMES is refused at gateway auth.
	c, resp := mgAuth(t, gwAddr, mgClientToken(t, "mg-cross-zeta", "zeta"), enc)
	defer c.Close()
	if resp.OK {
		t.Fatal("(iii) auth with gid=zeta accepted, want game not configured")
	}
	const wantAuthErr = "game not configured" // gateway/server/server.go handleAuth
	if resp.Error != wantAuthErr {
		t.Errorf("(iii) auth error = %q, want %q", resp.Error, wantAuthErr)
	}
	t.Log("PASS: cross-game join tokens refused by the game server; unlisted game refused at auth")
}
