package registry

import (
	"context"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

func TestRegistryService_RegisterAndFind(t *testing.T) {
	reg := NewRegistryService(storage.NewMemoryServerRegistry())
	ctx := context.Background()

	info := storage.ServerInfo{
		ServerID:    "srv1",
		MapID:       "map_forest",
		Addr:        "10.0.0.1:9000",
		Capacity:    100,
		PlayerCount: 50,
	}
	if err := reg.RegisterServer(ctx, info); err != nil {
		t.Fatalf("RegisterServer() error: %v", err)
	}

	found, err := reg.FindServer(ctx, "map_forest")
	if err != nil {
		t.Fatalf("FindServer() error: %v", err)
	}
	if found.ServerID != "srv1" {
		t.Errorf("ServerID = %q, want %q", found.ServerID, "srv1")
	}
	if found.Addr != "10.0.0.1:9000" {
		t.Errorf("Addr = %q, want %q", found.Addr, "10.0.0.1:9000")
	}
}

// A map served by several equally-loaded servers must always resolve to the same
// one. Registry order is non-deterministic (Go map iteration / Redis SMEMBERS), so
// without an explicit tiebreak two players entering the same map could be sent to
// different instances — which, with no cross-instance handoff, silently splits a
// party across two copies of the world. See ARCHITECTURE-DECISIONS.md ADR-2.
func TestRegistryService_FindServer_DeterministicTieBreak(t *testing.T) {
	ctx := context.Background()

	for attempt := 0; attempt < 50; attempt++ {
		reg := NewRegistryService(storage.NewMemoryServerRegistry())
		for _, id := range []string{"srv-c", "srv-a", "srv-b"} {
			if err := reg.RegisterServer(ctx, storage.ServerInfo{
				ServerID:    id,
				MapID:       "map_forest",
				Addr:        id + ":9000",
				Capacity:    100,
				PlayerCount: 10, // identical load: the tiebreak decides
			}); err != nil {
				t.Fatalf("RegisterServer(%s) error: %v", id, err)
			}
		}

		found, err := reg.FindServer(ctx, "map_forest")
		if err != nil {
			t.Fatalf("FindServer() error: %v", err)
		}
		if found.ServerID != "srv-a" {
			t.Fatalf("attempt %d: ServerID = %q, want %q (lowest id wins a tie)",
				attempt, found.ServerID, "srv-a")
		}
	}
}

// Load still beats the id tiebreak.
func TestRegistryService_FindServer_PrefersLeastLoaded(t *testing.T) {
	reg := NewRegistryService(storage.NewMemoryServerRegistry())
	ctx := context.Background()

	servers := []storage.ServerInfo{
		{ServerID: "srv-a", MapID: "map_forest", Addr: "a:9000", Capacity: 100, PlayerCount: 90},
		{ServerID: "srv-z", MapID: "map_forest", Addr: "z:9000", Capacity: 100, PlayerCount: 5},
	}
	for _, s := range servers {
		if err := reg.RegisterServer(ctx, s); err != nil {
			t.Fatalf("RegisterServer(%s) error: %v", s.ServerID, err)
		}
	}

	found, err := reg.FindServer(ctx, "map_forest")
	if err != nil {
		t.Fatalf("FindServer() error: %v", err)
	}
	if found.ServerID != "srv-z" {
		t.Errorf("ServerID = %q, want %q (least loaded wins over id order)", found.ServerID, "srv-z")
	}
}

func TestRegistryService_FindNoCapacity(t *testing.T) {
	reg := NewRegistryService(storage.NewMemoryServerRegistry())
	ctx := context.Background()

	info := storage.ServerInfo{
		ServerID:    "srv1",
		MapID:       "map_cave",
		Addr:        "10.0.0.1:9000",
		Capacity:    100,
		PlayerCount: 100, // full
	}
	if err := reg.RegisterServer(ctx, info); err != nil {
		t.Fatalf("RegisterServer() error: %v", err)
	}

	_, err := reg.FindServer(ctx, "map_cave")
	if err == nil {
		t.Error("FindServer() should fail when all servers are full")
	}
}

func TestRegistryService_FindNoServers(t *testing.T) {
	reg := NewRegistryService(storage.NewMemoryServerRegistry())
	ctx := context.Background()

	_, err := reg.FindServer(ctx, "map_nonexistent")
	if err == nil {
		t.Error("FindServer() should fail when no servers exist for map")
	}
}

func TestRegistryService_Deregister(t *testing.T) {
	reg := NewRegistryService(storage.NewMemoryServerRegistry())
	ctx := context.Background()

	info := storage.ServerInfo{
		ServerID:    "srv1",
		MapID:       "map_desert",
		Addr:        "10.0.0.1:9000",
		Capacity:    100,
		PlayerCount: 10,
	}
	if err := reg.RegisterServer(ctx, info); err != nil {
		t.Fatalf("RegisterServer() error: %v", err)
	}

	if err := reg.DeregisterServer(ctx, "srv1"); err != nil {
		t.Fatalf("DeregisterServer() error: %v", err)
	}

	_, err := reg.FindServer(ctx, "map_desert")
	if err == nil {
		t.Error("FindServer() should fail after deregister")
	}
}
