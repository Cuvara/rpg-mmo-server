package storage_test

import (
	"context"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"
	"github.com/redis/go-redis/v9"
)

func TestServerRegistry_GameMapLookupParity(t *testing.T) {
	backends := []struct {
		name string
		new  func(*testing.T) storage.ServerRegistry
	}{
		{"memory", func(t *testing.T) storage.ServerRegistry {
			return storage.NewMemoryServerRegistry()
		}},
		{"redis", func(t *testing.T) storage.ServerRegistry {
			mr := miniredis.RunT(t)
			client := redis.NewClient(&redis.Options{Addr: mr.Addr()})
			t.Cleanup(func() { _ = client.Close() })
			return redisstore.NewServerRegistryWithClient(client, time.Minute)
		}},
	}
	for _, backend := range backends {
		t.Run(backend.name, func(t *testing.T) {
			ctx := context.Background()
			reg := backend.new(t)
			for _, info := range []storage.ServerInfo{
				{ServerID: "gs-default", MapID: "map_01"},
				{ServerID: "foo-map-01", MapID: "map_01", GameID: "foo"},
				{ServerID: "foo-map-02", MapID: "map_02", GameID: "foo"},
			} {
				if err := reg.Register(ctx, info); err != nil {
					t.Fatalf("Register(%q): %v", info.ServerID, err)
				}
			}
			tests := []struct {
				name       string
				gameID     string
				mapID      string
				legacy     bool
				wantServer string
			}{
				{"implicit default", "", "map_01", false, "gs-default"},
				{"explicit default", constants.DefaultGameID, "map_01", false, "gs-default"},
				{"other game same map", "foo", "map_01", false, "foo-map-01"},
				{"other map", "foo", "map_02", false, "foo-map-02"},
				{"legacy default only", "", "map_01", true, "gs-default"},
				{"unknown game", "unknown", "map_01", false, ""},
				{"unknown map", "foo", "map_99", false, ""},
			}
			for _, tt := range tests {
				t.Run(tt.name, func(t *testing.T) {
					var got []storage.ServerInfo
					var err error
					if tt.legacy {
						got, err = reg.FindByMapID(ctx, tt.mapID)
					} else {
						got, err = reg.FindByGameAndMapID(ctx, tt.gameID, tt.mapID)
					}
					if err != nil {
						t.Fatalf("lookup: %v", err)
					}
					if tt.wantServer == "" {
						if len(got) != 0 {
							t.Errorf("lookup = %+v, want no servers", got)
						}
						return
					}
					if len(got) != 1 || got[0].ServerID != tt.wantServer || got[0].GameID != constants.GameOrDefault(tt.gameID) {
						t.Errorf("lookup = %+v, want server %q for game %q", got, tt.wantServer, constants.GameOrDefault(tt.gameID))
					}
				})
			}
		})
	}
}
