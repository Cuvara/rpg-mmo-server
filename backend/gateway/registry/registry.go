package registry

import (
	"context"
	"fmt"
	"sort"

	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// logger is the minimal logging surface RegistryService needs. It matches
// *slog.Logger so the gateway can pass its own logger straight in.
type logger interface {
	Warn(msg string, args ...any)
}

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

	// log is nil unless WithLogger was passed; every use must be nil-checked.
	log logger
}

// Option customises a RegistryService at construction time.
type Option func(*RegistryService)

// WithMetrics attaches the Prometheus metric set so allocator requests are
// counted (gateway_allocations_total).
func WithMetrics(m *metrics.Metrics) Option {
	return func(s *RegistryService) { s.metrics = m }
}

// WithLogger attaches a logger so FindServer can warn when a map is served by
// more than one live game server (see the split-brain check in FindServer).
func WithLogger(l logger) Option {
	return func(s *RegistryService) { s.log = l }
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
// capacity (PlayerCount < Capacity). Ties break on ServerID so the choice is
// deterministic. When no server has room and an allocator is configured, it
// requests a new instance and registers it. Returns an error when nothing can
// serve the map.
//
// MVP invariant: a map is expected to be served by exactly ONE live game
// server. Nothing enforces that — neither registry implementation guards
// against a second server claiming the same map_id, and the allocator
// deliberately registers an extra instance for a full map. Two instances of one
// map are two disconnected copies of the world: players on different instances
// cannot see each other and there is no handoff between them. That is a
// deliberate MVP limitation (see backend/docs/ARCHITECTURE-DECISIONS.md, ADR-2),
// so the condition is surfaced loudly here rather than failing the request.
func (s *RegistryService) FindServer(ctx context.Context, mapID string) (storage.ServerInfo, error) {
	servers, err := s.reg.FindByMapID(ctx, mapID)
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("find servers: %w", err)
	}

	if len(servers) > 1 && s.log != nil {
		ids := make([]string, 0, len(servers))
		for _, srv := range servers {
			ids = append(ids, srv.ServerID)
		}
		sort.Strings(ids)
		s.log.Warn("map served by multiple game servers; the world is split and players on different instances cannot interact",
			"map_id", mapID, "server_count", len(servers), "server_ids", ids)
	}

	var (
		best  storage.ServerInfo
		found bool
	)
	for _, srv := range servers {
		if srv.PlayerCount >= srv.Capacity {
			continue
		}
		// Registry order is non-deterministic (Go map iteration for the memory
		// store, SMEMBERS order for Redis), so an explicit ServerID tiebreak is
		// what keeps two equally-loaded servers from splitting a party.
		switch {
		case !found,
			srv.PlayerCount < best.PlayerCount,
			srv.PlayerCount == best.PlayerCount && srv.ServerID < best.ServerID:
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
