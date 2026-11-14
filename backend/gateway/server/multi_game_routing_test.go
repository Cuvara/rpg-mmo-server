package server

import (
	"context"
	"net"
	"strings"
	"sync"
	"sync/atomic"
	"testing"

	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/ratelimit"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/prometheus/client_golang/prometheus/testutil"
)

const testFooJoinSecret = "foo-join-secret"

// Existing session-isolation tests enter both games. Give foo its own server
// and secret rather than relying on the default game's routing configuration.
func withTestFooRouting(t *testing.T) Option {
	t.Helper()
	return func(g *Gateway) {
		WithGameJoinTokenSecret("foo", testFooJoinSecret)(g)
		if err := g.registry.RegisterServer(context.Background(), storage.ServerInfo{
			GameID: "foo", ServerID: "foo-srv1", MapID: "map_forest",
			Addr: "10.0.1.1:9000", Capacity: 100, Transport: "kcp",
		}); err != nil {
			t.Fatal(err)
		}
	}
}

type routingAuditRegistry struct {
	storage.ServerRegistry
	lookups atomic.Int32
}

func (r *routingAuditRegistry) FindByMapID(ctx context.Context, mapID string) ([]storage.ServerInfo, error) {
	r.lookups.Add(1)
	return r.ServerRegistry.FindByMapID(ctx, mapID)
}

func (r *routingAuditRegistry) FindByGameAndMapID(ctx context.Context, game, mapID string) ([]storage.ServerInfo, error) {
	r.lookups.Add(1)
	return r.ServerRegistry.FindByGameAndMapID(ctx, game, mapID)
}

type routingAuditParty struct {
	games chan string
}

func (p *routingAuditParty) IsMember(_ context.Context, game, partyID, userID string) (bool, error) {
	p.games <- game
	return false, nil
}

func requestWorld(t *testing.T, conn net.Conn, mapID, partyID string) messages.EnterWorldResponse {
	t.Helper()
	env, err := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: mapID, PartyID: partyID})
	if err != nil {
		t.Fatal(err)
	}
	sendEnvelope(t, conn, env)
	resp := readEnvelope(t, conn)
	if resp.Type != messages.MsgEnterWorldResp {
		t.Fatalf("enter-world response type = %v", resp.Type)
	}
	var world messages.EnterWorldResponse
	if err := resp.UnmarshalPayload(&world); err != nil {
		t.Fatal(err)
	}
	return world
}

func TestGatewayEnterWorldRejectsInvalidMapAndContentBeforeRouting(t *testing.T) {
	for _, tc := range []struct{ name, mapID, partyID string }{
		{"empty", "", ""},
		{"colon", "a:b", ""},
		{"too long", strings.Repeat("a", 65), ""},
		{"path", "../x", ""},
		{"unicode", "map_\u00e9", ""},
		{"dungeon colon", "dungeon:01", "party-1"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			store := &routingAuditRegistry{ServerRegistry: storage.NewMemoryServerRegistry()}
			alloc := &countingAllocator{}
			party := &routingAuditParty{games: make(chan string, 1)}
			gw, met := startGatewayWithOptions(t, WithDungeons(storage.NewMemoryDungeonIndex(), party), func(g *Gateway) {
				g.registry = registry.NewRegistryServiceWithAllocator(store, alloc)
			})
			conn := loginGame(t, gw, constants.DefaultGameID, "u1", false)
			resp := requestWorld(t, conn, tc.mapID, tc.partyID)
			if resp.Error != "invalid map id" || resp.JoinToken != "" || resp.ServerAddr != "" {
				t.Fatalf("invalid id response = %+v", resp)
			}
			if store.lookups.Load() != 0 || alloc.hits.Load() != 0 || len(party.games) != 0 {
				t.Fatal("invalid id reached registry, allocation or party membership")
			}
			if got := testutil.ToFloat64(met.EnterWorldTotal.WithLabelValues(constants.DefaultGameID, metrics.ResultFail)); got != 1 {
				t.Fatalf("enter-world failure count = %v", got)
			}
		})
	}
}

func TestGatewayEnterWorldAcceptsValidMapIDs(t *testing.T) {
	for _, mapID := range []string{"map_01", "map_forest", "map_full", "A-Za_z-09", strings.Repeat("a", 64)} {
		t.Run(mapID, func(t *testing.T) {
			gw, _ := startGatewayWithOptions(t, func(g *Gateway) {
				if err := g.registry.RegisterServer(context.Background(), storage.ServerInfo{
					ServerID: "valid-map-server", MapID: mapID, Addr: "10.1.0.1:9000", Capacity: 100, Transport: "kcp",
				}); err != nil {
					t.Fatal(err)
				}
			})
			conn := loginGame(t, gw, constants.DefaultGameID, "u1", false)
			resp := enterWorld(t, conn, mapID)
			if resp.Error != "" || resp.JoinToken == "" {
				t.Fatalf("valid map %q was refused: %+v", mapID, resp)
			}
		})
	}
}

func TestGatewayEnterWorldScopesMapAndJoinSecretToGame(t *testing.T) {
	const cuvaraSecret = "cuvara-join-secret"
	games := configuredTestGames(t, "cuvara,foo")
	gw, met := startGatewayWithOptions(t, WithGames(games), WithJoinTokenSecret(cuvaraSecret),
		WithGameJoinTokenSecret("foo", testFooJoinSecret), func(g *Gateway) {
			for _, info := range []storage.ServerInfo{
				{GameID: constants.DefaultGameID, ServerID: "srv-map01", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100, Transport: "kcp"},
				{GameID: "foo", ServerID: "foo-map01", MapID: "map_01", Addr: "10.0.1.1:9000", Capacity: 100, Transport: "kcp"},
			} {
				if err := g.registry.RegisterServer(context.Background(), info); err != nil {
					t.Fatal(err)
				}
			}
		})
	met.SetGames(games.IDs())
	for _, tc := range []struct{ game, secret, otherSecret, server, addr, gid string }{
		{constants.DefaultGameID, cuvaraSecret, testFooJoinSecret, "srv-map01", "10.0.0.1:9000", ""},
		{"foo", testFooJoinSecret, cuvaraSecret, "foo-map01", "10.0.1.1:9000", "foo"},
	} {
		conn := loginGame(t, gw, tc.game, "u1", false)
		resp := enterWorld(t, conn, "map_01")
		if resp.Error != "" || resp.ServerAddr != tc.addr {
			t.Fatalf("%s assignment = %+v", tc.game, resp)
		}
		claims, err := jwt.Verify(resp.JoinToken, tc.secret)
		if err != nil {
			t.Fatalf("%s dedicated secret failed: %v", tc.game, err)
		}
		if claims.GameID != tc.gid || claims.ServerID != tc.server || claims.UserID != "u1" || claims.CharacterID != "character-1" {
			t.Fatalf("%s claims = %+v", tc.game, claims)
		}
		for _, secret := range []string{tc.otherSecret, testSecret} {
			if _, err := jwt.Verify(resp.JoinToken, secret); err == nil {
				t.Fatalf("%s join token verified with another hop/game's secret", tc.game)
			}
		}
		if got := testutil.ToFloat64(met.EnterWorldTotal.WithLabelValues(tc.game, metrics.ResultOK)); got != 1 {
			t.Fatalf("%s successful entries = %v", tc.game, got)
		}
		sd, err := gw.sessions.GetGameSession(context.Background(), tc.game, "u1")
		if err != nil || sd.ServerID != tc.server || sd.MapID != "map_01" {
			t.Fatalf("%s assignment session = %+v (%v)", tc.game, sd, err)
		}
	}
}

func TestGatewayEnterWorldRefusesGameWithoutJoinSecretBeforeRouting(t *testing.T) {
	for _, partyID := range []string{"", "party-1"} {
		t.Run("party="+partyID, func(t *testing.T) {
			store := &routingAuditRegistry{ServerRegistry: storage.NewMemoryServerRegistry()}
			alloc := &countingAllocator{}
			party := &routingAuditParty{games: make(chan string, 1)}
			games := configuredTestGames(t, "cuvara,foo")
			gw, met := startGatewayWithOptions(t, WithGames(games), WithDungeons(storage.NewMemoryDungeonIndex(), party), func(g *Gateway) {
				g.registry = registry.NewRegistryServiceWithAllocator(store, alloc)
			})
			met.SetGames(games.IDs())
			conn := loginGame(t, gw, "foo", "u1", false)
			resp := requestWorld(t, conn, "map_01", partyID)
			if resp.Error != "game not routable" || resp.JoinToken != "" || resp.ServerAddr != "" {
				t.Fatalf("missing game secret response = %+v", resp)
			}
			if store.lookups.Load() != 0 || alloc.hits.Load() != 0 || len(party.games) != 0 {
				t.Fatal("missing game secret reached registry, allocation or party membership")
			}
			if got := testutil.ToFloat64(met.EnterWorldTotal.WithLabelValues("foo", metrics.ResultFail)); got != 1 {
				t.Fatalf("foo enter-world failure count = %v", got)
			}
		})
	}
}

func TestGatewayDungeonMembershipReceivesSessionGame(t *testing.T) {
	party := &routingAuditParty{games: make(chan string, 1)}
	gw, _ := startGatewayWithOptions(t, WithGames(configuredTestGames(t, "cuvara,foo")),
		WithGameJoinTokenSecret("foo", testFooJoinSecret), WithDungeons(storage.NewMemoryDungeonIndex(), party))
	conn := loginGame(t, gw, "foo", "u1", false)
	resp := requestWorld(t, conn, "dungeon_01", "party-1")
	if resp.Error != msgNotInParty {
		t.Fatalf("dungeon response = %+v", resp)
	}
	select {
	case game := <-party.games:
		if game != "foo" {
			t.Fatalf("membership game = %q", game)
		}
	default:
		t.Fatal("dungeon did not check party membership")
	}
}

func TestGatewayExpiredSessionUntracksOnlyItsGame(t *testing.T) {
	for _, game := range []string{constants.DefaultGameID, "foo"} {
		t.Run(game, func(t *testing.T) {
			r := startKickTestGateway(t, WithGames(configuredTestGames(t, "cuvara,foo")))
			defaultConn := loginGame(t, r.gw, constants.DefaultGameID, "u1", false)
			fooConn := loginGame(t, r.gw, "foo", "u1", false)
			conn, otherGame := defaultConn, "foo"
			if game == "foo" {
				conn, otherGame = fooConn, constants.DefaultGameID
			}
			cc := r.gw.findUserConn(game, "u1")
			if err := r.sessions.DestroySession(context.Background(), session.GameSessionKey(game, "u1")); err != nil {
				t.Fatal(err)
			}
			if resp := enterWorld(t, conn, "map_01"); resp.Error != "session expired" {
				t.Fatalf("expired-session response = %+v", resp)
			}
			if r.gw.findUserConn(game, "u1") != nil {
				t.Fatal("expired session left a stale (game, user) connection")
			}
			if user, state, gid := cc.IdentityGame(); user != "" || state != StateConnected || gid != constants.DefaultGameID {
				t.Fatalf("expired identity = (%q, %v, %q)", user, state, gid)
			}
			if r.gw.findUserConn(otherGame, "u1") == nil {
				t.Fatal("expiry untracked the other game's live session")
			}
			if _, err := r.sessions.GetGameSession(context.Background(), otherGame, "u1"); err != nil {
				t.Fatalf("expiry removed the other game's session: %v", err)
			}
		})
	}
}

func TestClientConnIdentityGameConcurrentTransitions(t *testing.T) {
	cc := NewClientConn(nil, logger.New("error"), ratelimit.Bucket{})
	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		for i := 0; i < 2000; i++ {
			cc.SetAuthenticatedGame("cuvara-user", "", constants.DefaultGameID)
			cc.SetAuthenticatedGame("foo-user", "", "foo")
			cc.ClearIdentityGame()
		}
	}()
	for i := 0; i < 3; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for j := 0; j < 2000; j++ {
				user, state, game := cc.IdentityGame()
				switch user {
				case "":
					if state != StateConnected || game != constants.DefaultGameID {
						t.Errorf("torn anonymous identity: %v/%s", state, game)
						return
					}
				case "cuvara-user", "foo-user":
					if state != StateAuthenticated || user != game+"-user" {
						t.Errorf("torn authenticated identity: %s/%v/%s", user, state, game)
						return
					}
				default:
					t.Errorf("unexpected identity %q", user)
					return
				}
			}
		}()
	}
	wg.Wait()
}
