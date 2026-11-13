package registry

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/testutil"
)

func TestRegistryService_FindServerGameIsolatesSameMap(t *testing.T) {
	ctx := context.Background()
	for name, store := range newStores(t) {
		t.Run(name, func(t *testing.T) {
			svc := NewRegistryService(store)
			for _, info := range []storage.ServerInfo{
				{ServerID: "legacy-server", MapID: "map_01", Capacity: 10},
				{GameID: "foo", ServerID: "foo-server", MapID: "map_01", Capacity: 10},
				{GameID: "bar", ServerID: "bar-server", MapID: "map_01", Capacity: 10},
			} {
				if err := svc.RegisterServer(ctx, info); err != nil {
					t.Fatal(err)
				}
			}
			for _, tt := range []struct{ game, want string }{
				{"", "legacy-server"}, {constants.DefaultGameID, "legacy-server"},
				{"foo", "foo-server"}, {"bar", "bar-server"},
			} {
				t.Run("game="+tt.game, func(t *testing.T) {
					got, err := svc.FindServerGame(ctx, tt.game, "map_01")
					if err != nil || got.ServerID != tt.want || got.GameID != constants.GameOrDefault(tt.game) {
						t.Fatalf("FindServerGame(%q) = %+v, %v; want %s", tt.game, got, err, tt.want)
					}
				})
			}
			got, err := svc.FindServer(ctx, "map_01")
			if err != nil || got.ServerID != "legacy-server" {
				t.Fatalf("default wrapper = %+v, %v", got, err)
			}
		})
	}
}

type gameLog struct{ entries []string }

func (l *gameLog) Warn(msg string, args ...any) {
	l.entries = append(l.entries, msg+fmt.Sprint(args...))
}

func TestRegistryService_RejectsOtherGameIndexEntry(t *testing.T) {
	for _, tt := range []struct{ requested, stored string }{
		{"foo", "bar"}, {"foo", ""}, {constants.DefaultGameID, "foo"},
	} {
		t.Run(tt.requested+"/"+tt.stored, func(t *testing.T) {
			store := &lyingRegistry{lie: storage.ServerInfo{
				GameID: tt.stored, ServerID: "wrong-server", MapID: "map_01", Addr: "wrong:7777", Capacity: 10,
			}}
			alloc := &fakeAllocator{}
			log := &gameLog{}
			svc := NewRegistryServiceWithAllocator(store, alloc, WithLogger(log))
			got, err := svc.FindServerGame(context.Background(), tt.requested, "map_01")
			if !errors.Is(err, ErrFleetMapMismatch) || got != (storage.ServerInfo{}) || alloc.hits != 0 {
				t.Fatalf("lookup = %+v, %v; allocations=%d", got, err, alloc.hits)
			}
			for _, game := range []string{tt.requested, constants.GameOrDefault(tt.stored)} {
				if !strings.Contains(err.Error(), game) || !strings.Contains(strings.Join(log.entries, " "), game) {
					t.Errorf("error and log must name game %q: %v, %v", game, err, log.entries)
				}
			}
		})
	}
}

// gameAllocator lets tests exercise the service's game boundary independently
// of the Agones HTTP adapter.
type gameAllocator struct {
	mapFn  func(context.Context, string, string) (storage.ServerInfo, error)
	kindFn func(context.Context, AllocationRequest) (storage.ServerInfo, error)
}

func (a *gameAllocator) AllocateServer(ctx context.Context, game, mapID string) (storage.ServerInfo, error) {
	return a.mapFn(ctx, game, mapID)
}

func (a *gameAllocator) Allocate(ctx context.Context, req AllocationRequest) (storage.ServerInfo, error) {
	return a.kindFn(ctx, req)
}

func TestRegistryService_MismatchMemoryIsPerGameAndMap(t *testing.T) {
	ctx := context.Background()
	store := storage.NewMemoryServerRegistry()
	hits := map[string]int{}
	alloc := &gameAllocator{mapFn: func(ctx context.Context, game, mapID string) (storage.ServerInfo, error) {
		hits[game]++
		info := storage.ServerInfo{GameID: game, ServerID: game + "-server", MapID: mapID, Capacity: 10}
		if game == "foo" {
			info.MapID = "wrong_map"
		}
		return info, store.Register(ctx, info)
	}}
	svc := NewRegistryServiceWithAllocator(store, alloc, fastWait())
	for _, tt := range []struct {
		game string
		want error
	}{
		{"foo", ErrFleetMapMismatch}, {"foo", ErrFleetMapMismatch}, {"bar", nil},
	} {
		got, err := svc.FindServerGame(ctx, tt.game, "map_01")
		if !errors.Is(err, tt.want) || (err == nil && got.GameID != tt.game) {
			t.Fatalf("game %q: got %+v, %v; want %v", tt.game, got, err, tt.want)
		}
	}
	if hits["foo"] != 1 || hits["bar"] != 1 {
		t.Fatalf("allocations = %v; want exactly one per game", hits)
	}
}

func TestRegistryService_SingleFlightIsPerGameAndMap(t *testing.T) {
	store := storage.NewMemoryServerRegistry()
	started := make(chan string, 2)
	release := make(chan struct{})
	var releaseOnce sync.Once
	unblock := func() { releaseOnce.Do(func() { close(release) }) }
	defer unblock()
	alloc := &gameAllocator{mapFn: func(ctx context.Context, game, mapID string) (storage.ServerInfo, error) {
		started <- game
		<-release
		info := storage.ServerInfo{GameID: game, ServerID: game + "-server", MapID: mapID, Capacity: 10}
		return info, store.Register(ctx, info)
	}}
	svc := NewRegistryServiceWithAllocator(store, alloc, fastWait())
	results := make(chan error, 2)
	for _, game := range []string{"foo", "bar"} {
		go func(game string) {
			info, err := svc.FindServerGame(context.Background(), game, "map_01")
			if err == nil && info.GameID != game {
				err = fmt.Errorf("game %q got server for %q", game, info.GameID)
			}
			results <- err
		}(game)
	}
	seen := map[string]bool{}
	for range 2 {
		select {
		case game := <-started:
			seen[game] = true
		case <-time.After(2 * time.Second):
			t.Fatal("one game's in-flight allocation blocked another game's allocation")
		}
	}
	unblock()
	for range 2 {
		if err := <-results; err != nil {
			t.Fatal(err)
		}
	}
	if !seen["foo"] || !seen["bar"] {
		t.Fatalf("allocations started for %v; want foo and bar", seen)
	}
}

func TestRegistryService_AllocationGameBoundaryAndMetrics(t *testing.T) {
	for _, kind := range []string{KindMap, KindDungeon} {
		for _, tt := range []struct {
			name, registeredGame string
			allocErr             error
			missing              bool
			wantErr              error
		}{
			{name: "same game", registeredGame: "foo"},
			{name: "other game", registeredGame: "bar", wantErr: ErrFleetMapMismatch},
			{name: "absent game belongs to cuvara", wantErr: ErrFleetMapMismatch},
			{name: "allocation fails", allocErr: ErrNoCapacity, wantErr: ErrNoCapacity},
			{name: "registration times out", missing: true, wantErr: ErrServerStarting},
		} {
			t.Run(kind+"/"+tt.name, func(t *testing.T) {
				store := storage.NewMemoryServerRegistry()
				m := metrics.New(prometheus.NewRegistry())
				m.SetGames([]string{constants.DefaultGameID, "foo", "bar"})
				allocate := func(ctx context.Context, game, mapID string) (storage.ServerInfo, error) {
					if game != "foo" || mapID != "map_01" {
						return storage.ServerInfo{}, fmt.Errorf("wrong allocation request: %q %q", game, mapID)
					}
					info := storage.ServerInfo{GameID: tt.registeredGame, ServerID: "foo-server", MapID: mapID, Capacity: 10}
					if tt.allocErr != nil {
						return storage.ServerInfo{}, tt.allocErr
					}
					if !tt.missing {
						if err := store.Register(ctx, info); err != nil {
							return storage.ServerInfo{}, err
						}
					}
					return info, nil
				}
				alloc := &gameAllocator{mapFn: allocate, kindFn: func(ctx context.Context, req AllocationRequest) (storage.ServerInfo, error) {
					if req.Kind != KindDungeon {
						return storage.ServerInfo{}, fmt.Errorf("wrong kind %q", req.Kind)
					}
					return allocate(ctx, req.Game, req.MapID)
				}}
				svc := NewRegistryServiceWithAllocator(store, alloc, WithMetrics(m),
					WithAllocationWait(20*time.Millisecond, time.Millisecond))
				var info storage.ServerInfo
				var err error
				if kind == KindMap {
					info, err = svc.FindServerGame(context.Background(), "foo", "map_01")
				} else {
					info, err = svc.AllocateDungeon(context.Background(), "foo", "map_01")
				}
				if !errors.Is(err, tt.wantErr) {
					t.Fatalf("allocation = %+v, %v; want %v", info, err, tt.wantErr)
				}
				if err != nil && info != (storage.ServerInfo{}) {
					t.Fatalf("failed allocation leaked server: %+v", info)
				}
				for _, game := range []string{constants.DefaultGameID, "foo", "bar"} {
					for _, result := range []string{metrics.ResultOK, metrics.ResultFail} {
						want := float64(0)
						if game == "foo" && ((err == nil) == (result == metrics.ResultOK)) {
							want = 1
						}
						if got := testutil.ToFloat64(m.AllocationsTotal.WithLabelValues(game, result)); got != want {
							t.Errorf("allocations{game=%q,result=%q} = %v; want %v", game, result, got, want)
						}
					}
				}
			})
		}
	}
}
