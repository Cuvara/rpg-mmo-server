package redisstore

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

func TestRedisServerRegistry_DeregisterForGame(t *testing.T) {
	ctx := context.Background()
	for _, game := range []string{"cuvara", ""} {
		t.Run("stale_"+game, func(t *testing.T) {
			mr, reg := newTestRegistry(t)
			info := testServer("ember-one", "m")
			info.GameID = "cuvara"
			if err := reg.Register(ctx, info); err != nil {
				t.Fatal(err)
			}
			mr.FastForward(constants.ServerHeartbeatTTL + time.Second)
			info.GameID = "ember"
			if err := reg.Register(ctx, info); err != nil {
				t.Fatal(err)
			}
			if err := reg.DeregisterForGame(ctx, game, info.ServerID); !errors.Is(err, storage.ErrGameMismatch) {
				t.Fatalf("got %v, want ErrGameMismatch", err)
			}
			got, err := reg.GetServer(ctx, info.ServerID)
			if err != nil || got.GameID != "ember" {
				t.Fatalf("server = %+v, error = %v", got, err)
			}
			member, err := reg.client.SIsMember(ctx, "servers:map:ember:m", info.ServerID).Result()
			if err != nil || !member {
				t.Fatalf("index member = %v, error = %v", member, err)
			}
		})
	}
	for _, tc := range []struct {
		name, stored, game string
		legacy             bool
	}{{"matching", "ember", "ember", false}, {"legacy_default", "", "cuvara", true}, {"legacy_empty", "", "", true}} {
		t.Run(tc.name, func(t *testing.T) {
			_, reg := newTestRegistry(t)
			info := testServer("ember-one", "m")
			info.GameID = tc.stored
			if err := reg.Register(ctx, info); err != nil {
				t.Fatal(err)
			}
			if tc.legacy {
				if err := reg.client.HDel(ctx, serverKey(info.ServerID), fieldGameID).Err(); err != nil {
					t.Fatal(err)
				}
			}
			if err := reg.DeregisterForGame(ctx, tc.game, info.ServerID); err != nil {
				t.Fatal(err)
			}
			if _, err := reg.GetServer(ctx, info.ServerID); !errors.Is(err, storage.ErrNotFound) {
				t.Fatalf("got %v, want ErrNotFound", err)
			}
			member, err := reg.client.SIsMember(ctx, MapIndexKey(tc.game, "m"), info.ServerID).Result()
			if err != nil || member {
				t.Fatalf("index member = %v, error = %v", member, err)
			}
			if err := reg.DeregisterForGame(ctx, tc.game, info.ServerID); err != nil {
				t.Fatalf("absent: %v", err)
			}
		})
	}
}
