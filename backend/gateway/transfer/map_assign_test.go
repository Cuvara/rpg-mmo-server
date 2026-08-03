package transfer

import (
	"context"
	"testing"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

func TestAssignMap_Success(t *testing.T) {
	memReg := storage.NewMemoryServerRegistry()
	reg := registry.NewRegistryService(memReg)
	ctx := context.Background()
	secret := "test-secret"

	// Register a server with capacity.
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

	result, err := AssignMap(ctx, "user1", "map_forest", reg, secret)
	if err != nil {
		t.Fatalf("AssignMap() error: %v", err)
	}
	if result.ServerAddr != "10.0.0.1:9000" {
		t.Errorf("ServerAddr = %q, want %q", result.ServerAddr, "10.0.0.1:9000")
	}
	if result.JoinToken == "" {
		t.Error("JoinToken should not be empty")
	}

	// Validate the join token round-trips correctly.
	userID, serverID, err := ValidateJoinToken(result.JoinToken, secret)
	if err != nil {
		t.Fatalf("ValidateJoinToken() error: %v", err)
	}
	if userID != "user1" {
		t.Errorf("userID = %q, want %q", userID, "user1")
	}
	if serverID != "srv1" {
		t.Errorf("serverID = %q, want %q", serverID, "srv1")
	}
}

func TestAssignMap_NoServer(t *testing.T) {
	memReg := storage.NewMemoryServerRegistry()
	reg := registry.NewRegistryService(memReg)
	ctx := context.Background()

	_, err := AssignMap(ctx, "user1", "map_nonexistent", reg, "secret")
	if err == nil {
		t.Error("AssignMap() should fail when no server available")
	}
}
