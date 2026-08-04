package registry

import (
	"context"
	"errors"
	"testing"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"
)

// newStore returns the ServerRegistry implementations under test.
func newStores(t *testing.T) map[string]storage.ServerRegistry {
	t.Helper()
	mr := miniredis.RunT(t)
	redisReg := redisstore.NewServerRegistry(mr.Addr(), "")
	t.Cleanup(func() { redisReg.Close() })
	return map[string]storage.ServerRegistry{
		"memory": storage.NewMemoryServerRegistry(),
		"redis":  redisReg,
	}
}

func TestRegistryService_FindServerLeastLoaded(t *testing.T) {
	tests := []struct {
		name    string
		servers []storage.ServerInfo
		want    string
		wantErr bool
	}{
		{
			name: "picks lowest player count",
			servers: []storage.ServerInfo{
				{ServerID: "a", MapID: "m", Addr: "a:9000", Capacity: 100, PlayerCount: 80},
				{ServerID: "b", MapID: "m", Addr: "b:9000", Capacity: 100, PlayerCount: 5},
				{ServerID: "c", MapID: "m", Addr: "c:9000", Capacity: 100, PlayerCount: 40},
			},
			want: "b",
		},
		{
			name: "skips full servers",
			servers: []storage.ServerInfo{
				{ServerID: "a", MapID: "m", Addr: "a:9000", Capacity: 10, PlayerCount: 10},
				{ServerID: "b", MapID: "m", Addr: "b:9000", Capacity: 10, PlayerCount: 9},
			},
			want: "b",
		},
		{
			name: "all full",
			servers: []storage.ServerInfo{
				{ServerID: "a", MapID: "m", Addr: "a:9000", Capacity: 1, PlayerCount: 1},
			},
			wantErr: true,
		},
		{
			name:    "no servers",
			servers: nil,
			wantErr: true,
		},
	}

	for storeName, store := range newStores(t) {
		for _, tt := range tests {
			t.Run(storeName+"/"+tt.name, func(t *testing.T) {
				ctx := context.Background()
				mapID := storeName + "-" + tt.name // isolate cases within one store
				svc := NewRegistryService(store)
				for _, s := range tt.servers {
					s.MapID = mapID
					if err := svc.RegisterServer(ctx, s); err != nil {
						t.Fatalf("RegisterServer: %v", err)
					}
				}

				got, err := svc.FindServer(ctx, mapID)
				if tt.wantErr {
					if err == nil {
						t.Fatalf("FindServer() = %+v, want error", got)
					}
					return
				}
				if err != nil {
					t.Fatalf("FindServer() error: %v", err)
				}
				if got.ServerID != tt.want {
					t.Errorf("ServerID = %q, want %q", got.ServerID, tt.want)
				}
			})
		}
	}
}

// fakeAllocator returns a canned server, mimicking an Agones allocation.
type fakeAllocator struct {
	info storage.ServerInfo
	err  error
	hits int
}

func (f *fakeAllocator) AllocateServer(_ context.Context, mapID string) (storage.ServerInfo, error) {
	f.hits++
	if f.err != nil {
		return storage.ServerInfo{}, f.err
	}
	info := f.info
	info.MapID = mapID
	return info, nil
}

func TestRegistryService_AllocatorFallback(t *testing.T) {
	ctx := context.Background()

	t.Run("allocates when no capacity", func(t *testing.T) {
		alloc := &fakeAllocator{info: storage.ServerInfo{ServerID: "new1", Addr: "10.0.0.9:9000", Capacity: 50}}
		svc := NewRegistryServiceWithAllocator(storage.NewMemoryServerRegistry(), alloc)

		got, err := svc.FindServer(ctx, "map_empty")
		if err != nil {
			t.Fatalf("FindServer() error: %v", err)
		}
		if got.ServerID != "new1" {
			t.Errorf("ServerID = %q, want %q", got.ServerID, "new1")
		}
		if alloc.hits != 1 {
			t.Errorf("allocator hits = %d, want 1", alloc.hits)
		}
		// The allocated server must be registered for the next lookup.
		again, err := svc.FindServer(ctx, "map_empty")
		if err != nil {
			t.Fatalf("second FindServer() error: %v", err)
		}
		if again.ServerID != "new1" || alloc.hits != 1 {
			t.Errorf("expected cached registry hit, got %q hits=%d", again.ServerID, alloc.hits)
		}
	})

	t.Run("not used when a server has capacity", func(t *testing.T) {
		store := storage.NewMemoryServerRegistry()
		alloc := &fakeAllocator{info: storage.ServerInfo{ServerID: "new1"}}
		svc := NewRegistryServiceWithAllocator(store, alloc)
		if err := svc.RegisterServer(ctx, storage.ServerInfo{
			ServerID: "srv1", MapID: "map_forest", Addr: "a:9000", Capacity: 10,
		}); err != nil {
			t.Fatalf("RegisterServer: %v", err)
		}
		got, err := svc.FindServer(ctx, "map_forest")
		if err != nil {
			t.Fatalf("FindServer() error: %v", err)
		}
		if got.ServerID != "srv1" || alloc.hits != 0 {
			t.Errorf("got %q hits=%d, want srv1 hits=0", got.ServerID, alloc.hits)
		}
	})

	t.Run("allocator error surfaces", func(t *testing.T) {
		alloc := &fakeAllocator{err: errors.New("agones unavailable")}
		svc := NewRegistryServiceWithAllocator(storage.NewMemoryServerRegistry(), alloc)
		if _, err := svc.FindServer(ctx, "map_x"); err == nil {
			t.Error("FindServer() should fail when the allocator fails")
		}
	})

	t.Run("stub allocator degrades to error", func(t *testing.T) {
		svc := NewRegistryServiceWithAllocator(storage.NewMemoryServerRegistry(), &StubAllocator{})
		if _, err := svc.FindServer(ctx, "map_x"); err == nil {
			t.Error("FindServer() should fail with the stub allocator")
		}
	})
}

func TestRegistryService_GetServer(t *testing.T) {
	ctx := context.Background()
	for name, store := range newStores(t) {
		t.Run(name, func(t *testing.T) {
			svc := NewRegistryService(store)
			info := storage.ServerInfo{ServerID: "g1", MapID: "map_g", Addr: "g:9000", Capacity: 10}
			if err := svc.RegisterServer(ctx, info); err != nil {
				t.Fatalf("RegisterServer: %v", err)
			}
			got, err := svc.GetServer(ctx, "g1")
			if err != nil {
				t.Fatalf("GetServer() error: %v", err)
			}
			if got.Addr != "g:9000" {
				t.Errorf("Addr = %q, want %q", got.Addr, "g:9000")
			}
			// Note: MemoryServerRegistry reports a plain error while the Redis
			// impl wraps storage.ErrNotFound, so only assert that it fails.
			if _, err := svc.GetServer(ctx, "missing"); err == nil {
				t.Error("GetServer(missing) should fail")
			}
		})
	}
}
