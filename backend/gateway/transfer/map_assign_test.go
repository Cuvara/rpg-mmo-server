package transfer

import (
	"context"
	"errors"
	"testing"
	"time"

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

// selfRegisteringAllocator mimics an Agones allocation followed (or not) by the
// game server's own registration. The address it self-reports differs from the
// allocation response so the test can tell which one the token was minted from.
type selfRegisteringAllocator struct {
	info     storage.ServerInfo
	store    storage.ServerRegistry
	after    time.Duration
	selfAddr string
	selfTran string
	// selfMap, when set, is the map the pod registers itself under — its fleet's
	// GAMESERVER_MAP_ID, which need not be the map that was requested.
	selfMap string
}

func (a *selfRegisteringAllocator) AllocateServer(_ context.Context, mapID string) (storage.ServerInfo, error) {
	info := a.info
	info.MapID = mapID
	if a.store != nil {
		self := info
		self.Addr, self.Transport = a.selfAddr, a.selfTran
		if a.selfMap != "" {
			self.MapID = a.selfMap
		}
		time.AfterFunc(a.after, func() { _ = a.store.Register(context.Background(), self) })
	}
	return info, nil
}

// The join token is single-use, pinned to one server id and short-lived, so it
// must be minted from the entry the allocated server published about itself —
// not from the allocation response, whose address is only a guess.
func TestAssignMap_AllocatedServerMintsFromItsOwnEntry(t *testing.T) {
	ctx := context.Background()
	store := storage.NewMemoryServerRegistry()
	alloc := &selfRegisteringAllocator{
		info:     storage.ServerInfo{ServerID: "gs-new", Addr: "10.0.0.9:9000", Capacity: 50},
		store:    store,
		after:    10 * time.Millisecond,
		selfAddr: "10.0.0.9:7257",
		selfTran: "kcp",
	}
	reg := registry.NewRegistryServiceWithAllocator(store, alloc,
		registry.WithAllocationWait(2*time.Second, 5*time.Millisecond))

	result, err := AssignMap(ctx, "user1", "map_new", reg, "test-secret")
	if err != nil {
		t.Fatalf("AssignMap() error: %v", err)
	}
	if result.ServerAddr != "10.0.0.9:7257" {
		t.Errorf("ServerAddr = %q, want the self-registered %q", result.ServerAddr, "10.0.0.9:7257")
	}
	if result.Transport != "kcp" {
		t.Errorf("Transport = %q, want the self-registered %q", result.Transport, "kcp")
	}
	if _, sid, verr := ValidateJoinToken(result.JoinToken, "test-secret"); verr != nil || sid != "gs-new" {
		t.Errorf("join token sid = %q err = %v, want gs-new", sid, verr)
	}
}

// A pod that never registers must yield no token at all, and a retryable error
// distinct from the capacity condition.
func TestAssignMap_AllocatedServerNeverRegisters(t *testing.T) {
	ctx := context.Background()
	store := storage.NewMemoryServerRegistry()
	alloc := &selfRegisteringAllocator{info: storage.ServerInfo{ServerID: "gs-ghost", Addr: "10.0.0.9:9000", Capacity: 50}}
	reg := registry.NewRegistryServiceWithAllocator(store, alloc,
		registry.WithAllocationWait(50*time.Millisecond, 5*time.Millisecond))

	result, err := AssignMap(ctx, "user1", "map_ghost", reg, "test-secret")
	if !errors.Is(err, registry.ErrServerStarting) {
		t.Fatalf("AssignMap() error = %v, want registry.ErrServerStarting", err)
	}
	if errors.Is(err, registry.ErrNoServerAvailable) {
		t.Error("a booting server must not be reported as ErrNoServerAvailable")
	}
	if result.JoinToken != "" {
		t.Error("no join token may be minted for a server that never showed up")
	}
}

// A pod allocated for map X that turns out to serve map Y must yield no token
// and no address: joining it would drop the player into a different world with
// every layer reporting success.
func TestAssignMap_AllocatedServerServesAnotherMap(t *testing.T) {
	ctx := context.Background()
	store := storage.NewMemoryServerRegistry()
	alloc := &selfRegisteringAllocator{
		info:     storage.ServerInfo{ServerID: "map-servers-dotnet-dev-q7bdn-hctpd", Addr: "127.0.0.1:7002", Capacity: 100},
		store:    store,
		after:    5 * time.Millisecond,
		selfAddr: "127.0.0.1:7002",
		selfMap:  "map_01", // the fleet's GAMESERVER_MAP_ID
	}
	reg := registry.NewRegistryServiceWithAllocator(store, alloc,
		registry.WithAllocationWait(2*time.Second, 5*time.Millisecond))

	result, err := AssignMap(ctx, "user1", "map_77", reg, "test-secret")
	if !errors.Is(err, registry.ErrFleetMapMismatch) {
		t.Fatalf("AssignMap() error = %v, want registry.ErrFleetMapMismatch", err)
	}
	if result.JoinToken != "" {
		t.Error("no join token may be minted for a server that serves another map")
	}
	if result.ServerAddr != "" {
		t.Errorf("ServerAddr = %q, want empty: the wrong-map address must never reach a client", result.ServerAddr)
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
