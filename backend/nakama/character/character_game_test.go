package character

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"testing"

	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

func TestRosterGameCollections(t *testing.T) {
	games := []struct {
		game       string
		collection string
	}{
		{game: "", collection: "characters"},
		{game: constants.DefaultGameID, collection: "characters"},
		{game: "foo", collection: "foo.characters"},
	}
	operations := []struct {
		name   string
		writes int
		call   func(*testing.T, *memStore, string, string, Character)
	}{
		{name: "ReadRoster", call: func(t *testing.T, st *memStore, game, user string, c Character) {
			roster, version, err := ReadRoster(context.Background(), st, game, user)
			if err != nil || version == "" || len(roster.Characters) != 1 || roster.Characters[0] != c {
				t.Fatalf("ReadRoster = %+v, %q, %v", roster, version, err)
			}
		}},
		{name: "List", call: func(t *testing.T, st *memStore, game, user string, c Character) {
			cs, err := List(context.Background(), st, game, user)
			if err != nil || len(cs) != 1 || cs[0] != c {
				t.Fatalf("List = %+v, %v", cs, err)
			}
		}},
		{name: "Owns", call: func(t *testing.T, st *memStore, game, user string, c Character) {
			if owns, err := Owns(context.Background(), st, game, user, c.ID); err != nil || !owns {
				t.Fatalf("Owns = %v, %v", owns, err)
			}
		}},
		{name: "Create", writes: 1, call: func(t *testing.T, st *memStore, game, user string, _ Character) {
			c, err := Create(context.Background(), st, game, user, "Second", -1, fixedNow)
			if err != nil || c.Slot != 1 {
				t.Fatalf("Create = %+v, %v", c, err)
			}
		}},
		{name: "Delete", writes: 1, call: func(t *testing.T, st *memStore, game, user string, c Character) {
			if err := Delete(context.Background(), st, game, user, c.ID); err != nil {
				t.Fatalf("Delete: %v", err)
			}
			var roster Roster
			if err := json.Unmarshal([]byte(st.objects[objKey(gamectx.Scoped(game, Collection), RosterKey, user)].value), &roster); err != nil {
				t.Fatal(err)
			}
			if len(roster.Characters) != 0 {
				t.Fatalf("deleted roster = %+v", roster)
			}
		}},
	}
	for _, game := range games {
		t.Run("game="+game.game, func(t *testing.T) {
			for _, op := range operations {
				t.Run(op.name, func(t *testing.T) {
					st := newMemStore()
					user := t.Name()
					c, err := Create(context.Background(), st, game.game, user, "First", -1, fixedNow)
					if err != nil {
						t.Fatal(err)
					}
					st.readCollections, st.writeCollections = nil, nil
					op.call(t, st, game.game, user, c)
					assertRosterCollections(t, st, game.collection, 1, op.writes)
					if len(st.objects) != 1 || st.objects[objKey(game.collection, RosterKey, user)] == nil {
						t.Fatalf("stored objects = %+v; want only %s", st.objects, game.collection)
					}
				})
			}
		})
	}
}

func assertRosterCollections(t *testing.T, st *memStore, collection string, reads, writes int) {
	t.Helper()
	if len(st.readCollections) != reads || len(st.writeCollections) != writes {
		t.Fatalf("storage reads/writes = %v/%v, want %d/%d in %s", st.readCollections, st.writeCollections, reads, writes, collection)
	}
	for _, got := range append(append([]string(nil), st.readCollections...), st.writeCollections...) {
		if got != collection {
			t.Fatalf("storage collection = %q, want %q", got, collection)
		}
	}
}

func TestRosterGameIsolation(t *testing.T) {
	for _, tt := range []struct{ source, target string }{
		{source: "foo", target: ""},
		{source: "foo", target: constants.DefaultGameID},
		{source: "", target: "foo"},
		{source: constants.DefaultGameID, target: "foo"},
		{source: "foo", target: "bar"},
	} {
		t.Run(tt.source+"_to_"+tt.target, func(t *testing.T) {
			st := newMemStore()
			ctx, user := context.Background(), t.Name()
			c, err := Create(ctx, st, tt.source, user, "Source", 0, fixedNow)
			if err != nil {
				t.Fatal(err)
			}
			st.readCollections, st.writeCollections = nil, nil
			if cs, err := List(ctx, st, tt.target, user); err != nil || len(cs) != 0 {
				t.Fatalf("other game's List = %+v, %v", cs, err)
			}
			if owns, err := Owns(ctx, st, tt.target, user, c.ID); err != nil || owns {
				t.Fatalf("other game's Owns = %v, %v", owns, err)
			}
			if err := Delete(ctx, st, tt.target, user, c.ID); !errors.Is(err, ErrCharacterNotFound) {
				t.Fatalf("other game's Delete = %v", err)
			}
			assertRosterCollections(t, st, gamectx.Scoped(tt.target, Collection), 3, 0)
			// The same user can use the same slot independently in both games.
			other, err := Create(ctx, st, tt.target, user, "Target", 0, fixedNow)
			if err != nil {
				t.Fatal(err)
			}
			for _, own := range []struct {
				game string
				c    Character
			}{{tt.source, c}, {tt.target, other}} {
				cs, err := List(ctx, st, own.game, user)
				if err != nil || len(cs) != 1 || cs[0] != own.c {
					t.Fatalf("List(%q) = %+v, %v", own.game, cs, err)
				}
			}
		})
	}
}

func TestRosterDefaultGameAliases(t *testing.T) {
	for _, game := range []string{"", constants.DefaultGameID} {
		t.Run("created_in_"+game, func(t *testing.T) {
			st := newMemStore()
			ctx, user := context.Background(), t.Name()
			c, err := Create(ctx, st, game, user, "Default", 0, fixedNow)
			if err != nil {
				t.Fatal(err)
			}
			for _, alias := range []string{"", constants.DefaultGameID} {
				cs, err := List(ctx, st, alias, user)
				if err != nil || len(cs) != 1 || cs[0] != c {
					t.Fatalf("List(%q) = %+v, %v", alias, cs, err)
				}
				if owns, err := Owns(ctx, st, alias, user, c.ID); err != nil || !owns {
					t.Fatalf("Owns(%q) = %v, %v", alias, owns, err)
				}
			}
			if err := Delete(ctx, st, constants.DefaultGameID, user, c.ID); err != nil {
				t.Fatal(err)
			}
			assertRosterCollections(t, st, "characters", 6, 2)
		})
	}
}

// rosterResponseStore simulates an unexpected object returned alongside the
// requested game's object, ensuring the read-back filter stays scoped too.
type rosterResponseStore struct {
	Store
	objects []*api.StorageObject
}

func (s rosterResponseStore) StorageRead(context.Context, []*runtime.StorageRead) ([]*api.StorageObject, error) {
	return s.objects, nil
}

func TestReadRosterGameFilter(t *testing.T) {
	for _, tt := range []struct{ game, foreign string }{
		{game: "foo", foreign: "characters"},
		{game: "foo", foreign: "bar.characters"},
		{game: constants.DefaultGameID, foreign: "foo.characters"},
	} {
		t.Run(tt.game+"_ignores_"+tt.foreign, func(t *testing.T) {
			st := rosterResponseStore{objects: []*api.StorageObject{
				{Collection: tt.foreign, Key: RosterKey, Value: `{bad-json`},
				{Collection: gamectx.Scoped(tt.game, Collection), Key: "another-key", Value: `{bad-json`},
				{Collection: gamectx.Scoped(tt.game, Collection), Key: RosterKey, Value: `{"characters":[{"id":"mine"}]}`, Version: "v1"},
			}}
			r, version, err := ReadRoster(context.Background(), st, tt.game, t.Name())
			if err != nil || version != "v1" || len(r.Characters) != 1 || r.Characters[0].ID != "mine" {
				t.Fatalf("ReadRoster = %+v, %q, %v", r, version, err)
			}
		})
	}
}

type rosterGameLogger struct {
	noopLogger
	errors []string
}

func (l *rosterGameLogger) Error(format string, args ...interface{}) {
	l.errors = append(l.errors, fmt.Sprintf(format, args...))
}

func TestRosterRPCGame(t *testing.T) {
	t.Setenv("GAMES", "")
	rpcs := []struct {
		name   string
		writes int
		call   func(context.Context, runtime.Logger, Store) (string, error)
		check  func(*testing.T, string)
	}{
		{name: RPCCharacterList, call: listRPC, check: func(t *testing.T, out string) {
			if out != `{"characters":[{"id":"existing","slot":0,"name":"First","created_at":0}],"max_slots":4}` {
				t.Fatalf("list response changed: %s", out)
			}
		}},
		{name: RPCCharacterCreate, writes: 1, call: func(ctx context.Context, logger runtime.Logger, st Store) (string, error) {
			return createRPC(ctx, logger, st, `{"name":"Second"}`)
		}, check: func(t *testing.T, out string) {
			var response CreateResponse
			if err := json.Unmarshal([]byte(out), &response); err != nil {
				t.Fatal(err)
			}
			if response.Character.Name != "Second" || response.Character.Slot != 1 || !uuidV4.MatchString(response.Character.ID) {
				t.Fatalf("create response = %s", out)
			}
		}},
		{name: RPCCharacterDelete, writes: 1, call: func(ctx context.Context, logger runtime.Logger, st Store) (string, error) {
			return deleteRPC(ctx, logger, st, `{"character_id":"existing"}`)
		}, check: func(t *testing.T, out string) {
			if out != `{"character_id":"existing","deleted":true}` {
				t.Fatalf("delete response changed: %s", out)
			}
		}},
	}
	cases := []struct {
		name       string
		game       string
		games      string
		noEnv      bool
		collection string
		wantErr    error
	}{
		{name: "session game", game: "foo", games: "cuvara,foo", collection: "foo.characters"},
		{name: "legacy no vars or config", noEnv: true, collection: "characters"},
		{name: "explicit default", game: constants.DefaultGameID, games: "cuvara,foo", collection: "characters"},
		{name: "game outside allow-list", game: "bar", games: "cuvara,foo", wantErr: gamectx.ErrGameNotAllowed},
		{name: "invalid configuration", game: "foo", games: "Bad", wantErr: ErrInternal},
	}
	for _, rpc := range rpcs {
		t.Run(rpc.name, func(t *testing.T) {
			for _, tt := range cases {
				t.Run(tt.name, func(t *testing.T) {
					user := t.Name() // Each RPC/case has its own rate-limit bucket.
					ctx := userCtx(user)
					if !tt.noEnv {
						ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_ENV, map[string]string{"GAMES": tt.games}) //nolint:staticcheck
						ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_VARS, map[string]string{"game": tt.game})  //nolint:staticcheck
					}
					st := newMemStore()
					if tt.wantErr == nil {
						// Seed through the expected collection directly so an incorrectly
						// defaulted RPC cannot pass by reading its own misplaced write.
						st.objects[objKey(tt.collection, RosterKey, user)] = &record{
							value: `{"characters":[{"id":"existing","slot":0,"name":"First","created_at":0}]}`, version: "seed",
						}
					}
					logger := &rosterGameLogger{}
					out, err := rpc.call(ctx, logger, st)
					if err != tt.wantErr {
						t.Fatalf("RPC = %q, %v; want error %v", out, err, tt.wantErr)
					}
					if tt.wantErr != nil {
						if out != "" || len(st.objects) != 0 {
							t.Fatalf("rejected RPC output/objects = %q/%+v", out, st.objects)
						}
						assertRosterCollections(t, st, "", 0, 0)
						if tt.wantErr == ErrInternal {
							if len(logger.errors) != 1 || !strings.Contains(logger.errors[0], rpc.name) || !strings.Contains(logger.errors[0], "Bad") {
								t.Fatalf("configuration log = %v", logger.errors)
							}
						}
						return
					}
					assertRosterCollections(t, st, tt.collection, 1, rpc.writes)
					if len(st.objects) != 1 || st.objects[objKey(tt.collection, RosterKey, user)] == nil {
						t.Fatalf("RPC stored objects = %+v", st.objects)
					}
					rpc.check(t, out)
				})
			}
		})
	}
}
