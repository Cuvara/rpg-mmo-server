package redisstore

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"reflect"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

type registryFixture struct {
	HashFields   []string `json:"hash_fields"`
	MapIndexKeys []struct {
		Game  string `json:"game"`
		MapID string `json:"map_id"`
		Key   string `json:"key"`
	} `json:"map_index_keys"`
}

func readRegistryFixture(t *testing.T) registryFixture {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("..", "..", "testdata", "multigame", "registry.json"))
	if err != nil {
		t.Fatalf("read registry fixture: %v", err)
	}
	var fixture registryFixture
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatalf("parse registry fixture: %v", err)
	}
	if len(fixture.HashFields) != 8 || len(fixture.MapIndexKeys) != 4 {
		t.Fatalf("fixture has %d fields and %d map keys, want 8 and 4", len(fixture.HashFields), len(fixture.MapIndexKeys))
	}
	return fixture
}

func TestRegistryHashFieldsFixture(t *testing.T) {
	fixture := readRegistryFixture(t)
	if !slices.Equal(registryHashFields, fixture.HashFields) {
		t.Errorf("registry hash fields = %v, want %v", registryHashFields, fixture.HashFields)
	}
}

func TestMapIndexKeyFixture(t *testing.T) {
	for _, tt := range readRegistryFixture(t).MapIndexKeys {
		t.Run(tt.Game+"/"+tt.MapID, func(t *testing.T) {
			if got := MapIndexKey(tt.Game, tt.MapID); got != tt.Key {
				t.Errorf("MapIndexKey(%q, %q) = %q, want %q", tt.Game, tt.MapID, got, tt.Key)
			}
		})
	}
}

func TestRegisterScriptFixture(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "..", "testdata", "multigame", "register.lua"))
	if err != nil {
		t.Fatalf("read registration script fixture: %v", err)
	}
	normalize := func(s string) string { return strings.TrimSpace(strings.ReplaceAll(s, "\r\n", "\n")) }
	if got, want := normalize(registerScriptSource), normalize(string(data)); got != want {
		t.Errorf("Go registration script differs from shared fixture\ngot:\n%s\nwant:\n%s", got, want)
	}
}

func TestRedisServerRegistry_DefaultGameHashFields(t *testing.T) {
	fixture := readRegistryFixture(t)
	for _, gameID := range []string{"", constants.DefaultGameID} {
		t.Run(gameID, func(t *testing.T) {
			_, reg := newTestRegistry(t)
			ctx := context.Background()
			info := testServer("gs-1", "map_01")
			info.GameID = gameID
			info.Transport, info.PlayerCount, info.IdentityKey = "kcp", 7, "identity"
			if err := reg.Register(ctx, info); err != nil {
				t.Fatalf("Register(): %v", err)
			}
			fields, err := reg.client.HGetAll(ctx, serverKey(info.ServerID)).Result()
			if err != nil {
				t.Fatalf("HGetAll(): %v", err)
			}
			keys := make([]string, 0, len(fields))
			for key := range fields {
				keys = append(keys, key)
			}
			wantKeys := slices.Clone(fixture.HashFields)
			slices.Sort(keys)
			slices.Sort(wantKeys)
			if !slices.Equal(keys, wantKeys) {
				t.Errorf("hash fields = %v, want %v", keys, wantKeys)
			}
			want := map[string]string{
				"server_id": info.ServerID, "map_id": info.MapID, "addr": info.Addr,
				"transport": "kcp", "capacity": "100", "player_count": "7",
				"identity_key": "identity", "game_id": constants.DefaultGameID,
			}
			if !reflect.DeepEqual(fields, want) {
				t.Errorf("hash = %v, want %v", fields, want)
			}
		})
	}
}

func TestRedisServerRegistry_LegacyGameReads(t *testing.T) {
	for _, fieldPresent := range []bool{false, true} {
		t.Run(map[bool]string{false: "absent", true: "empty"}[fieldPresent], func(t *testing.T) {
			_, reg := newTestRegistry(t)
			ctx := context.Background()
			fields := map[string]any{"server_id": "legacy", "map_id": "map_01", "addr": "old:9000"}
			if fieldPresent {
				fields["game_id"] = ""
			}
			if err := reg.client.HSet(ctx, serverKey("legacy"), fields).Err(); err != nil {
				t.Fatal(err)
			}
			if err := reg.client.SAdd(ctx, mapKey("map_01"), "legacy").Err(); err != nil {
				t.Fatal(err)
			}
			tests := []struct {
				name string
				read func() ([]storage.ServerInfo, error)
			}{
				{"GetServer", func() ([]storage.ServerInfo, error) {
					info, err := reg.GetServer(ctx, "legacy")
					return []storage.ServerInfo{info}, err
				}},
				{"FindByMapID", func() ([]storage.ServerInfo, error) { return reg.FindByMapID(ctx, "map_01") }},
				{"FindByGameAndMapID", func() ([]storage.ServerInfo, error) { return reg.FindByGameAndMapID(ctx, "", "map_01") }},
			}
			for _, tt := range tests {
				t.Run(tt.name, func(t *testing.T) {
					got, err := tt.read()
					if err != nil || len(got) != 1 {
						t.Fatalf("read = %v, %v, want one legacy server", got, err)
					}
					if got[0].GameID != constants.DefaultGameID {
						t.Errorf("GameID = %q, want %q", got[0].GameID, constants.DefaultGameID)
					}
				})
			}
		})
	}
}

func TestRedisServerRegistry_RegisterGameOwnership(t *testing.T) {
	tests := []struct {
		name          string
		exists        bool
		gameField     bool
		existingGame  string
		requestedGame string
		mismatch      bool
	}{
		{"other game", true, true, "foo", "bar", true},
		{"other game to default", true, true, "foo", "", true},
		{"same non-default game", true, true, "foo", "foo", false},
		{"same default game", true, true, constants.DefaultGameID, "", false},
		{"absent game to default", true, false, "", "", false},
		{"empty game to default", true, true, "", constants.DefaultGameID, false},
		{"absent game to other", true, false, "", "foo", true},
		{"empty game to other", true, true, "", "foo", true},
		{"absent hash to other", false, false, "", "foo", false},
		{"absent hash to default", false, false, "", "", false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			mr, reg := newTestRegistry(t)
			ctx := context.Background()
			key := serverKey("gs-1")
			if tt.exists {
				fields := map[string]any{"server_id": "gs-1", "map_id": "map_01", "addr": "old:9000", "identity_key": "old-key"}
				if tt.gameField {
					fields["game_id"] = tt.existingGame
				}
				if err := reg.client.HSet(ctx, key, fields).Err(); err != nil {
					t.Fatal(err)
				}
				if err := reg.client.PExpire(ctx, key, reg.ttl).Err(); err != nil {
					t.Fatal(err)
				}
				mr.FastForward(time.Second)
			}
			before, err := reg.client.HGetAll(ctx, key).Result()
			if err != nil {
				t.Fatal(err)
			}
			ttlBefore := mr.TTL(key)
			info := testServer("gs-1", "map_01")
			info.GameID = tt.requestedGame
			err = reg.Register(ctx, info)
			if tt.mismatch {
				if !errors.Is(err, storage.ErrGameMismatch) {
					t.Fatalf("Register() error = %v, want ErrGameMismatch", err)
				}
				after, err := reg.client.HGetAll(ctx, key).Result()
				if err != nil {
					t.Fatal(err)
				}
				if !reflect.DeepEqual(after, before) || mr.TTL(key) != ttlBefore {
					t.Errorf("refused registration changed hash/TTL: hash %v -> %v, TTL %v -> %v", before, after, ttlBefore, mr.TTL(key))
				}
			} else {
				if err != nil {
					t.Fatalf("Register(): %v", err)
				}
				got, err := reg.GetServer(ctx, "gs-1")
				if err != nil {
					t.Fatal(err)
				}
				info.GameID = constants.GameOrDefault(info.GameID)
				if got != info || mr.TTL(key) != reg.ttl {
					t.Errorf("Register() = %+v, TTL %v; want %+v, TTL %v", got, mr.TTL(key), info, reg.ttl)
				}
			}
			for _, gameID := range []string{"", "foo", "bar"} {
				idxKey := MapIndexKey(gameID, info.MapID)
				members, err := reg.client.SMembers(ctx, idxKey).Result()
				if err != nil {
					t.Fatal(err)
				}
				want := []string{}
				if !tt.mismatch && idxKey == MapIndexKey(tt.requestedGame, info.MapID) {
					want = []string{info.ServerID}
				}
				if !slices.Equal(members, want) {
					t.Errorf("index %s = %v, want %v", idxKey, members, want)
				}
			}
		})
	}
}

func TestRedisServerRegistry_GameLookupFiltersAndPrunes(t *testing.T) {
	for _, gameID := range []string{"", constants.DefaultGameID, "foo"} {
		t.Run(gameID, func(t *testing.T) {
			_, reg := newTestRegistry(t)
			ctx := context.Background()
			own := testServer("own", "map_01")
			own.GameID = gameID
			foreign := testServer("foreign", "map_01")
			foreign.GameID = "bar"
			for _, info := range []storage.ServerInfo{own, foreign} {
				if err := reg.Register(ctx, info); err != nil {
					t.Fatal(err)
				}
			}
			idxKey := MapIndexKey(gameID, "map_01")
			if err := reg.client.SAdd(ctx, idxKey, "foreign", "expired").Err(); err != nil {
				t.Fatal(err)
			}
			got, err := reg.FindByGameAndMapID(ctx, gameID, "map_01")
			if err != nil || len(got) != 1 || got[0].ServerID != "own" {
				t.Fatalf("FindByGameAndMapID() = %+v, %v, want own server only", got, err)
			}
			members, err := reg.client.SMembers(ctx, idxKey).Result()
			if err != nil {
				t.Fatal(err)
			}
			slices.Sort(members)
			if !slices.Equal(members, []string{"foreign", "own"}) {
				t.Errorf("index after lookup = %v, want live members only", members)
			}
			if constants.GameOrDefault(gameID) == constants.DefaultGameID {
				all, err := reg.FindByMapID(ctx, "map_01")
				if err != nil || len(all) != 1 || all[0].ServerID != "own" {
					t.Errorf("FindByMapID() = %+v, %v, want own default-game server only", all, err)
				}
			}
		})
	}
}

func TestRedisServerRegistry_DeregisterGameIndex(t *testing.T) {
	for _, gameID := range []string{"", constants.DefaultGameID, "foo"} {
		t.Run(gameID, func(t *testing.T) {
			_, reg := newTestRegistry(t)
			ctx := context.Background()
			info := testServer("gs-1", "map_01")
			info.GameID = gameID
			if err := reg.Register(ctx, info); err != nil {
				t.Fatal(err)
			}
			if gameID == "" {
				if err := reg.client.HDel(ctx, serverKey(info.ServerID), "game_id").Err(); err != nil {
					t.Fatal(err)
				}
			}
			if err := reg.Deregister(ctx, info.ServerID); err != nil {
				t.Fatal(err)
			}
			members, err := reg.client.SMembers(ctx, MapIndexKey(gameID, info.MapID)).Result()
			if err != nil || len(members) != 0 {
				t.Errorf("index after deregistration = %v, %v, want empty", members, err)
			}
			if _, err := reg.GetServer(ctx, info.ServerID); !errors.Is(err, storage.ErrNotFound) {
				t.Errorf("GetServer() after deregistration = %v, want ErrNotFound", err)
			}
		})
	}
}
