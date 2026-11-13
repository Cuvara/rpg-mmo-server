package social

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"testing"

	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// gameStore records reads and mutations, including deletes of records that no
// longer exist after leave. The existing mock still enforces version checks.
type gameStore struct {
	*mockNakama
	collections []string
}

func newGameStore() *gameStore { return &gameStore{mockNakama: newMockNakama()} }

func (m *gameStore) StorageRead(ctx context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error) {
	for _, r := range reads {
		m.collections = append(m.collections, r.Collection)
	}
	return m.mockNakama.StorageRead(ctx, reads)
}

func (m *gameStore) MultiUpdate(ctx context.Context, a []*runtime.AccountUpdate, w []*runtime.StorageWrite,
	d []*runtime.StorageDelete, wa []*runtime.WalletUpdate, ledger bool,
) ([]*api.StorageObjectAck, []*runtime.WalletUpdateResult, error) {
	for _, write := range w {
		m.collections = append(m.collections, write.Collection)
	}
	for _, del := range d {
		m.collections = append(m.collections, del.Collection)
	}
	return m.mockNakama.MultiUpdate(ctx, a, w, d, wa, ledger)
}

func assertGameCollections(t *testing.T, store *gameStore, game string) {
	t.Helper()
	party := gamectx.Scoped(game, PartyCollection)
	member := gamectx.Scoped(game, MembershipCollection)
	if len(store.collections) == 0 {
		t.Fatal("no storage access recorded")
	}
	for _, collection := range store.collections {
		if collection != party && collection != member {
			t.Errorf("accessed collection %q, want only %q or %q", collection, party, member)
		}
	}
}

func partyGameContext(ctx context.Context, games, game string) context.Context {
	ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_ENV, map[string]string{gamectx.EnvGames: games})
	if game != "" {
		ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_VARS, map[string]string{gamectx.VarGame: game})
	}
	return ctx
}

func partyPayload(partyID, game string) string {
	out, _ := json.Marshal(PartyRequest{PartyID: partyID, GameID: game})
	return string(out)
}

func TestPartyRPCs_GameLifecycle(t *testing.T) {
	t.Setenv("GAMES", "")
	for _, tc := range []struct {
		name, games, game, collection string
	}{
		{"legacy session", "", "", "party"},
		{"explicit default", "", "cuvara", "party"},
		{"non-default", "cuvara,foo", "foo", "foo.party"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			nk := newGameStore()
			leader := "game-lifecycle-leader-" + tc.name
			joiner := "game-lifecycle-joiner-" + tc.name
			leaderCtx := partyGameContext(clientCtx(leader), tc.games, tc.game)
			joinerCtx := partyGameContext(clientCtx(joiner), tc.games, tc.game)
			out, err := PartyCreateRPC(leaderCtx, noopLogger{}, nil, nk, "")
			if err != nil {
				t.Fatalf("create: %v", err)
			}
			created := decodeResponse(t, out)
			if _, ok := nk.get(tc.collection, created.PartyID, ""); !ok {
				t.Fatalf("party not stored in %s", tc.collection)
			}
			payload := partyPayload(created.PartyID, "")
			out, err = PartyJoinRPC(joinerCtx, noopLogger{}, nil, nk, payload)
			if err != nil {
				t.Fatalf("join: %v", err)
			}
			if joined := decodeResponse(t, out); joined.MemberCount != 2 {
				t.Fatalf("join response = %+v", joined)
			}
			out, err = PartyGetRPC(joinerCtx, noopLogger{}, nil, nk, payload)
			if err != nil {
				t.Fatalf("get: %v", err)
			}
			if got := decodeResponse(t, out); got.PartyID != created.PartyID || got.MemberCount != 2 {
				t.Fatalf("get response = %+v", got)
			}
			out, err = PartyLeaveRPC(leaderCtx, noopLogger{}, nil, nk, "")
			if err != nil {
				t.Fatalf("leader leave: %v", err)
			}
			if left := decodeResponse(t, out); left.LeaderID != joiner || left.MemberCount != 1 {
				t.Fatalf("leave response = %+v", left)
			}
			out, err = PartyLeaveRPC(joinerCtx, noopLogger{}, nil, nk, "")
			if err != nil {
				t.Fatalf("last leave: %v", err)
			}
			if left := decodeResponse(t, out); left.PartyID != "" || left.MemberCount != 0 {
				t.Fatalf("last leave response = %+v", left)
			}
			if len(nk.objects) != 0 {
				t.Fatalf("leave leaked records: %v", nk.objects)
			}
			assertGameCollections(t, nk, tc.game)
		})
	}
}

func TestPartyRPCs_CannotCrossGames(t *testing.T) {
	for _, tc := range []struct{ source, target string }{{"foo", "cuvara"}, {"cuvara", "foo"}} {
		t.Run(tc.source+" to "+tc.target, func(t *testing.T) {
			nk := newGameStore()
			p, err := CreateParty(context.Background(), nk, tc.source, "cross-leader", fixedNow)
			if err != nil {
				t.Fatalf("create: %v", err)
			}
			nk.collections = nil
			ctx := partyGameContext(clientCtx("cross-joiner-"+tc.source), "cuvara,foo", tc.target)
			payload := partyPayload(p.ID, "")
			if _, err := PartyJoinRPC(ctx, noopLogger{}, nil, nk, payload); !errors.Is(err, ErrPartyNotFound) {
				t.Fatalf("cross-game join = %v, want ErrPartyNotFound", err)
			}
			if _, err := PartyGetRPC(ctx, noopLogger{}, nil, nk, payload); !errors.Is(err, ErrPartyNotFound) {
				t.Fatalf("cross-game get = %v, want ErrPartyNotFound", err)
			}
			assertGameCollections(t, nk, tc.target)
			if nk.multiCalls != 1 {
				t.Fatalf("cross-game lookups mutated storage: %d updates", nk.multiCalls)
			}
		})
	}
}

func TestPartyMembership_OnePartyPerGame(t *testing.T) {
	nk := newGameStore()
	ctx := context.Background()
	parties := make(map[string]*Party)
	for _, game := range []string{"cuvara", "foo"} {
		p, err := CreateParty(ctx, nk, game, "same-user", fixedNow)
		if err != nil {
			t.Fatalf("create in %s: %v", game, err)
		}
		parties[game] = p
		if _, err := CreateParty(ctx, nk, game, "same-user", fixedNow); !errors.Is(err, ErrAlreadyInParty) {
			t.Fatalf("second create in %s = %v, want ErrAlreadyInParty", game, err)
		}
	}
	if _, err := LeaveParty(ctx, nk, "foo", "same-user", fixedNow); err != nil {
		t.Fatalf("leave foo: %v", err)
	}
	if _, err := GetParty(ctx, nk, "foo", parties["foo"].ID); !errors.Is(err, ErrPartyNotFound) {
		t.Fatalf("foo party after leave = %v", err)
	}
	if _, err := GetParty(ctx, nk, "cuvara", parties["cuvara"].ID); err != nil {
		t.Fatalf("default party lost: %v", err)
	}
	if _, ok := nk.get("party_member", MembershipKey, "same-user"); !ok {
		t.Fatal("leaving foo removed the default membership")
	}
}

func TestPartyGetRPC_ServerGame(t *testing.T) {
	t.Setenv("GAMES", "")
	for _, tc := range []struct{ name, games, payloadGame, wantGame string }{
		{"non-default payload", "cuvara,foo", "foo", "foo"},
		{"absent payload", "", "", "cuvara"},
		{"explicit default payload", "", "cuvara", "cuvara"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			nk := newGameStore()
			for _, game := range []string{"cuvara", "foo"} {
				w := partyWrite(game, &Party{ID: "same-id", LeaderID: game, Members: []string{game}}, "")
				nk.put(w.Collection, w.Key, w.UserID, w.Value)
			}
			// Sessionless calls must ignore stray context vars.
			ctx := partyGameContext(serverCtx(), tc.games, "foo")
			out, err := PartyGetRPC(ctx, noopLogger{}, nil, nk, partyPayload("same-id", tc.payloadGame))
			if err != nil {
				t.Fatalf("get: %v", err)
			}
			if got := decodeResponse(t, out); got.LeaderID != tc.wantGame {
				t.Fatalf("leader = %q, want %q", got.LeaderID, tc.wantGame)
			}
			assertGameCollections(t, nk, tc.wantGame)
		})
	}
}

type partyGameLogger struct {
	noopLogger
	message string
}

func (l *partyGameLogger) Error(format string, args ...interface{}) {
	l.message = fmt.Sprintf(format, args...)
}

func TestPartyRPCs_GameErrorsBeforeStorage(t *testing.T) {
	type rpcHandler func(context.Context, runtime.Logger, *sql.DB, runtime.NakamaModule, string) (string, error)
	for _, rpc := range []struct {
		name string
		call rpcHandler
	}{{RPCPartyCreate, PartyCreateRPC}, {RPCPartyJoin, PartyJoinRPC}, {RPCPartyLeave, PartyLeaveRPC}, {RPCPartyGet, PartyGetRPC}} {
		for _, tc := range []struct {
			name, games, game, payloadGame string
			want                           error
		}{
			{"game not allowed", "cuvara,foo", "bar", "", gamectx.ErrGameNotAllowed},
			{"invalid configuration", "Bad", "foo", "", ErrInternal},
			{"session override", "cuvara,foo", "foo", "cuvara", gamectx.ErrGameMismatch},
		} {
			if tc.payloadGame != "" && rpc.name != RPCPartyJoin && rpc.name != RPCPartyGet {
				continue // create and leave have no game_id payload
			}
			t.Run(rpc.name+"/"+tc.name, func(t *testing.T) {
				nk := newGameStore()
				log := &partyGameLogger{}
				ctx := partyGameContext(clientCtx("game-error-"+rpc.name+tc.name), tc.games, tc.game)
				_, err := rpc.call(ctx, log, nil, nk, partyPayload("party-id", tc.payloadGame))
				if err != tc.want {
					t.Fatalf("err = %#v, want exact public error %#v", err, tc.want)
				}
				if nk.readCalls != 0 || nk.multiCalls != 0 || len(nk.collections) != 0 {
					t.Fatalf("rejected call reached storage: %d reads, %d updates", nk.readCalls, nk.multiCalls)
				}
				if tc.want == ErrInternal && (!strings.Contains(log.message, rpc.name) || !strings.Contains(log.message, "Bad")) {
					t.Fatalf("configuration log lacks RPC or invalid entry: %q", log.message)
				}
			})
		}
	}
	for _, tc := range []struct {
		name, games, payloadGame string
		want                     error
	}{
		{"unknown server game", "cuvara,foo", "bar", gamectx.ErrGameNotAllowed},
		{"bad server configuration", "Bad", "foo", ErrInternal},
	} {
		t.Run(tc.name, func(t *testing.T) {
			nk := newGameStore()
			ctx := partyGameContext(serverCtx(), tc.games, "")
			_, err := PartyGetRPC(ctx, noopLogger{}, nil, nk, partyPayload("party-id", tc.payloadGame))
			if err != tc.want || nk.readCalls != 0 || nk.multiCalls != 0 {
				t.Fatalf("err = %v, reads = %d, updates = %d", err, nk.readCalls, nk.multiCalls)
			}
		})
	}
}

func TestLeaveParty_GameStaleMembership(t *testing.T) {
	for _, exists := range []bool{false, true} {
		t.Run(fmt.Sprintf("party exists=%t", exists), func(t *testing.T) {
			nk := newGameStore()
			w := membershipWrite("foo", "stale-user", "stale-party", fixedNow())
			nk.put(w.Collection, w.Key, w.UserID, w.Value)
			if exists {
				w := partyWrite("foo", &Party{ID: "stale-party", Members: []string{"other-user"}}, "")
				nk.put(w.Collection, w.Key, w.UserID, w.Value)
			}
			if _, err := LeaveParty(context.Background(), nk, "foo", "stale-user", fixedNow); err != nil {
				t.Fatalf("clear stale membership: %v", err)
			}
			if _, ok := nk.get("foo.party_member", MembershipKey, "stale-user"); ok {
				t.Fatal("stale membership survived leave")
			}
			assertGameCollections(t, nk, "foo")
		})
	}
}
