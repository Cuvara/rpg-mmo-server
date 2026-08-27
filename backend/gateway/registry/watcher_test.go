package registry

import (
	"context"
	"encoding/json"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

// flakyRegistry wraps a ServerRegistry so tests can inject a transient
// GetServer failure (e.g. a Redis connection error) that is NOT ErrNotFound.
type flakyRegistry struct {
	storage.ServerRegistry
	getServerErr error
}

func (f *flakyRegistry) GetServer(ctx context.Context, serverID string) (storage.ServerInfo, error) {
	if f.getServerErr != nil {
		return storage.ServerInfo{}, f.getServerErr
	}
	return f.ServerRegistry.GetServer(ctx, serverID)
}

// A transient store error (anything that is not storage.ErrNotFound) must not
// be read as server death: nothing is published, the server stays tracked, and
// once the store recovers a real expiry is still detected exactly once (#236).
func TestRegistryWatcher_TransientErrorKeepsTracking(t *testing.T) {
	mem := storage.NewMemoryServerRegistry()
	ctx := context.Background()
	_ = mem.Register(ctx, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	})

	reg := &flakyRegistry{ServerRegistry: mem}
	pubsub := NewMemoryPubSub()
	spy := &spyLogger{}
	watcher := NewRegistryWatcher(reg, pubsub, spy)
	watcher.TrackServer("srv1", "map_01")

	// Store is blipping: every GetServer fails with a transport error.
	reg.getServerErr = errors.New("redis: connection refused")
	for i := 0; i < 3; i++ {
		watcher.checkServers(ctx)
	}
	if pubsub.MessageCount() != 0 {
		t.Fatalf("expected no server_down during transient errors, got %d", pubsub.MessageCount())
	}
	if watcher.KnownCount() != 1 {
		t.Fatalf("KnownCount() = %d, want 1 (server must stay tracked through a blip)", watcher.KnownCount())
	}
	if spy.calls.Load() == 0 {
		t.Error("expected the transient error to be logged")
	}

	// Store recovers, server is alive: still nothing.
	reg.getServerErr = nil
	watcher.checkServers(ctx)
	if pubsub.MessageCount() != 0 {
		t.Fatalf("expected no server_down after recovery with server alive, got %d", pubsub.MessageCount())
	}

	// Now the server really disappears (heartbeat expiry -> ErrNotFound):
	// exactly one server_down, and it is untracked.
	_ = mem.Deregister(ctx, "srv1")
	watcher.checkServers(ctx)
	watcher.checkServers(ctx)
	if pubsub.MessageCount() != 1 {
		t.Fatalf("expected exactly 1 server_down after real expiry, got %d", pubsub.MessageCount())
	}
	if watcher.KnownCount() != 0 {
		t.Errorf("KnownCount() = %d, want 0 after detection", watcher.KnownCount())
	}
}

// A wrapped ErrNotFound (every store implementation wraps it with context) must
// still be recognised as death, not misread as a transient error.
func TestRegistryWatcher_WrappedNotFoundStillDetected(t *testing.T) {
	mem := storage.NewMemoryServerRegistry()
	ctx := context.Background()

	pubsub := NewMemoryPubSub()
	watcher := NewRegistryWatcher(mem, pubsub, &spyLogger{})
	// Tracked but never registered: memory GetServer returns a wrapped ErrNotFound.
	watcher.TrackServer("ghost", "map_01")

	watcher.checkServers(ctx)
	if pubsub.MessageCount() != 1 {
		t.Fatalf("expected 1 server_down for a not-found server, got %d", pubsub.MessageCount())
	}
	if watcher.KnownCount() != 0 {
		t.Errorf("KnownCount() = %d, want 0", watcher.KnownCount())
	}
}

func TestRegistryWatcher_DetectsServerDown(t *testing.T) {
	reg := storage.NewMemoryServerRegistry()
	ctx := context.Background()
	_ = reg.Register(ctx, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	})

	pubsub := NewMemoryPubSub()
	spy := &spyLogger{}
	watcher := NewRegistryWatcher(reg, pubsub, spy)
	watcher.TrackServer("srv1", "map_01")

	if watcher.KnownCount() != 1 {
		t.Fatalf("KnownCount() = %d, want 1", watcher.KnownCount())
	}

	// Server is alive, check should not publish.
	watcher.checkServers(ctx)
	if pubsub.MessageCount() != 0 {
		t.Fatalf("expected no messages while server is alive, got %d", pubsub.MessageCount())
	}

	// Deregister the server (simulates heartbeat expiry).
	_ = reg.Deregister(ctx, "srv1")

	// Now the check should detect the server is gone.
	watcher.checkServers(ctx)
	if pubsub.MessageCount() != 1 {
		t.Fatalf("expected 1 message after server down, got %d", pubsub.MessageCount())
	}

	// Server should be untracked after detection.
	if watcher.KnownCount() != 0 {
		t.Errorf("KnownCount() = %d, want 0 after detection", watcher.KnownCount())
	}

	// Verify the event payload.
	pubsub.mu.Lock()
	msg := pubsub.messages[0]
	pubsub.mu.Unlock()

	var event ServerDownEvent
	if err := json.Unmarshal(msg, &event); err != nil {
		t.Fatalf("unmarshal error: %v", err)
	}
	if event.ServerID != "srv1" {
		t.Errorf("ServerID = %q, want %q", event.ServerID, "srv1")
	}
	if event.MapID != "map_01" {
		t.Errorf("MapID = %q, want %q", event.MapID, "map_01")
	}
}

func TestRegistryWatcher_DoesNotFireForAliveServers(t *testing.T) {
	reg := storage.NewMemoryServerRegistry()
	ctx := context.Background()
	_ = reg.Register(ctx, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	})

	pubsub := NewMemoryPubSub()
	spy := &spyLogger{}
	watcher := NewRegistryWatcher(reg, pubsub, spy)
	watcher.TrackServer("srv1", "map_01")

	// Multiple checks with server alive should produce no events.
	for i := 0; i < 5; i++ {
		watcher.checkServers(ctx)
	}
	if pubsub.MessageCount() != 0 {
		t.Errorf("expected no messages, got %d", pubsub.MessageCount())
	}
	if watcher.KnownCount() != 1 {
		t.Errorf("KnownCount() = %d, want 1", watcher.KnownCount())
	}
}

func TestRegistryWatcher_PollLoopStops(t *testing.T) {
	reg := storage.NewMemoryServerRegistry()
	pubsub := NewMemoryPubSub()
	spy := &spyLogger{}
	watcher := NewRegistryWatcher(reg, pubsub, spy)

	ctx, cancel := context.WithCancel(context.Background())
	watcher.Start(ctx)
	cancel()

	// Stop should return promptly.
	done := make(chan struct{})
	go func() {
		watcher.Stop()
		close(done)
	}()

	select {
	case <-done:
		// OK
	case <-time.After(2 * time.Second):
		t.Fatal("Stop() did not return within 2s")
	}
}

func TestSubscribeServerDown_ReceivesEvents(t *testing.T) {
	pubsub := NewMemoryPubSub()
	spy := &spyLogger{}

	var received atomic.Int32
	err := SubscribeServerDown(context.Background(), pubsub, func(e ServerDownEvent) {
		if e.ServerID == "srv1" && e.MapID == "map_01" {
			received.Add(1)
		}
	}, spy)
	if err != nil {
		t.Fatalf("SubscribeServerDown() error: %v", err)
	}

	// Publish an event.
	event := ServerDownEvent{ServerID: "srv1", MapID: "map_01"}
	payload, _ := json.Marshal(event)
	_ = pubsub.Publish(context.Background(), ServerDownChannel, payload)

	if received.Load() != 1 {
		t.Errorf("received %d events, want 1", received.Load())
	}
}

func TestRegistryWatcher_UntrackPreventsDetection(t *testing.T) {
	reg := storage.NewMemoryServerRegistry()
	ctx := context.Background()
	_ = reg.Register(ctx, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	})

	pubsub := NewMemoryPubSub()
	spy := &spyLogger{}
	watcher := NewRegistryWatcher(reg, pubsub, spy)
	watcher.TrackServer("srv1", "map_01")
	watcher.UntrackServer("srv1")

	// Deregister the server.
	_ = reg.Deregister(ctx, "srv1")

	// Check should not fire because server is untracked.
	watcher.checkServers(ctx)
	if pubsub.MessageCount() != 0 {
		t.Errorf("expected no messages for untracked server, got %d", pubsub.MessageCount())
	}
}
