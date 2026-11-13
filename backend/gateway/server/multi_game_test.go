package server

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"net"
	"sync/atomic"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/ratelimit"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/testutil"
)

func configuredTestGames(t *testing.T, raw string) session.Games {
	t.Helper()
	games, err := session.ParseGames(raw)
	if err != nil {
		t.Fatal(err)
	}
	return games
}

func authenticateGame(t *testing.T, conn net.Conn, game, user string) messages.AuthResponse {
	t.Helper()
	token, err := jwt.SignWithGame(user, "", "character-1", game, testSecret, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	env, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	if err != nil {
		t.Fatal(err)
	}
	sendEnvelope(t, conn, env)
	resp := readEnvelope(t, conn)
	if resp.Type != messages.MsgAuthResp {
		t.Fatalf("auth response type = %v", resp.Type)
	}
	var auth messages.AuthResponse
	if err := resp.UnmarshalPayload(&auth); err != nil {
		t.Fatal(err)
	}
	return auth
}

func loginGame(t *testing.T, gw *Gateway, game, user string, inWorld bool) net.Conn {
	t.Helper()
	conn := dialGateway(t, gw)
	t.Cleanup(func() { _ = conn.Close() })
	if auth := authenticateGame(t, conn, game, user); !auth.OK {
		t.Fatalf("auth %s/%s: %s", game, user, auth.Error)
	}
	if inWorld {
		if resp := enterWorld(t, conn, "map_forest"); resp.Error != "" {
			t.Fatalf("enter world %s/%s: %s", game, user, resp.Error)
		}
	}
	return conn
}

func assertGatewayKick(t *testing.T, conn net.Conn) {
	t.Helper()
	for _, want := range []messages.MsgType{messages.MsgKick, messages.MsgDisconnect} {
		env := readEnvelope(t, conn)
		if env.Type != want {
			t.Fatalf("eviction frame = %v, want %v", env.Type, want)
		}
		var payload messages.DisconnectMessage
		if err := env.UnmarshalPayload(&payload); err != nil {
			t.Fatal(err)
		}
		if payload.Reason != KickReasonDuplicateLogin {
			t.Fatalf("eviction reason = %q", payload.Reason)
		}
	}
}

// A round trip proves the socket remains usable and has no queued eviction.
func assertGatewayAlive(t *testing.T, conn net.Conn) {
	t.Helper()
	ping, err := messages.NewEnvelope(messages.MsgPing, messages.PingMessage{Timestamp: 123})
	if err != nil {
		t.Fatal(err)
	}
	sendEnvelope(t, conn, ping)
	if env := readEnvelope(t, conn); env.Type != messages.MsgPong {
		t.Fatalf("live socket answered %v, want pong", env.Type)
	}
}

func TestGatewayGameSessionKeys(t *testing.T) {
	for _, tc := range []struct{ name, claim, key string }{
		{"absent gid", "", "session:u1"},
		{"default gid", "cuvara", "session:u1"},
		{"configured gid", "foo", "session:foo:u1"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := startKickTestGateway(t, WithGames(configuredTestGames(t, "cuvara,foo")))
			loginGame(t, r.gw, tc.claim, "u1", false)
			if _, err := r.store.Get(context.Background(), tc.key); err != nil {
				t.Fatalf("missing %s: %v", tc.key, err)
			}
			cc := r.gw.findUserConn(tc.claim, "u1")
			if cc == nil || cc.Game() != constants.GameOrDefault(tc.claim) || cc.CharacterID() != "character-1" {
				t.Fatalf("verified identity not latched for %q", tc.claim)
			}
		})
	}
}

func TestGatewaySameInstanceGamesStayIndependent(t *testing.T) {
	r := startKickTestGateway(t, WithGames(configuredTestGames(t, "cuvara,foo")))
	ctx := context.Background()
	defaultConn := loginGame(t, r.gw, constants.DefaultGameID, "u1", true)
	before, err := r.store.Get(ctx, "session:u1")
	if err != nil {
		t.Fatal(err)
	}
	defaultCC := r.gw.findUserConn(constants.DefaultGameID, "u1")
	fooConn := loginGame(t, r.gw, "foo", "u1", true)
	fooCC := r.gw.findUserConn("foo", "u1")
	if defaultCC == nil || fooCC == nil || defaultCC == fooCC {
		t.Fatal("both games must have independent tracked connections")
	}
	assertGatewayAlive(t, defaultConn)
	assertGatewayAlive(t, fooConn)
	r.expectNoEvent(t)
	r.expectNoGatewayKickEvent(t)
	after, err := r.store.Get(ctx, "session:u1")
	if err != nil || !bytes.Equal(before, after) {
		t.Fatalf("foo login changed default session: %s -> %s (%v)", before, after, err)
	}
	fooJTI := fooCC.JoinTokenJTI()
	loginGame(t, r.gw, "foo", "u1", false)
	assertGatewayKick(t, fooConn)
	ev := r.expectEvent(t)
	if ev.GameID != "foo" || ev.JTI != fooJTI || fooJTI == "" {
		t.Fatalf("foo supersede = %+v", ev)
	}
	if r.gw.findUserConn(constants.DefaultGameID, "u1") != defaultCC || r.gw.findUserConn("foo", "u1") == fooCC {
		t.Fatal("duplicate foo login replaced the wrong tracking entry")
	}
	assertGatewayAlive(t, defaultConn)
	after, err = r.store.Get(ctx, "session:u1")
	if err != nil || !bytes.Equal(before, after) {
		t.Fatalf("foo eviction changed default session: %v", err)
	}
}

func TestGatewayReauthAnotherGameRetiresPreviousTracking(t *testing.T) {
	r := startKickTestGateway(t, WithGames(configuredTestGames(t, "cuvara,foo")))
	conn := loginGame(t, r.gw, constants.DefaultGameID, "u1", true)
	cc := r.gw.findUserConn(constants.DefaultGameID, "u1")
	if resp := authenticateGame(t, conn, "foo", "u1"); !resp.OK {
		t.Fatal(resp.Error)
	}
	if r.gw.findUserConn(constants.DefaultGameID, "u1") != nil || r.gw.findUserConn("foo", "u1") != cc {
		t.Fatal("re-authenticated socket is still tracked under the previous game")
	}
	loginGame(t, r.gw, constants.DefaultGameID, "u1", false)
	assertGatewayAlive(t, conn)
	if r.gw.findUserConn("foo", "u1") != cc {
		t.Fatal("previous game's login evicted the re-authenticated socket")
	}
}

func TestGatewayAcrossInstancesGamesStayIndependent(t *testing.T) {
	games := configuredTestGames(t, "cuvara,foo")
	r := startKickTestGateway(t, WithGames(games))
	consumer := NewKickConsumer(r.stream, r.sessions.GatewayID(), r.gw.FindAndCloseConnection, logger.New("error"))
	if err := consumer.Start(context.Background()); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = consumer.Stop() })
	gwB := New(session.NewSessionManager(r.store, "gw-b"), r.gw.registry, testSecret, logger.New("error"),
		WithGames(games), WithKickStream(r.stream))
	t.Cleanup(gwB.Shutdown)
	go func() { _ = gwB.Run("127.0.0.1:0") }()
	for i := 0; i < 100 && gwB.Addr() == ""; i++ {
		time.Sleep(10 * time.Millisecond)
	}
	if gwB.Addr() == "" {
		t.Fatal("second gateway did not start")
	}
	defaultConn := loginGame(t, r.gw, constants.DefaultGameID, "u1", true)
	old, err := r.sessions.GetSession(context.Background(), "u1")
	if err != nil {
		t.Fatal(err)
	}
	fooConn := loginGame(t, gwB, "foo", "u1", true)
	r.expectNoEvent(t)
	r.expectNoGatewayKickEvent(t)
	assertGatewayAlive(t, defaultConn)
	unchanged, err := r.sessions.GetSession(context.Background(), "u1")
	if err != nil || unchanged != old {
		t.Fatalf("other game's cross-gateway login changed session: %+v (%v)", unchanged, err)
	}
	loginGame(t, gwB, constants.DefaultGameID, "u1", true)
	for _, ev := range []SessionSupersededEvent{r.expectEvent(t), r.expectGatewayKickEvent(t)} {
		if ev.GameID != constants.DefaultGameID || ev.UserID != "u1" || ev.JTI != old.JoinTokenJTI ||
			ev.OldGateway != r.sessions.GatewayID() || ev.NewGateway != "gw-b" {
			t.Fatalf("default game's cross-gateway supersede = %+v", ev)
		}
	}
	assertGatewayKick(t, defaultConn)
	assertGatewayAlive(t, fooConn)
	if gwB.findUserConn("foo", "u1") == nil || gwB.findUserConn(constants.DefaultGameID, "u1") == nil {
		t.Fatal("new gateway must retain both games")
	}
}

func TestGatewayUnconfiguredGameRefusedBeforeSessionAccess(t *testing.T) {
	for _, tc := range []struct{ name, configured, claim string }{
		{"default configuration", "", "foo"},
		{"explicit configuration", "cuvara,foo", "bar"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			games := configuredTestGames(t, tc.configured)
			// Observe every session operation so rejection cannot quietly read
			// another game's session, even if it never writes one.
			store := &gameSessionAuditStore{SessionStore: storage.NewMemorySessionStore()}
			gw, met := startGatewayWithOptions(t, WithGames(games), func(g *Gateway) {
				g.sessions = session.NewSessionManager(store)
			})
			met.SetGames(games.IDs())
			conn := dialGateway(t, gw)
			t.Cleanup(func() { _ = conn.Close() })
			resp := authenticateGame(t, conn, tc.claim, "u1")
			if resp.OK || resp.Error != "game not configured" {
				t.Fatalf("unconfigured auth = %+v", resp)
			}
			if store.operations.Load() != 0 {
				t.Fatalf("rejected auth accessed sessions: %v", store.operations.Load())
			}
			if got := testutil.ToFloat64(met.AuthTotal.WithLabelValues(metrics.GameOther, metrics.ResultFail)); got != 1 {
				t.Fatalf("other/fail auth = %v, want 1", got)
			}
			if resp := enterWorld(t, conn, "map_forest"); resp.Error != "not authenticated" {
				t.Fatalf("rejected game entered world: %+v", resp)
			}
			if gw.findUserConn(tc.claim, "u1") != nil || store.operations.Load() != 0 {
				t.Fatal("unconfigured game acquired a session")
			}
		})
	}
}

type gameSessionAuditStore struct {
	storage.SessionStore
	operations atomic.Int64
}

func (s *gameSessionAuditStore) Get(ctx context.Context, key string) ([]byte, error) {
	s.operations.Add(1)
	return s.SessionStore.Get(ctx, key)
}

func (s *gameSessionAuditStore) Set(ctx context.Context, key string, value []byte, ttl time.Duration) error {
	s.operations.Add(1)
	return s.SessionStore.Set(ctx, key, value, ttl)
}

func TestGatewayKickGameAndJTIGuards(t *testing.T) {
	for _, tc := range []struct {
		name, eventGame, eventJTI string
		inWorld, closes           bool
	}{
		{"other game", "foo", "matching", true, false},
		{"stale jti", "cuvara", "old-token", true, false},
		{"empty jti", "cuvara", "", true, false},
		{"never entered world", "cuvara", "matching", false, false},
		{"matching identity", "cuvara", "matching", true, true},
		{"legacy event default game", "", "matching", true, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			gw := startTestGateway(t)
			t.Cleanup(gw.Shutdown)
			conn := loginGame(t, gw, constants.DefaultGameID, "u1", tc.inWorld)
			cc := gw.findUserConn(constants.DefaultGameID, "u1")
			jti := tc.eventJTI
			if jti == "matching" && tc.inWorld {
				jti = cc.JoinTokenJTI()
				if jti == "" {
					t.Fatal("EnterWorld did not latch its join-token jti")
				}
			}
			stream := storage.NewMemoryEventStream()
			t.Cleanup(func() { _ = stream.Close() })
			consumer := NewKickConsumer(stream, "gw-local", gw.FindAndCloseConnection, logger.New("error"))
			consumer.dispatch(makeGatewayKickEvent(t, SessionSupersededEvent{
				GameID: tc.eventGame, UserID: "u1", JTI: jti, OldGateway: "gw-local",
			}))
			if tc.closes {
				assertGatewayKick(t, conn)
				if gw.findUserConn(constants.DefaultGameID, "u1") != nil {
					t.Fatal("kicked connection remained tracked")
				}
			} else {
				assertGatewayAlive(t, conn)
				if gw.findUserConn(constants.DefaultGameID, "u1") != cc {
					t.Fatal("ignored kick removed live tracking")
				}
			}
		})
	}
}

func TestGatewayKickUsesLastAssignmentJTI(t *testing.T) {
	r := startKickTestGateway(t, WithGames(configuredTestGames(t, "cuvara,foo")))
	defaultConn := loginGame(t, r.gw, constants.DefaultGameID, "u1", true)
	fooConn := loginGame(t, r.gw, "foo", "u1", true)
	cc := r.gw.findUserConn("foo", "u1")
	oldJTI := cc.JoinTokenJTI()
	if resp := enterWorld(t, fooConn, "map_forest"); resp.Error != "" {
		t.Fatal(resp.Error)
	}
	newJTI := cc.JoinTokenJTI()
	if newJTI == "" || oldJTI == newJTI {
		t.Fatal("second assignment did not replace the connection's token jti")
	}
	r.gw.FindAndCloseConnection("foo", "u1", oldJTI)
	assertGatewayAlive(t, fooConn)
	r.gw.FindAndCloseConnection("foo", "u1", newJTI)
	assertGatewayKick(t, fooConn)
	assertGatewayAlive(t, defaultConn)
	if r.gw.findUserConn("foo", "u1") != nil || r.gw.findUserConn(constants.DefaultGameID, "u1") == nil {
		t.Fatal("matching foo kick changed the wrong game's tracking")
	}
}

func TestKickConsumerPassesGameUserAndJTI(t *testing.T) {
	for _, game := range []string{"", constants.DefaultGameID, "foo"} {
		t.Run(constants.GameOrDefault(game), func(t *testing.T) {
			var got [3]string
			consumer := NewKickConsumer(nil, "gw", func(game, user, jti string) {
				got = [3]string{game, user, jti}
			}, logger.New("error"))
			consumer.dispatch(makeGatewayKickEvent(t, SessionSupersededEvent{
				GameID: game, UserID: "user", JTI: "jti", OldGateway: "gw",
			}))
			if want := [3]string{constants.GameOrDefault(game), "user", "jti"}; got != want {
				t.Fatalf("closer args = %q, want %q", got, want)
			}
		})
	}
}

func TestClientConnGameIdentityLifecycle(t *testing.T) {
	cc := NewClientConn(nil, logger.New("error"), ratelimit.Bucket{})
	cc.SetJoinTokenJTI("anonymous")
	if cc.Game() != constants.DefaultGameID || cc.JoinTokenJTI() != "" {
		t.Fatal("anonymous connection acquired a token identity")
	}
	cc.SetAuthenticatedGame("u1", "character-1", "foo")
	cc.SetJoinTokenJTI("token-1")
	cc.SetInWorld()
	game, user := cc.ClearIdentityGame()
	if game != "foo" || user != "u1" || cc.UserID() != "" || cc.CharacterID() != "" ||
		cc.Game() != constants.DefaultGameID || cc.JoinTokenJTI() != "" || cc.State() != StateConnected {
		t.Fatal("identity was not cleared together")
	}
	cc.SetJoinTokenJTI("late-token")
	cc.SetInWorld()
	if cc.JoinTokenJTI() != "" || cc.State() != StateConnected {
		t.Fatal("late assignment resurrected cleared identity")
	}
	cc.SetAuthenticatedGame("u1", "character-1", "foo")
	cc.SetJoinTokenJTI("token-2")
	cc.SetAuthenticatedCharacter("u1", "character-2")
	if cc.Game() != constants.DefaultGameID || cc.JoinTokenJTI() != "" {
		t.Fatal("legacy authentication did not reset game and assignment")
	}
	if cc.ClearIdentity() != "u1" {
		t.Fatal("legacy clear lost the user")
	}
	cc.SetAuthenticated("u1")
	if cc.Game() != constants.DefaultGameID {
		t.Fatal("legacy account authentication did not select default game")
	}
}

func TestGatewayGameDisconnectOnlyCleansItsSession(t *testing.T) {
	r := startKickTestGateway(t, WithGames(configuredTestGames(t, "cuvara,foo")))
	defaultConn := loginGame(t, r.gw, constants.DefaultGameID, "u1", false)
	fooConn := loginGame(t, r.gw, "foo", "u1", false)
	disc, err := messages.NewEnvelope(messages.MsgDisconnect, messages.DisconnectMessage{})
	if err != nil {
		t.Fatal(err)
	}
	sendEnvelope(t, fooConn, disc)
	deadline := time.Now().Add(2 * time.Second)
	for {
		_, err = r.store.Get(context.Background(), "session:foo:u1")
		if errors.Is(err, storage.ErrNotFound) {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("foo session survived disconnect: %v", err)
		}
		time.Sleep(10 * time.Millisecond)
	}
	if r.gw.findUserConn("foo", "u1") != nil || r.gw.findUserConn(constants.DefaultGameID, "u1") == nil {
		t.Fatal("disconnect cleaned the wrong game's tracking")
	}
	if _, err := r.store.Get(context.Background(), "session:u1"); err != nil {
		t.Fatalf("default session lost on foo disconnect: %v", err)
	}
	assertGatewayAlive(t, defaultConn)
}

type gameSessionMaintenanceStore struct {
	storage.SessionStore
	refreshes chan string
}

func (s *gameSessionMaintenanceStore) Refresh(ctx context.Context, key string, ttl time.Duration) error {
	s.refreshes <- key
	return s.SessionStore.Refresh(ctx, key, ttl)
}

func TestGatewayGameSessionMaintenance(t *testing.T) {
	ctx := context.Background()
	store := &gameSessionMaintenanceStore{
		SessionStore: storage.NewMemorySessionStore(), refreshes: make(chan string, 2),
	}
	mgr := session.NewSessionManager(store)
	for _, game := range []string{constants.DefaultGameID, "foo"} {
		if _, err := mgr.CreateGameSession(ctx, game, "u1"); err != nil {
			t.Fatal(err)
		}
	}
	gw := New(mgr, nil, testSecret, logger.New("error"))
	cc := NewClientConn(nil, logger.New("error"), ratelimit.Bucket{})
	cc.SetAuthenticatedGame("u1", "character-1", "foo")
	if !gw.checkSession(cc, messages.MsgEnterWorld) {
		t.Fatal("live foo session did not validate")
	}
	gw.refreshSessionOnPong(cc)
	for i := 0; i < 2; i++ {
		select {
		case key := <-store.refreshes:
			if key != "session:foo:u1" {
				t.Errorf("session refresh used %q", key)
			}
		default:
			t.Fatal("missing session refresh")
		}
	}
	if user := gw.cleanupSession(cc); user != "u1" {
		t.Fatalf("cleanup user = %q", user)
	}
	if _, err := store.Get(ctx, "session:foo:u1"); !errors.Is(err, storage.ErrNotFound) {
		t.Fatalf("foo session survived cleanup: %v", err)
	}
	if _, err := store.Get(ctx, "session:u1"); err != nil {
		t.Fatalf("foo maintenance changed default session: %v", err)
	}
}

func TestGatewayVerifiedGameMetrics(t *testing.T) {
	for _, game := range []string{constants.DefaultGameID, "foo"} {
		t.Run(game, func(t *testing.T) {
			games := configuredTestGames(t, "cuvara,foo")
			gw, met := startGatewayWithOptions(t, WithGames(games))
			met.SetGames(games.IDs())
			conn := loginGame(t, gw, game, "u1", true)
			cc := gw.findUserConn(game, "u1")
			claims, err := gw.sessions.GetGameSession(context.Background(), game, "u1")
			if err != nil || claims.JoinTokenJTI != cc.JoinTokenJTI() || claims.JoinTokenJTI == "" {
				t.Fatalf("store and connection join-token jti differ: %+v (%v)", claims, err)
			}
			if resp := enterWorld(t, conn, "unknown-map"); resp.Error == "" {
				t.Fatal("unknown map unexpectedly assigned")
			}
			for _, check := range []struct {
				name  string
				value prometheus.Counter
				want  float64
			}{
				{"auth ok", met.AuthTotal.WithLabelValues(game, metrics.ResultOK), 1},
				{"enter ok", met.EnterWorldTotal.WithLabelValues(game, metrics.ResultOK), 1},
				{"enter fail", met.EnterWorldTotal.WithLabelValues(game, metrics.ResultFail), 1},
				{"session ok", met.SessionChecksTotal.WithLabelValues(game, metrics.SessionCheckOK), 2},
			} {
				if got := testutil.ToFloat64(check.value); got != check.want {
					t.Errorf("%s/%s = %v, want %v", game, check.name, got, check.want)
				}
			}
			if err := gw.sessions.DestroySession(context.Background(), session.GameSessionKey(game, "u1")); err != nil {
				t.Fatal(err)
			}
			if resp := enterWorld(t, conn, "map_forest"); resp.Error != "session expired" {
				t.Fatalf("expired session response = %+v", resp)
			}
			if got := testutil.ToFloat64(met.SessionChecksTotal.WithLabelValues(game, metrics.SessionCheckExpired)); got != 1 {
				t.Fatalf("%s/expired session metric = %v", game, got)
			}
			if got := testutil.ToFloat64(met.EnterWorldTotal.WithLabelValues(game, metrics.ResultFail)); got != 2 {
				t.Fatalf("%s/fail enter-world metric after expiry = %v", game, got)
			}
		})
	}
}

func TestSupersedeAlwaysWritesExplicitGame(t *testing.T) {
	for _, game := range []string{constants.DefaultGameID, "foo"} {
		t.Run(game, func(t *testing.T) {
			r := startKickTestGateway(t, WithGames(configuredTestGames(t, "cuvara,foo")))
			loginGame(t, r.gw, game, "u1", true)
			loginGame(t, r.gw, game, "u1", false)
			select {
			case ev := <-r.events:
				var payload map[string]json.RawMessage
				if err := json.Unmarshal(ev.Payload, &payload); err != nil {
					t.Fatal(err)
				}
				if got := string(payload["game_id"]); got != `"`+game+`"` {
					t.Fatalf("game_id = %s, want explicit %q", got, game)
				}
			case <-time.After(2 * time.Second):
				t.Fatal("no supersede event")
			}
		})
	}
}
