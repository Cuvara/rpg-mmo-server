package registry

import (
	"context"
	"errors"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

// Evict must remove the server from both the registry (so FindServer stops
// returning it immediately, instead of waiting out the heartbeat TTL) and the
// watcher's tracked set — and must not stand in the way of the server
// re-registering later (#236).
func TestRegistryService_EvictRemovesServerFromAssignment(t *testing.T) {
	ctx := context.Background()
	mem := storage.NewMemoryServerRegistry()
	pubsub := NewMemoryPubSub()
	watcher := NewRegistryWatcher(mem, pubsub, &spyLogger{})
	svc := NewRegistryService(mem, WithWatcher(watcher))

	info := storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	}
	if err := svc.RegisterServer(ctx, info); err != nil {
		t.Fatalf("RegisterServer() error: %v", err)
	}

	got, err := svc.FindServer(ctx, "map_01")
	if err != nil {
		t.Fatalf("FindServer() before eviction error: %v", err)
	}
	if got.ServerID != "srv1" {
		t.Fatalf("FindServer() = %q, want srv1", got.ServerID)
	}
	if watcher.KnownCount() != 1 {
		t.Fatalf("KnownCount() = %d, want 1", watcher.KnownCount())
	}

	if err := svc.Evict(ctx, "srv1"); err != nil {
		t.Fatalf("Evict() error: %v", err)
	}

	if _, err := svc.FindServer(ctx, "map_01"); !errors.Is(err, ErrNoServerAvailable) {
		t.Errorf("FindServer() after eviction error = %v, want ErrNoServerAvailable", err)
	}
	if servers, _ := mem.FindByMapID(ctx, "map_01"); len(servers) != 0 {
		t.Errorf("FindByMapID() after eviction returned %d servers, want 0", len(servers))
	}
	if watcher.KnownCount() != 0 {
		t.Errorf("KnownCount() = %d, want 0 after eviction", watcher.KnownCount())
	}

	// Idempotent: evicting a server that is already gone is a success. Several
	// gateway instances consume the same server_down event.
	if err := svc.Evict(ctx, "srv1"); err != nil {
		t.Errorf("Evict() second call error: %v, want nil", err)
	}

	// A server that comes back is alive by definition: re-registration makes it
	// assignable again through the normal path (heartbeat self-heal intact).
	if err := svc.RegisterServer(ctx, info); err != nil {
		t.Fatalf("RegisterServer() after eviction error: %v", err)
	}
	got, err = svc.FindServer(ctx, "map_01")
	if err != nil {
		t.Fatalf("FindServer() after re-register error: %v", err)
	}
	if got.ServerID != "srv1" {
		t.Errorf("FindServer() after re-register = %q, want srv1", got.ServerID)
	}
	if watcher.KnownCount() != 1 {
		t.Errorf("KnownCount() = %d, want 1 after re-register", watcher.KnownCount())
	}
}

// Evict without a watcher attached (registry-only construction) must work.
func TestRegistryService_EvictWithoutWatcher(t *testing.T) {
	ctx := context.Background()
	mem := storage.NewMemoryServerRegistry()
	svc := NewRegistryService(mem)

	_ = svc.RegisterServer(ctx, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	})
	if err := svc.Evict(ctx, "srv1"); err != nil {
		t.Fatalf("Evict() error: %v", err)
	}
	if _, err := svc.FindServer(ctx, "map_01"); !errors.Is(err, ErrNoServerAvailable) {
		t.Errorf("FindServer() after eviction error = %v, want ErrNoServerAvailable", err)
	}
}
