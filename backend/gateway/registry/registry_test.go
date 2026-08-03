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
