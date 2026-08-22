package registry

import (
	"context"
	"errors"
	"fmt"
	"sort"
	"sync"
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
	//
	// Lowered from 20s to 15s on 2026-08-17: 20s sat exactly on
	// server.MaxHandlerBlockingWait, the point at which the wait starves the
	// connection's own heartbeat (the read loop cannot record a MsgPong while a
	// handler blocks). Sitting on a ceiling leaves no room for scheduling and
	// poll-interval slop, and the failure it produces is the gateway dropping
	// the very client it is waiting for. cmd/gateway now refuses to start above
	// that ceiling.
	DefaultAllocationWaitTimeout = 15 * time.Second

	// DefaultAllocationPollInterval is how often that wait re-reads the
	// registry. 250ms costs at most 80 single-key reads over the full timeout
	// and adds at most 250ms of detection lag once the server appears.
	DefaultAllocationPollInterval = 250 * time.Millisecond

	// DefaultMapMismatchTTL is how long a proven "the configured fleet does not
	// serve this map" verdict is remembered, so a retrying client cannot burn a
	// pod per attempt (see rememberMismatch).
	//
	// 60s is long enough that a client retry loop (seconds) is fully absorbed,
	// and short enough that fixing the fleet's GAMESERVER_MAP_ID and rolling it
	// out takes effect without restarting the gateway.
	DefaultMapMismatchTTL = 60 * time.Second
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

	// allocMu guards allocInFlight only. It is never taken on the
	// existing-server path.
	allocMu       sync.Mutex
	allocInFlight map[string]*allocationCall

	// mismatchTTL is how long a proven fleet/map mismatch is remembered. Zero
	// means the default; a negative value disables the memory entirely.
	mismatchTTL time.Duration

	// mismatchMu guards mismatchUntil. Like allocMu it is never taken on the
	// existing-server path: the memory is only consulted immediately before an
	// allocation, which by definition means the registry had nothing.
	mismatchMu    sync.Mutex
	mismatchUntil map[string]time.Time
}

// allocationCall is one in-flight allocate-and-wait for a single map_id. Every
// caller that joins it blocks on done and then reads info/err, which are
// written exactly once by the leader before done is closed.
type allocationCall struct {
	done chan struct{}
	info storage.ServerInfo
	err  error
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

// WithMapMismatchTTL sets how long a proven "the configured fleet does not
// serve this map" verdict is remembered before another allocation is attempted
// for that map. Zero keeps DefaultMapMismatchTTL; a negative value disables the
// memory, which restores the unbounded-allocation behaviour and is only useful
// in tests.
func WithMapMismatchTTL(ttl time.Duration) Option {
	return func(s *RegistryService) { s.mismatchTTL = ttl }
}

func newRegistryService(s *RegistryService, opts []Option) *RegistryService {
	s.allocWaitTimeout = DefaultAllocationWaitTimeout
	s.allocPollInterval = DefaultAllocationPollInterval
	s.mismatchTTL = DefaultMapMismatchTTL
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

// ErrFleetMapMismatch means a game server exists and is healthy, but it serves a
// DIFFERENT map than the one that was asked for. It is a configuration fault,
// not a capacity one, which is why it is deliberately NOT a flavour of
// ErrNoServerAvailable: "we are out of capacity for map X" and "the fleet you
// configured hosts map Y, not X" call for opposite operator responses (grow the
// fleet vs. fix GAMESERVER_MAP_ID / point the map at a fleet that serves it).
//
// It is raised in two places, both of which used to pass silently:
//
//   - after allocation, when the pod that self-registered published a MapID that
//     is not the requested one. The gateway allocates by *fleet*, not by map, and
//     the pod's map comes from the fleet spec's own GAMESERVER_MAP_ID — so a
//     single-map fleet answers an allocation for any map at all, and
//     awaitRegistration (which polls by ServerID) finds that pod's pre-existing
//     entry instantly. Before this check the client was handed a server for the
//     wrong world and every layer reported success.
//   - on the registry path, when FindByMapID returns an entry whose MapID is not
//     the key it was asked for, which means the store is lying.
//
// Must be matchable with errors.Is; the gateway maps it to a distinct,
// non-retryable client message.
var ErrFleetMapMismatch = errors.New("configured fleet does not serve the requested map")

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

// FindServer locates a live server for mapID that still has capacity
// (PlayerCount < Capacity).
//
// With exactly one live server for the map — the only state ADR-2 allows — the
// rule is least-loaded with a ServerID tie-break, which degenerates to "that
// server, if it has room". With more than one, selection switches to the lowest
// ServerID and ignores PlayerCount entirely; see the selection block below for
// why (#203).
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
//
// Allocation is single-flight per map_id (see allocateOnce): concurrent callers
// for one unserved map produce one allocation, not one each.
//
// Whatever path produced the entry, the entry must serve the map that was asked
// for. Allocation targets a *fleet*, not a map, so nothing upstream guarantees
// that; ErrFleetMapMismatch is the refusal, and rememberMismatch stops a
// retrying client from burning one GameServer per attempt on a map the fleet
// cannot serve.
func (s *RegistryService) FindServer(ctx context.Context, mapID string) (storage.ServerInfo, error) {
	servers, err := s.findByMapIDWithRetry(ctx, mapID)
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("find servers: %w", err)
	}

	// Defence in depth on the registry path. FindByMapID is keyed by map_id, so
	// every entry it returns should already serve mapID; an entry that does not
	// means the store is lying (a key written under the wrong map, a stale index,
	// a buggy implementation). That is exactly the failure the allocation path
	// used to ship to clients silently, so it is refused here too rather than
	// filtered away quietly: dropping it would degrade into "no server for this
	// map" and, with an allocator configured, into an allocation the map does not
	// need. Announcing it is worse still — the client would join the wrong world.
	if wrong := wrongMapServers(servers, mapID); len(wrong) > 0 {
		if s.log != nil {
			s.log.Warn("registry returned servers for a different map than the one queried; the store index is inconsistent",
				"map_id", mapID, "server_ids", wrong)
		}
		return storage.ServerInfo{}, fmt.Errorf("%w: registry index for map %s returned servers %v serving another map",
			ErrFleetMapMismatch, mapID, wrong)
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

	// A map served by more than one live server is a violated invariant, not a
	// bigger map (ADR-2), and how the gateway selects inside that state decides
	// whether the state heals or entrenches.
	//
	// Least-loaded is the correct rule for capacity and the wrong rule for a
	// fault: it steers every new joiner into whichever half is emptier, which is
	// load-balancing across a split world. The two halves then converge on equal
	// population, both stay occupied, and neither ever drains — the accidental
	// split becomes a permanent one, with players who cannot see each other
	// standing in the same place (#203).
	//
	// So when more than one server serves the map, PlayerCount is not a
	// criterion at all: pick the lowest ServerID among those with spare
	// capacity. Every caller — every gateway instance, every retry — then lands
	// on the same half, so the other half drains as its players leave and the
	// split converges out instead of widening. It is deliberately the rule that
	// treats the split as a fault to escape rather than an arrangement to
	// exploit. The multi-server case is still logged loudly above; this only
	// stops the gateway from making it worse while an operator reacts.
	multiServer := len(servers) > 1

	var (
		best  storage.ServerInfo
		found bool
	)
	for _, srv := range servers {
		if srv.PlayerCount >= srv.Capacity {
			continue
		}
		switch {
		case !found:
			best, found = srv, true
		case multiServer:
			// Split world: deterministic, load-blind.
			if srv.ServerID < best.ServerID {
				best = srv
			}
		case srv.PlayerCount < best.PlayerCount,
			srv.PlayerCount == best.PlayerCount && srv.ServerID < best.ServerID:
			best = srv
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
		// A map already proven unservable by the configured fleet is refused
		// before the allocation API is called at all — see rememberMismatch.
		if until, ok := s.mismatchRemembered(mapID); ok {
			return storage.ServerInfo{}, fmt.Errorf(
				"%w: map %s was allocated a server for another map; not allocating again for %s",
				ErrFleetMapMismatch, mapID, time.Until(until).Round(time.Second))
		}
		return s.allocateOnce(ctx, mapID)
	}

	return storage.ServerInfo{}, fmt.Errorf("%w %s", ErrNoServerAvailable, mapID)
}

// wrongMapServers returns the ids of entries that do not serve mapID.
func wrongMapServers(servers []storage.ServerInfo, mapID string) []string {
	var wrong []string
	for _, srv := range servers {
		if srv.MapID != mapID {
			wrong = append(wrong, srv.ServerID)
		}
	}
	sort.Strings(wrong)
	return wrong
}

// effectiveMismatchTTL resolves the configured value: zero means the default,
// negative means the memory is disabled.
func (s *RegistryService) effectiveMismatchTTL() time.Duration {
	if s.mismatchTTL == 0 {
		return DefaultMapMismatchTTL
	}
	return s.mismatchTTL
}

// mismatchRemembered reports whether mapID is inside a live "the fleet does not
// serve this map" window, and when that window ends.
func (s *RegistryService) mismatchRemembered(mapID string) (time.Time, bool) {
	if s.mismatchTTL < 0 {
		return time.Time{}, false
	}
	s.mismatchMu.Lock()
	defer s.mismatchMu.Unlock()
	until, ok := s.mismatchUntil[mapID]
	if !ok {
		return time.Time{}, false
	}
	if !time.Now().Before(until) {
		delete(s.mismatchUntil, mapID)
		return time.Time{}, false
	}
	return until, true
}

// rememberMismatch records that allocating for mapID produced a server for a
// different map, so the next request refuses before calling the allocation API.
//
// This is the one failure that IS cached, and the exception is deliberate.
// allocateOnce never caches a failure because the failures it sees are
// transient — a Redis blip, a momentarily exhausted fleet, a pod that lost a
// boot race — and caching one would poison a map that is about to work by
// itself. A fleet/map mismatch is not that: the pod's map comes from the fleet
// spec's GAMESERVER_MAP_ID, which is fixed for the life of those pods, so no
// number of retries can turn the answer into "yes". Meanwhile every retry costs
// a GameServer permanently: Agones has no un-allocate and this gateway has no
// Deallocate, so an Allocated pod never returns to the pool. Caching a
// self-correcting failure is a bug; caching a fact of the deployment, with a TTL
// short enough that a corrected deployment is picked up without a restart, is
// what bounds the leak to one pod per map per TTL.
func (s *RegistryService) rememberMismatch(mapID string) {
	ttl := s.effectiveMismatchTTL()
	if ttl < 0 {
		return
	}
	s.mismatchMu.Lock()
	defer s.mismatchMu.Unlock()
	if s.mismatchUntil == nil {
		s.mismatchUntil = make(map[string]time.Time)
	}
	s.mismatchUntil[mapID] = time.Now().Add(ttl)
}

// allocateOnce runs allocateAndWait for mapID under single-flight: the first
// caller for a map does the work, every caller that arrives while it is running
// waits for it and shares its outcome.
//
// Without this, the retry we ask clients to perform is an amplifier. A client
// told "server is starting, retry shortly" retries, the retry finds the map
// still unserved and allocates ANOTHER pod, and so on — while ADR-2 guarantees
// only one of those pods can ever win the map_id registration and nothing
// deallocates the losers (Agones will not reclaim an Allocated GameServer). On a
// replicas:1 fleet that exhausts the fleet from a single player reconnecting.
//
// The result is deliberately NOT cached: the entry is removed as soon as the
// leader finishes, whether it succeeded or failed, so one transient allocation
// failure cannot poison a map until restart. A success needs no cache either —
// by then the server is in the registry, and the next FindServer resolves it
// before ever reaching this function.
//
// The one exception is a proven fleet/map mismatch, which is remembered outside
// this map for DefaultMapMismatchTTL (see rememberMismatch): it is a fact of the
// deployment rather than a transient fault, and each retry of it costs a
// GameServer that is never reclaimed.
func (s *RegistryService) allocateOnce(ctx context.Context, mapID string) (storage.ServerInfo, error) {
	s.allocMu.Lock()
	if call, ok := s.allocInFlight[mapID]; ok {
		s.allocMu.Unlock()
		if s.log != nil {
			s.log.Warn("joining an allocation already in flight for this map",
				"map_id", mapID)
		}
		select {
		case <-call.done:
			return call.info, call.err
		case <-ctx.Done():
			// This caller gave up (client disconnected); the leader carries on
			// for everyone else.
			return storage.ServerInfo{}, fmt.Errorf("await allocation for map %s: %w", mapID, ctx.Err())
		}
	}
	if s.allocInFlight == nil {
		s.allocInFlight = make(map[string]*allocationCall)
	}
	call := &allocationCall{done: make(chan struct{})}
	s.allocInFlight[mapID] = call
	s.allocMu.Unlock()

	// The leader's work is detached from its own caller's cancellation: the
	// followers' outcome must not depend on which client happened to arrive
	// first, and a leader that disconnects mid-allocation would otherwise abort
	// an allocation the followers are still waiting on. The wait stays bounded
	// by allocWaitTimeout, and the allocator call by its own timeout.
	call.info, call.err = s.allocateAndWait(context.WithoutCancel(ctx), mapID)

	s.allocMu.Lock()
	delete(s.allocInFlight, mapID)
	s.allocMu.Unlock()
	close(call.done)

	return call.info, call.err
}

// allocateAndWait requests one new instance for mapID and blocks until that
// instance has registered itself, or the wait window expires.
func (s *RegistryService) allocateAndWait(ctx context.Context, mapID string) (storage.ServerInfo, error) {
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
	// awaitRegistration polls by ServerID, not by map, so what it returns is
	// "the entry this pod published", not "an entry for this map". Those differ
	// whenever the fleet serves a map other than the requested one: the pod
	// self-registered under its own GAMESERVER_MAP_ID at boot, the poll finds
	// that entry on the first read, and without this check the address of a
	// server for a completely different world is handed to the client with a
	// valid join token and no error anywhere.
	if ready.MapID != mapID {
		s.metrics.AllocationResult(false)
		s.rememberMismatch(mapID)
		if s.log != nil {
			s.log.Warn("allocated game server serves a different map than the one requested; refusing the assignment and suppressing further allocations for this map",
				"requested_map_id", mapID, "server_map_id", ready.MapID,
				"server_id", ready.ServerID, "suppress_for", s.effectiveMismatchTTL())
		}
		return storage.ServerInfo{}, fmt.Errorf(
			"%w: allocated %s for map %s but it serves map %q (check the fleet's GAMESERVER_MAP_ID)",
			ErrFleetMapMismatch, ready.ServerID, mapID, ready.MapID)
	}
	s.metrics.AllocationResult(true)
	return ready, nil
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
