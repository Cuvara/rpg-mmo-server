//go:build integration

package integration

import (
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"

	gwserver "github.com/duycuong/rpg-mmo/gateway/server"
)

// ADR-33 scenario 3: the supersede key is (game, user), not user. Harness:
// multigame_harness_test.go; kick chain: duplicate_login_kick_e2e_test.go.

const (
	// mgsWatchWindow is how long each game connection must keep being served.
	mgsWatchWindow = 3 * time.Second
	// mgsMinSnapshots is well below one snapshot per tick over the window, but
	// far above zero: a kicked connection goes quiet.
	mgsMinSnapshots = 5
	// mgsProbeMap is a map no server serves. EnterWorld for it passes the
	// gateway session check and then fails assignment, without touching the
	// session's join-token jti (server.go updates it only on success).
	mgsProbeMap = "mgs_no_such_map"
	// mgsNoServer is the gateway's client-safe refusal for mgsProbeMap.
	mgsNoServer = "no server available for map"
)

// TestMultiGame_SameUserTwoGamesSupersedesNeither: one user logged in to cuvara
// and to arena at once keeps both sessions; no supersede event is published. A
// second cuvara login then does supersede the first cuvara session (positive
// control) while the arena session is untouched.
func TestMultiGame_SameUserTwoGamesSupersedesNeither(t *testing.T) {
	enc := messages.EncodingProto
	mr := miniredis.RunT(t)
	redisEnv := []string{"REDIS_ADDR=" + mr.Addr()}

	// Both servers run their Redis kick consumers on the same events:kick stream.
	cuvaraAddr, cuvaraCleanup := mgStartCuvaraServer(t, nil, redisEnv)
	defer cuvaraCleanup()
	arenaAddr, arenaCleanup := mgStartArenaServer(t, nil, redisEnv)
	defer arenaCleanup()

	kickStream := redisstore.NewEventStream(mr.Addr(), "", "gw-e2e", "gw-e2e-1")
	defer kickStream.Close()
	gwAddr, gwCleanup := mgStartGateway(t, []storage.ServerInfo{
		mgCuvaraServerInfo(cuvaraAddr),
		mgArenaServerInfo(arenaAddr),
	}, gwserver.WithKickStream(kickStream))
	defer gwCleanup()

	kickKey := constants.EventStreamPrefix + constants.KickEventStream
	const userID = "mg-two-games-player"

	// --- cuvara (no gid): log in, enter map_01, join the cuvara server. ---
	gwCuvara := mgDialAndAuthGame(t, gwAddr, userID, "", enc)
	defer gwCuvara.Close()
	cuvaraEnter := mgEnterWorld(t, gwCuvara, mgMapID, enc)
	if cuvaraEnter.ServerAddr != cuvaraAddr {
		t.Fatalf("cuvara enter world addr = %q, want %q", cuvaraEnter.ServerAddr, cuvaraAddr)
	}
	gsCuvara, err := NewGameClientFor(cuvaraEnter)
	if err != nil {
		t.Fatalf("connect to cuvara server: %v", err)
	}
	defer gsCuvara.Close()
	joinGameServer(t, gsCuvara, cuvaraEnter.JoinToken, enc, "cuvara")
	waitForSnapshot(t, gsCuvara, "cuvara")

	// --- arena (gid arena), same user, new gateway connection. ---
	gwArena := mgDialAndAuthGame(t, gwAddr, userID, mgGame, enc)
	defer gwArena.Close()
	arenaEnter := mgEnterWorld(t, gwArena, mgMapID, enc)
	if arenaEnter.ServerAddr != arenaAddr {
		t.Fatalf("arena enter world addr = %q, want %q", arenaEnter.ServerAddr, arenaAddr)
	}
	gsArena, err := NewGameClientFor(arenaEnter)
	if err != nil {
		t.Fatalf("connect to arena server: %v", err)
	}
	defer gsArena.Close()
	joinGameServer(t, gsArena, arenaEnter.JoinToken, enc, "arena")
	waitForSnapshot(t, gsArena, "arena")
	t.Log("same user is in both games")

	// --- Neither is superseded. ---
	mgsRequireServed(t, map[string]*MockClient{"cuvara game": gsCuvara, "arena game": gsArena})
	mgsProbeGateway(t, gwCuvara, enc, "cuvara gateway")
	mgsProbeGateway(t, gwArena, enc, "arena gateway")
	if n := mgsStreamLen(t, mr, kickKey); n != 0 {
		t.Fatalf("%s holds %d entries after a login in a second game, want 0", kickKey, n)
	}
	t.Log("no supersede across games: both game connections served, both gateway sessions live, kick stream empty")

	// --- Positive control: a second cuvara login supersedes the first one. ---
	gwCuvara2 := mgDialAndAuthGame(t, gwAddr, userID, "", enc)
	defer gwCuvara2.Close()

	kickEnv := readUntilType(t, gsCuvara, messages.MsgKick, "cuvara waiting for MsgKick")
	var kick messages.KickMessage
	if err := kickEnv.UnmarshalPayload(&kick); err != nil {
		t.Fatalf("unmarshal kick: %v", err)
	}
	if kick.Reason != "duplicate_login" {
		t.Errorf("cuvara kick reason = %q, want duplicate_login", kick.Reason)
	}
	readUntilType(t, gsCuvara, messages.MsgDisconnect, "cuvara waiting for MsgDisconnect")
	waitForClose(t, gsCuvara, "first cuvara game connection")

	entries, err := mr.Stream(kickKey)
	if err != nil {
		t.Fatalf("read %s: %v", kickKey, err)
	}
	if len(entries) != 1 {
		t.Fatalf("%s holds %d entries after a same-game re-login, want 1", kickKey, len(entries))
	}
	payload := mgsStreamField(entries[0], "payload")
	if !strings.Contains(payload, `"game_id":"cuvara"`) || !strings.Contains(payload, `"user_id":"`+userID+`"`) {
		t.Errorf("supersede event payload = %s, want game_id cuvara and user_id %s", payload, userID)
	}

	// The arena session survives the cuvara supersede.
	mgsRequireServed(t, map[string]*MockClient{"arena game": gsArena})
	mgsProbeGateway(t, gwArena, enc, "arena gateway after cuvara re-login")
	t.Log("PASS: supersede is keyed by (game, user): cross-game login kicks nothing, same-game re-login kicks only that game")
}

// --- helpers -----------------------------------------------------------------

// mgsRequireServed watches every connection in parallel for mgsWatchWindow and
// fails if any receives MsgKick/MsgDisconnect, errors, or gets too few snapshots.
func mgsRequireServed(t *testing.T, conns map[string]*MockClient) {
	t.Helper()
	type result struct {
		who   string
		snaps int
		err   error
	}
	done := make(chan result, len(conns))
	for who, c := range conns {
		go func(who string, c *MockClient) {
			snaps, err := mgsWatch(c, mgsWatchWindow)
			done <- result{who, snaps, err}
		}(who, c)
	}
	for range conns {
		r := <-done
		if r.err != nil {
			t.Fatalf("%s: %v", r.who, r.err)
		}
		if r.snaps < mgsMinSnapshots {
			t.Fatalf("%s: %d snapshots in %s, want >= %d", r.who, r.snaps, mgsWatchWindow, mgsMinSnapshots)
		}
		t.Logf("%s: still served, %d snapshots in %s", r.who, r.snaps, mgsWatchWindow)
	}
}

// mgsWatch reads c for d and counts snapshots. It runs off the test goroutine,
// so it returns errors instead of calling t.Fatal.
func mgsWatch(c *MockClient, d time.Duration) (int, error) {
	snaps := 0
	end := time.Now().Add(d)
	for time.Now().Before(end) {
		env, err := c.Receive()
		if err != nil {
			return snaps, fmt.Errorf("receive after %d snapshots: %w", snaps, err)
		}
		switch env.Type {
		case messages.MsgKick, messages.MsgDisconnect:
			return snaps, fmt.Errorf("received %v: the session was superseded", env.Type)
		case messages.MsgSnapshot:
			snaps++
		}
	}
	return snaps, nil
}

// mgsProbeGateway proves the gateway still serves c's session: an EnterWorld
// for an unserved map must pass the session check and come back as the
// assignment refusal, not "session expired"/"not authenticated", and no kick
// frame may precede it.
func mgsProbeGateway(t *testing.T, c *MockClient, enc messages.Encoding, who string) {
	t.Helper()
	env, _ := messages.NewEnvelopeAs(enc, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: mgsProbeMap})
	if err := c.Send(env); err != nil {
		t.Fatalf("%s: send probe: %v", who, err)
	}
	deadline := time.Now().Add(15 * time.Second)
	for time.Now().Before(deadline) {
		got, err := c.Receive()
		if err != nil {
			t.Fatalf("%s: probe response: %v", who, err)
		}
		switch got.Type {
		case messages.MsgKick, messages.MsgDisconnect:
			t.Fatalf("%s: received %v: the session was superseded", who, got.Type)
		case messages.MsgEnterWorldResp:
			var resp messages.EnterWorldResponse
			if err := got.UnmarshalPayload(&resp); err != nil {
				t.Fatalf("%s: unmarshal probe response: %v", who, err)
			}
			if resp.Error != mgsNoServer {
				t.Fatalf("%s: probe error = %q, want %q (session check passed)", who, resp.Error, mgsNoServer)
			}
			return
		}
	}
	t.Fatalf("%s: no probe response within 15s", who)
}

// mgsStreamLen is XLEN on key; a missing stream counts as empty.
func mgsStreamLen(t *testing.T, mr *miniredis.Miniredis, key string) int {
	t.Helper()
	entries, err := mr.Stream(key)
	if err != nil {
		t.Fatalf("read %s: %v", key, err)
	}
	return len(entries)
}

// mgsStreamField returns the value of field in a stream entry's flat
// field/value list.
func mgsStreamField(e miniredis.StreamEntry, field string) string {
	for i := 0; i+1 < len(e.Values); i += 2 {
		if e.Values[i] == field {
			return e.Values[i+1]
		}
	}
	return ""
}
