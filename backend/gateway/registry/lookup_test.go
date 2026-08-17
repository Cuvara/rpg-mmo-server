package registry

import (
	"context"
	"errors"
	"testing"
	"time"

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
//
// selfRegister stands in for the game server's own RegistrationService: when
// set, the allocated pod publishes its entry into that store after
// registerAfter. The entry it writes is deliberately NOT identical to the
// allocation response (see selfAddr/selfTransport) so tests can prove which of
// the two the gateway announced to the client.
type fakeAllocator struct {
	info storage.ServerInfo
	err  error
	hits int

	selfRegister  storage.ServerRegistry
	registerAfter time.Duration
	selfAddr      string
	selfTransport string
}

func (f *fakeAllocator) AllocateServer(_ context.Context, mapID string) (storage.ServerInfo, error) {
	f.hits++
	if f.err != nil {
		return storage.ServerInfo{}, f.err
	}
	info := f.info
	info.MapID = mapID
	if f.selfRegister != nil {
		self := info
		if f.selfAddr != "" {
			self.Addr = f.selfAddr
		}
		if f.selfTransport != "" {
			self.Transport = f.selfTransport
		}
		time.AfterFunc(f.registerAfter, func() {
			_ = f.selfRegister.Register(context.Background(), self)
		})
	}
	return info, nil
}

// fastWait keeps the allocation wait short enough for unit tests while leaving
// the production defaults alone.
func fastWait() Option { return WithAllocationWait(2*time.Second, 5*time.Millisecond) }

func TestRegistryService_AllocatorFallback(t *testing.T) {
	ctx := context.Background()

	t.Run("allocates when the map has no server at all", func(t *testing.T) {
		store := storage.NewMemoryServerRegistry()
		alloc := &fakeAllocator{
			info:          storage.ServerInfo{ServerID: "new1", Addr: "10.0.0.9:9000", Capacity: 50},
			selfRegister:  store,
			registerAfter: 10 * time.Millisecond,
			selfAddr:      "10.0.0.9:7257", // the port the pod actually got
		}
		svc := NewRegistryServiceWithAllocator(store, alloc, fastWait())

		got, err := svc.FindServer(ctx, "map_empty")
		if err != nil {
			t.Fatalf("FindServer() error: %v", err)
		}
		if got.ServerID != "new1" {
			t.Errorf("ServerID = %q, want %q", got.ServerID, "new1")
		}
		// The entry returned must be the one the server wrote about itself, not
		// the allocation response's guess.
		if got.Addr != "10.0.0.9:7257" {
			t.Errorf("Addr = %q, want the self-registered %q", got.Addr, "10.0.0.9:7257")
		}
		if alloc.hits != 1 {
			t.Errorf("allocator hits = %d, want 1", alloc.hits)
		}
		// The now-live server serves the next lookup without another allocation.
		again, err := svc.FindServer(ctx, "map_empty")
		if err != nil {
			t.Fatalf("second FindServer() error: %v", err)
		}
		if again.ServerID != "new1" || alloc.hits != 1 {
			t.Errorf("expected registry hit, got %q hits=%d", again.ServerID, alloc.hits)
		}
	})

	// ADR-2: allocation replaces an absent server, it never adds capacity to a
	// full one. A second live server for one map_id is a split world.
	t.Run("does not allocate when live servers are all full", func(t *testing.T) {
		store := storage.NewMemoryServerRegistry()
		alloc := &fakeAllocator{info: storage.ServerInfo{ServerID: "new1", Addr: "10.0.0.9:9000", Capacity: 50}}
		svc := NewRegistryServiceWithAllocator(store, alloc, fastWait())
		if err := svc.RegisterServer(ctx, storage.ServerInfo{
			ServerID: "srv1", MapID: "map_full", Addr: "a:9000", Capacity: 10, PlayerCount: 10,
		}); err != nil {
			t.Fatalf("RegisterServer: %v", err)
		}

		_, err := svc.FindServer(ctx, "map_full")
		if !errors.Is(err, ErrNoServerAvailable) {
			t.Fatalf("FindServer() error = %v, want ErrNoServerAvailable", err)
		}
		if alloc.hits != 0 {
			t.Errorf("allocator hits = %d, want 0 (a full map must not gain a second server)", alloc.hits)
		}
	})

	// The gateway must not write the allocated server's entry on its behalf
	// (ADR-1: one writer per datum), so a pod that never registers must fail the
	// join as retryable rather than announcing an address nobody answers on.
	t.Run("allocated server that never registers is retryable", func(t *testing.T) {
		store := storage.NewMemoryServerRegistry()
		alloc := &fakeAllocator{info: storage.ServerInfo{ServerID: "ghost", Addr: "10.0.0.9:9000", Capacity: 50}}
		svc := NewRegistryServiceWithAllocator(store, alloc, WithAllocationWait(50*time.Millisecond, 5*time.Millisecond))

		start := time.Now()
		_, err := svc.FindServer(ctx, "map_ghost")
		if !errors.Is(err, ErrServerStarting) {
			t.Fatalf("FindServer() error = %v, want ErrServerStarting", err)
		}
		if errors.Is(err, ErrNoServerAvailable) {
			t.Error("a booting server must not be reported as ErrNoServerAvailable")
		}
		if elapsed := time.Since(start); elapsed < 50*time.Millisecond {
			t.Errorf("returned after %s, want at least the 50ms wait window", elapsed)
		}
		// Nothing may have been written to the registry by the gateway.
		if _, gerr := store.GetServer(ctx, "ghost"); gerr == nil {
			t.Error("gateway wrote the allocated server's registry entry; only the game server may")
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

// countingRegistry counts GetServer calls so a test can prove the common path
// does not poll.
type countingRegistry struct {
	storage.ServerRegistry
	gets int
}

func (c *countingRegistry) GetServer(ctx context.Context, serverID string) (storage.ServerInfo, error) {
	c.gets++
	return c.ServerRegistry.GetServer(ctx, serverID)
}

// The hot path is an already-registered server. It must not pay any part of the
// allocation wait: no allocator call, no registry polling, no added latency.
func TestRegistryService_ExistingServerPathDoesNotPoll(t *testing.T) {
	ctx := context.Background()
	store := &countingRegistry{ServerRegistry: storage.NewMemoryServerRegistry()}
	alloc := &fakeAllocator{info: storage.ServerInfo{ServerID: "new1"}}
	// A pathological wait: if the fast path touched it at all, this test would
	// take a minute.
	svc := NewRegistryServiceWithAllocator(store, alloc, WithAllocationWait(time.Minute, time.Minute))

	if err := svc.RegisterServer(ctx, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_forest", Addr: "a:9000", Capacity: 10, PlayerCount: 1,
	}); err != nil {
		t.Fatalf("RegisterServer: %v", err)
	}

	start := time.Now()
	got, err := svc.FindServer(ctx, "map_forest")
	if err != nil {
		t.Fatalf("FindServer() error: %v", err)
	}
	if got.ServerID != "srv1" {
		t.Errorf("ServerID = %q, want srv1", got.ServerID)
	}
	if alloc.hits != 0 {
		t.Errorf("allocator hits = %d, want 0", alloc.hits)
	}
	if store.gets != 0 {
		t.Errorf("GetServer calls = %d, want 0 (the existing-server path must not poll)", store.gets)
	}
	if elapsed := time.Since(start); elapsed > time.Second {
		t.Errorf("FindServer took %s on the existing-server path", elapsed)
	}
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
