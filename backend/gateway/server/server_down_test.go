package server

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// newServerDownFixture builds a gateway (no listener needed — OnEvent is called
// directly, as the relay would) over a memory registry pre-loaded with srv1.
func newServerDownFixture(t *testing.T) (*Gateway, *registry.RegistryService, storage.ServerInfo) {
	t.Helper()
	serverRegistry := storage.NewMemoryServerRegistry()
	reg := registry.NewRegistryService(serverRegistry)
	sessions := session.NewSessionManager(storage.NewMemorySessionStore())
	gw := New(sessions, reg, testSecret, logger.New("error"))

	info := storage.ServerInfo{
		ServerID: "srv1", MapID: "map_forest", Addr: "10.0.0.1:9000", Capacity: 100,
	}
	if err := reg.RegisterServer(context.Background(), info); err != nil {
		t.Fatalf("RegisterServer() error: %v", err)
	}
	return gw, reg, info
}

// A consumed server_down event must evict the named server so the assignment
// path stops returning it immediately, without waiting out the registry TTL —
// and a server that re-registers afterwards must be assignable again (#236).
func TestOnEvent_ServerDownEvictsServer(t *testing.T) {
	gw, reg, info := newServerDownFixture(t)
	ctx := context.Background()

	if got, err := reg.FindServer(ctx, "map_forest"); err != nil || got.ServerID != "srv1" {
		t.Fatalf("FindServer() before event = (%v, %v), want srv1", got.ServerID, err)
	}

	payload, _ := json.Marshal(registry.ServerDownEvent{ServerID: "srv1", MapID: "map_forest"})
	gw.OnEvent(storage.Event{Type: registry.ServerDownChannel, Payload: payload})

	if _, err := reg.FindServer(ctx, "map_forest"); !errors.Is(err, registry.ErrNoServerAvailable) {
		t.Errorf("FindServer() after server_down error = %v, want ErrNoServerAvailable", err)
	}

	// Duplicate event (several gateways consume the same stream): harmless.
	gw.OnEvent(storage.Event{Type: registry.ServerDownChannel, Payload: payload})

	// Re-registration heals: the server is assignable again.
	if err := reg.RegisterServer(ctx, info); err != nil {
		t.Fatalf("RegisterServer() after eviction error: %v", err)
	}
	if got, err := reg.FindServer(ctx, "map_forest"); err != nil || got.ServerID != "srv1" {
		t.Errorf("FindServer() after re-register = (%v, %v), want srv1", got.ServerID, err)
	}
}

// Events that are not server_down, or that are malformed, must not touch the
// registry (and must not panic the relay's consumer goroutine).
func TestOnEvent_NonServerDownLeavesRegistryAlone(t *testing.T) {
	gw, reg, _ := newServerDownFixture(t)
	ctx := context.Background()

	gw.OnEvent(storage.Event{Type: "boss_killed", Payload: []byte(`{"server_id":"srv1"}`)})
	gw.OnEvent(storage.Event{Type: registry.ServerDownChannel, Payload: []byte(`{not json`)})
	gw.OnEvent(storage.Event{Type: registry.ServerDownChannel, Payload: []byte(`{"map_id":"map_forest"}`)}) // empty server_id

	if got, err := reg.FindServer(ctx, "map_forest"); err != nil || got.ServerID != "srv1" {
		t.Errorf("FindServer() = (%v, %v), want srv1 still assignable", got.ServerID, err)
	}
	if gw.EventCount() != 3 {
		t.Errorf("EventCount() = %d, want 3", gw.EventCount())
	}
}
