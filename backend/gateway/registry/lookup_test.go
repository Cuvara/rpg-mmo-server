package registry

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
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

// concurrentAllocator counts allocations atomically and, optionally, registers
// the allocated server after a delay. It also blocks for `hold` so that
// concurrent callers genuinely overlap rather than serialising by luck.
type concurrentAllocator struct {
	hits atomic.Int32

	hold          time.Duration
	err           error
	store         storage.ServerRegistry
	registerAfter time.Duration
	info          storage.ServerInfo
}

func (c *concurrentAllocator) AllocateServer(_ context.Context, mapID string) (storage.ServerInfo, error) {
	c.hits.Add(1)
	time.Sleep(c.hold)
	if c.err != nil {
		return storage.ServerInfo{}, c.err
	}
	info := c.info
	info.MapID = mapID
	info.ServerID = c.info.ServerID + "-" + mapID
	if c.store != nil {
		self := info
		time.AfterFunc(c.registerAfter, func() { _ = c.store.Register(context.Background(), self) })
	}
	return info, nil
}

// Concurrent joins on one unserved map must produce ONE allocation, not one per
// caller. Without this, the retry we tell clients to perform amplifies: each
// retry of "server is starting" allocates another pod, only one can win the
// map_id registration (ADR-2) and nothing deallocates the losers.
func TestRegistryService_AllocationIsSingleFlightPerMap(t *testing.T) {
	const callers = 10
	store := storage.NewMemoryServerRegistry()
	alloc := &concurrentAllocator{
		hold:          50 * time.Millisecond, // guarantees overlap
		store:         store,
		registerAfter: 60 * time.Millisecond,
		info:          storage.ServerInfo{ServerID: "gs", Addr: "10.0.0.9:7257", Capacity: 50},
	}
	svc := NewRegistryServiceWithAllocator(store, alloc, WithAllocationWait(2*time.Second, 5*time.Millisecond))

	type outcome struct {
		info storage.ServerInfo
		err  error
	}
	results := make([]outcome, callers)
	start := make(chan struct{})
	var wg sync.WaitGroup
	for i := 0; i < callers; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			<-start // release everyone at once
			info, err := svc.FindServer(context.Background(), "map_rush")
			results[i] = outcome{info, err}
		}(i)
	}
	close(start)
	wg.Wait()

	if got := alloc.hits.Load(); got != 1 {
		t.Errorf("allocator hits = %d, want exactly 1 for %d concurrent callers", got, callers)
	}
	for i, r := range results {
		if r.err != nil {
			t.Fatalf("caller %d: FindServer() error: %v", i, r.err)
		}
		if r.info.ServerID != "gs-map_rush" || r.info.Addr != "10.0.0.9:7257" {
			t.Errorf("caller %d got %+v, want the single allocated server", i, r.info)
		}
	}
}

// Single-flight must be per map: a slow allocation for one map cannot block
// another map's.
func TestRegistryService_SingleFlightIsPerMap(t *testing.T) {
	store := storage.NewMemoryServerRegistry()
	alloc := &concurrentAllocator{
		hold:          30 * time.Millisecond,
		store:         store,
		registerAfter: 35 * time.Millisecond,
		info:          storage.ServerInfo{ServerID: "gs", Addr: "10.0.0.9:7257", Capacity: 50},
	}
	svc := NewRegistryServiceWithAllocator(store, alloc, WithAllocationWait(2*time.Second, 5*time.Millisecond))

	var wg sync.WaitGroup
	errs := make([]error, 2)
	for i, mapID := range []string{"map_a", "map_b"} {
		wg.Add(1)
		go func(i int, mapID string) {
			defer wg.Done()
			_, errs[i] = svc.FindServer(context.Background(), mapID)
		}(i, mapID)
	}
	wg.Wait()

	for i, err := range errs {
		if err != nil {
			t.Fatalf("map %d: FindServer() error: %v", i, err)
		}
	}
	if got := alloc.hits.Load(); got != 2 {
		t.Errorf("allocator hits = %d, want 2 (one per map)", got)
	}
}

// A failed allocation must not be cached: the next caller has to be free to try
// again, or one transient failure poisons the map until the gateway restarts.
func TestRegistryService_FailedAllocationIsNotCached(t *testing.T) {
	store := storage.NewMemoryServerRegistry()
	alloc := &concurrentAllocator{err: errors.New("fleet exhausted")}
	svc := NewRegistryServiceWithAllocator(store, alloc, WithAllocationWait(50*time.Millisecond, 5*time.Millisecond))

	for i := 0; i < 3; i++ {
		if _, err := svc.FindServer(context.Background(), "map_flaky"); !errors.Is(err, ErrNoServerAvailable) {
			t.Fatalf("attempt %d: error = %v, want ErrNoServerAvailable", i, err)
		}
	}
	if got := alloc.hits.Load(); got != 3 {
		t.Errorf("allocator hits = %d, want 3 (a failure must not be cached)", got)
	}
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
