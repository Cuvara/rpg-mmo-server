package registry

import (
	"context"
	"fmt"

	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// RegistryService wraps a storage.ServerRegistry for game server lookup.
//
// It only ever talks to the interface, never to a concrete store: the gateway
// works the same whether the registry lives in-process (MemoryServerRegistry)
// or in Redis (redisstore.ServerRegistry, shared by every gateway instance).
type RegistryService struct {
	reg       storage.ServerRegistry
	allocator Allocator

	// metrics is nil unless WithMetrics was passed; recording is nil-safe.
	metrics *metrics.Metrics
}

// Option customises a RegistryService at construction time.
type Option func(*RegistryService)

// WithMetrics attaches the Prometheus metric set so allocator requests are
// counted (gateway_allocations_total).
func WithMetrics(m *metrics.Metrics) Option {
	return func(s *RegistryService) { s.metrics = m }
}

// NewRegistryService creates a RegistryService backed by the given registry.
func NewRegistryService(reg storage.ServerRegistry, opts ...Option) *RegistryService {
	return newRegistryService(&RegistryService{reg: reg}, opts)
}

// NewRegistryServiceWithAllocator creates a RegistryService that asks the
// allocator (e.g. Agones) for a new instance when no live server has capacity.
func NewRegistryServiceWithAllocator(reg storage.ServerRegistry, alloc Allocator, opts ...Option) *RegistryService {
	return newRegistryService(&RegistryService{reg: reg, allocator: alloc}, opts)
}

func newRegistryService(s *RegistryService, opts []Option) *RegistryService {
	for _, opt := range opts {
		opt(s)
	}
	return s
}

// FindServer locates the least-loaded live server for mapID that still has
// capacity (PlayerCount < Capacity). Ties keep registry order. When no server
// has room and an allocator is configured, it requests a new instance and
// registers it. Returns an error when nothing can serve the map.
func (s *RegistryService) FindServer(ctx context.Context, mapID string) (storage.ServerInfo, error) {
	servers, err := s.reg.FindByMapID(ctx, mapID)
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("find servers: %w", err)
	}

	var (
		best  storage.ServerInfo
		found bool
	)
	for _, srv := range servers {
		if srv.PlayerCount >= srv.Capacity {
			continue
		}
		if !found || srv.PlayerCount < best.PlayerCount {
			best, found = srv, true
		}
	}
	if found {
		return best, nil
	}

	if s.allocator != nil {
		allocated, aerr := s.allocator.AllocateServer(ctx, mapID)
		if aerr != nil {
			s.metrics.AllocationResult(false)
			return storage.ServerInfo{}, fmt.Errorf("no available server for map %s: %w", mapID, aerr)
		}
		// The allocation itself succeeded; a failed registry write still leaves
		// a pod running, so it is counted as a failure of the whole request.
		if rerr := s.reg.Register(ctx, allocated); rerr != nil {
			s.metrics.AllocationResult(false)
			return storage.ServerInfo{}, fmt.Errorf("register allocated server %s: %w", allocated.ServerID, rerr)
		}
		s.metrics.AllocationResult(true)
		return allocated, nil
	}

	return storage.ServerInfo{}, fmt.Errorf("no available server for map %s", mapID)
}

// GetServer returns a single live server by ID.
func (s *RegistryService) GetServer(ctx context.Context, serverID string) (storage.ServerInfo, error) {
	info, err := s.reg.GetServer(ctx, serverID)
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("get server %s: %w", serverID, err)
	}
	return info, nil
}

// RegisterServer registers a game server in the registry.
func (s *RegistryService) RegisterServer(ctx context.Context, info storage.ServerInfo) error {
	return s.reg.Register(ctx, info)
}

// DeregisterServer removes a game server from the registry.
func (s *RegistryService) DeregisterServer(ctx context.Context, serverID string) error {
	return s.reg.Deregister(ctx, serverID)
}
