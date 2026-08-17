package registry

import (
	"context"
	"errors"
	"fmt"
	"sort"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

const (
	// retryMaxAttempts is the maximum number of retries for transient registry
	// errors (e.g. Redis connection blip). The initial attempt is not counted.
	retryMaxAttempts = 3

	// retryInitialDelay is the backoff seed: 1s -> 2s -> 4s.
	retryInitialDelay = 1 * time.Second

	// retryTotalTimeout caps the total time spent retrying so a slow Redis does
	// not hold a client connection open indefinitely.
	retryTotalTimeout = 10 * time.Second

	// DefaultAllocationWaitTimeout bounds how long FindServer waits for a
	// freshly allocated game server to publish its own registry entry.
	//
	// A newly allocated Agones pod has to pull/start the NativeAOT container,
	// bind its port, report Ready to the SDK sidecar, learn its own address and
	// self-register into the registry before a client can reach it. That cold
	// start is not measured yet, so the default is deliberately generous —
	// larger than registry.retryTotalTimeout (10s, a Redis blip) because a pod
	// start is a much heavier event — while staying below
	// constants.JoinTokenTTL (30s) so the wait can never be longer than the
	// life of the token minted after it.
	DefaultAllocationWaitTimeout = 20 * time.Second

	// DefaultAllocationPollInterval is how often that wait re-reads the
	// registry. 250ms costs at most 80 single-key reads over the full timeout
	// and adds at most 250ms of detection lag once the server appears.
	DefaultAllocationPollInterval = 250 * time.Millisecond
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

	// allocWaitTimeout / allocPollInterval bound the wait for a freshly
	// allocated server's own registry entry. Zero means the default.
	allocWaitTimeout  time.Duration
	allocPollInterval time.Duration
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

// WithAllocationWait sets how long FindServer waits for a freshly allocated
// game server to register itself, and how often it re-checks. Non-positive
// values keep the corresponding default.
func WithAllocationWait(timeout, interval time.Duration) Option {
	return func(s *RegistryService) {
		if timeout > 0 {
			s.allocWaitTimeout = timeout
		}
		if interval > 0 {
			s.allocPollInterval = interval
		}
	}
}

func newRegistryService(s *RegistryService, opts []Option) *RegistryService {
	s.allocWaitTimeout = DefaultAllocationWaitTimeout
	s.allocPollInterval = DefaultAllocationPollInterval
	for _, opt := range opts {
		opt(s)
	}
	return s
}

// ErrNoServerAvailable means no game server has spare capacity for the
// requested map (and allocation, if configured, did not produce one). It is a
// capacity condition, not a fault: the gateway maps it to a distinct
// client-facing message, so it must be matchable with errors.Is rather than by
// string comparison.
var ErrNoServerAvailable = errors.New("no available server for map")

// ErrServerStarting means a game server was allocated for the map but did not
// publish its own registry entry inside the allocation wait window, so there is
// no address a client could be sent to yet.
//
// It is a *retryable* condition and deliberately distinct from
// ErrNoServerAvailable: the map is not full or unserved, its server is booting.
// The gateway maps it to its own client-facing message so a client can tell
// "do not retry" from "retry shortly". Must be matchable with errors.Is.
var ErrServerStarting = errors.New("allocated server has not registered yet")

// isRetriable returns true for errors that are likely transient (Redis blip,
// network timeout) as opposed to logical conditions (no server for map, not
// found) that will not succeed on retry.
func isRetriable(err error) bool {
	if err == nil {
		return false
	}
	if errors.Is(err, ErrNoServerAvailable) || errors.Is(err, storage.ErrNotFound) {
		return false
	}
	return true
}

// findByMapIDWithRetry wraps FindByMapID with exponential backoff. Only
// transient errors are retried; capacity / not-found errors return immediately.
func (s *RegistryService) findByMapIDWithRetry(ctx context.Context, mapID string) ([]storage.ServerInfo, error) {
	ctx, cancel := context.WithTimeout(ctx, retryTotalTimeout)
	defer cancel()

	var lastErr error
	delay := retryInitialDelay

	for attempt := 0; attempt <= retryMaxAttempts; attempt++ {
		servers, err := s.reg.FindByMapID(ctx, mapID)
		if err == nil {
			return servers, nil
		}

		if !isRetriable(err) {
			return nil, err
		}
		lastErr = err

		if attempt == retryMaxAttempts {
			break
		}

		if s.log != nil {
			s.log.Warn("registry lookup failed, retrying",
				"map_id", mapID,
				"attempt", attempt+1,
				"delay", delay,
				"error", err)
		}

		select {
		case <-ctx.Done():
			return nil, fmt.Errorf("retry aborted: %w (last: %w)", ctx.Err(), lastErr)
		case <-time.After(delay):
		}
		delay *= 2
	}

	return nil, fmt.Errorf("all %d retries exhausted: %w", retryMaxAttempts, lastErr)
}

// getServerWithRetry wraps GetServer with exponential backoff for transient
// errors.
func (s *RegistryService) getServerWithRetry(ctx context.Context, serverID string) (storage.ServerInfo, error) {
	ctx, cancel := context.WithTimeout(ctx, retryTotalTimeout)
	defer cancel()

	var lastErr error
	delay := retryInitialDelay

	for attempt := 0; attempt <= retryMaxAttempts; attempt++ {
		info, err := s.reg.GetServer(ctx, serverID)
		if err == nil {
			return info, nil
		}

		if !isRetriable(err) {
			return storage.ServerInfo{}, err
		}
		lastErr = err

		if attempt == retryMaxAttempts {
			break
		}

		if s.log != nil {
			s.log.Warn("registry get server failed, retrying",
				"server_id", serverID,
				"attempt", attempt+1,
				"delay", delay,
				"error", err)
		}

		select {
		case <-ctx.Done():
			return storage.ServerInfo{}, fmt.Errorf("retry aborted: %w (last: %w)", ctx.Err(), lastErr)
		case <-time.After(delay):
		}
		delay *= 2
	}

	return storage.ServerInfo{}, fmt.Errorf("all %d retries exhausted: %w", retryMaxAttempts, lastErr)
}

// FindServer locates the least-loaded live server for mapID that still has
// capacity (PlayerCount < Capacity). Ties break on ServerID so the choice is
// deterministic.
//
// MVP invariant: a map is served by exactly ONE live game server (ADR-2). Two
// instances of one map are two disconnected copies of the world: players on
// different instances cannot see or interact with each other and there is no
// handoff between them. FindServer therefore never adds a second server to a
// map:
//
//   - Live servers exist and one has room -> that server is returned.
//   - Live servers exist and every one is FULL -> ErrNoServerAvailable, with no
//     allocation. Refusing a join is a loud, bounded failure; a silently split
//     world is not. Allocation exists to replace an *absent* server, never to
//     add capacity to a full one.
//   - No live server at all, and an allocator is configured -> a new instance is
//     requested.
//
// Nothing else enforces the invariant — neither registry implementation guards
// against a second server claiming the same map_id — so a map that still
// resolves to more than one server is logged loudly here as the detector for it
// being violated by some other means.
//
// Allocation does not return the allocation response directly. The gateway must
// not write the allocated server's registry entry on its behalf: that would put
// two writers on one datum (ADR-1) and an entry the gateway wrote has nothing
// re-arming its TTL. Instead FindServer waits, bounded by allocWaitTimeout, for
// the game server's *own* entry to appear and returns that — so the address and
// transport handed to a client are the ones the server self-reported and is
// actually listening on. If it never appears, ErrServerStarting is returned and
// no address is announced.
func (s *RegistryService) FindServer(ctx context.Context, mapID string) (storage.ServerInfo, error) {
	servers, err := s.findByMapIDWithRetry(ctx, mapID)
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

	// Servers exist for this map but all of them are full. Allocating here would
	// register a second live server for one map_id and split the world, so it is
	// a refusal, not a fallback (ADR-2).
	if len(servers) > 0 {
		return storage.ServerInfo{}, fmt.Errorf("%w %s: all %d servers full", ErrNoServerAvailable, mapID, len(servers))
	}

	if s.allocator != nil {
		allocated, aerr := s.allocator.AllocateServer(ctx, mapID)
		if aerr != nil {
			s.metrics.AllocationResult(false)
			return storage.ServerInfo{}, fmt.Errorf("%w %s: allocate: %w", ErrNoServerAvailable, mapID, aerr)
		}
		ready, werr := s.awaitRegistration(ctx, allocated.ServerID)
		if werr != nil {
			s.metrics.AllocationResult(false)
			return storage.ServerInfo{}, fmt.Errorf("allocated server for map %s: %w", mapID, werr)
		}
		s.metrics.AllocationResult(true)
		return ready, nil
	}

	return storage.ServerInfo{}, fmt.Errorf("%w %s", ErrNoServerAvailable, mapID)
}

// awaitRegistration polls the registry until serverID has published its own
// entry, or until allocWaitTimeout elapses.
//
// Every read error is treated as "not there yet" and simply retried: a missing
// key is the expected state for most of the wait, and a transient store error
// during a pod cold start is indistinguishable from it in any way that would
// change the outcome. The timeout, not the error class, is what ends the wait.
func (s *RegistryService) awaitRegistration(ctx context.Context, serverID string) (storage.ServerInfo, error) {
	timeout := s.allocWaitTimeout
	if timeout <= 0 {
		timeout = DefaultAllocationWaitTimeout
	}
	interval := s.allocPollInterval
	if interval <= 0 {
		interval = DefaultAllocationPollInterval
	}

	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	var lastErr error
	for {
		info, err := s.reg.GetServer(ctx, serverID)
		if err == nil {
			return info, nil
		}
		lastErr = err

		select {
		case <-ctx.Done():
			if s.log != nil {
				s.log.Warn("allocated game server never registered itself",
					"server_id", serverID, "waited", timeout, "last_error", lastErr)
			}
			return storage.ServerInfo{}, fmt.Errorf("%w: %s did not register within %s: %w",
				ErrServerStarting, serverID, timeout, lastErr)
		case <-ticker.C:
		}
	}
}

// GetServer returns a single live server by ID, retrying transient errors
// with exponential backoff.
func (s *RegistryService) GetServer(ctx context.Context, serverID string) (storage.ServerInfo, error) {
	info, err := s.getServerWithRetry(ctx, serverID)
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
