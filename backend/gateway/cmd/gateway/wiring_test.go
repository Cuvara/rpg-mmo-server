package main

import (
	"context"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// These tests guard the wiring itself, not the watcher's logic. registry's own
// unit tests construct a RegistryWatcher by hand, so they keep passing even when
// nothing in the binary builds one — which is exactly how issue #204 happened:
// the watcher existed, was tested, and never ran. Everything below fails if
// wireRegistry stops constructing, attaching or starting the watcher.

func testWireRegistry(t *testing.T, ctx context.Context, reg storage.ServerRegistry) (*registry.RegistryService, *registry.RegistryWatcher) {
	t.Helper()
	met, _ := metrics.NewDefault()
	stream := storage.NewMemoryEventStream()
	t.Cleanup(func() { _ = stream.Close() })
	svc, watcher := wireRegistry(ctx, reg, eventStreamPublisher{stream: stream}, met, logger.New("error"), nil)
	if svc == nil {
		t.Fatal("wireRegistry returned a nil RegistryService")
	}
	if watcher == nil {
		t.Fatal("wireRegistry returned a nil RegistryWatcher: the gateway would never notice a dead server before the registry TTL expires")
	}
	return svc, watcher
}

// TestWireRegistry_StartsWatcher proves the poll loop is actually running.
// RegistryWatcher.Stop blocks until pollLoop exits, and pollLoop only exists
// once Start has been called — so a watcher that was constructed but never
// started makes this hang and fail on the deadline.
func TestWireRegistry_StartsWatcher(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	_, watcher := testWireRegistry(t, ctx, storage.NewMemoryServerRegistry())

	cancel()

	done := make(chan struct{})
	go func() {
		watcher.Stop()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("watcher.Stop() did not return within 2s: the poll loop was never started by wireRegistry")
	}
}

// TestWireRegistry_TracksRegisteredServer proves the watcher is attached to the
// RegistryService the gateway actually uses: a watcher whose tracked set stays
// empty polls nothing and can never publish a server_down event.
func TestWireRegistry_TracksRegisteredServer(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	svc, watcher := testWireRegistry(t, ctx, storage.NewMemoryServerRegistry())

	if err := svc.RegisterServer(context.Background(), storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	}); err != nil {
		t.Fatalf("RegisterServer: %v", err)
	}
	if got := watcher.KnownCount(); got != 1 {
		t.Fatalf("watcher.KnownCount() = %d, want 1: the watcher is not attached to the wired RegistryService", got)
	}

	if err := svc.DeregisterServer(context.Background(), "srv1"); err != nil {
		t.Fatalf("DeregisterServer: %v", err)
	}
	if got := watcher.KnownCount(); got != 0 {
		t.Fatalf("watcher.KnownCount() = %d, want 0 after deregister: a graceful deregister must not be reported as a server_down", got)
	}
}

// TestWireRegistry_TracksServerHandedToClient covers the production path. Game
// servers self-register straight into the shared registry (Redis), so the
// gateway never calls RegisterServer itself; the moment it learns a server
// exists is when it hands its address to a client. If that did not track, the
// watcher would run with an empty set in every real deployment.
func TestWireRegistry_TracksServerHandedToClient(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	reg := storage.NewMemoryServerRegistry()
	if err := reg.Register(context.Background(), storage.ServerInfo{
		ServerID: "srv2", MapID: "map_02", Addr: "10.0.0.2:9000", Capacity: 100,
	}); err != nil {
		t.Fatalf("Register: %v", err)
	}

	svc, watcher := testWireRegistry(t, ctx, reg)

	if _, err := svc.FindServer(context.Background(), "map_02"); err != nil {
		t.Fatalf("FindServer: %v", err)
	}
	if got := watcher.KnownCount(); got != 1 {
		t.Fatalf("watcher.KnownCount() = %d, want 1: a server handed to a client is not being watched", got)
	}
}
