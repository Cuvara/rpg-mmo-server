package storage

import (
	"context"
	"fmt"
	"sync"
	"time"
)

// --- MemoryPlayerStore ---

// MemoryPlayerStore is an in-memory PlayerStore for testing.
type MemoryPlayerStore struct {
	mu      sync.RWMutex
	players map[string]*PlayerState
}

func NewMemoryPlayerStore() *MemoryPlayerStore {
	return &MemoryPlayerStore{players: make(map[string]*PlayerState)}
}

func (s *MemoryPlayerStore) SavePlayer(_ context.Context, state *PlayerState) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	cp := *state
	s.players[state.UserID] = &cp
	return nil
}

func (s *MemoryPlayerStore) LoadPlayer(_ context.Context, userID string) (*PlayerState, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	p, ok := s.players[userID]
	if !ok {
		return nil, fmt.Errorf("player %s: %w", userID, ErrNotFound)
	}
	cp := *p
	return &cp, nil
}

func (s *MemoryPlayerStore) DeletePlayer(_ context.Context, userID string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.players, userID)
	return nil
}

// --- MemorySessionStore ---

type sessionEntry struct {
	value     []byte
	expiresAt time.Time
}

// MemorySessionStore is an in-memory SessionStore for testing.
type MemorySessionStore struct {
	mu       sync.RWMutex
	sessions map[string]sessionEntry
}

func NewMemorySessionStore() *MemorySessionStore {
	return &MemorySessionStore{sessions: make(map[string]sessionEntry)}
}

func (s *MemorySessionStore) Set(_ context.Context, key string, value []byte, ttl time.Duration) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.sessions[key] = sessionEntry{
		value:     append([]byte{}, value...),
		expiresAt: time.Now().Add(ttl),
	}
	return nil
}

func (s *MemorySessionStore) Get(_ context.Context, key string) ([]byte, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	entry, ok := s.sessions[key]
	if !ok {
		return nil, fmt.Errorf("session %s: %w", key, ErrNotFound)
	}
	if time.Now().After(entry.expiresAt) {
		return nil, fmt.Errorf("session %s expired", key)
	}
	return append([]byte{}, entry.value...), nil
}

func (s *MemorySessionStore) Delete(_ context.Context, key string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.sessions, key)
	return nil
}

// Refresh extends the TTL of an existing session entry.
func (s *MemorySessionStore) Refresh(_ context.Context, key string, ttl time.Duration) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	entry, ok := s.sessions[key]
	if !ok {
		return fmt.Errorf("session %s: %w", key, ErrNotFound)
	}
	if time.Now().After(entry.expiresAt) {
		delete(s.sessions, key)
		return fmt.Errorf("session %s expired", key)
	}
	entry.expiresAt = time.Now().Add(ttl)
	s.sessions[key] = entry
	return nil
}

// --- MemoryServerRegistry ---

// MemoryServerRegistry is an in-memory ServerRegistry for testing.
//
// Heartbeat expiry is opt-in: the zero TTL (from NewMemoryServerRegistry) keeps
// entries forever, which is what in-process tests and single-node dev runs want.
// Use NewMemoryServerRegistryWithTTL to mirror the Redis liveness behaviour.
type MemoryServerRegistry struct {
	mu      sync.RWMutex
	ttl     time.Duration
	servers map[string]*registryEntry
}

type registryEntry struct {
	info     ServerInfo
	lastSeen time.Time
}

func NewMemoryServerRegistry() *MemoryServerRegistry {
	return &MemoryServerRegistry{servers: make(map[string]*registryEntry)}
}

// NewMemoryServerRegistryWithTTL creates a registry that drops servers which
// have not called Heartbeat within ttl. A ttl <= 0 disables expiry.
func NewMemoryServerRegistryWithTTL(ttl time.Duration) *MemoryServerRegistry {
	return &MemoryServerRegistry{ttl: ttl, servers: make(map[string]*registryEntry)}
}

// alive reports whether the entry is still within its heartbeat window.
// Caller must hold at least a read lock.
func (r *MemoryServerRegistry) alive(e *registryEntry) bool {
	if r.ttl <= 0 {
		return true
	}
	return time.Since(e.lastSeen) < r.ttl
}

func (r *MemoryServerRegistry) Register(_ context.Context, info ServerInfo) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.servers[info.ServerID] = &registryEntry{info: info, lastSeen: time.Now()}
	return nil
}

// Heartbeat resets the liveness window for the server.
func (r *MemoryServerRegistry) Heartbeat(_ context.Context, serverID string) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	e, ok := r.servers[serverID]
	if !ok || !r.alive(e) {
		return fmt.Errorf("server %s: %w", serverID, ErrNotFound)
	}
	e.lastSeen = time.Now()
	return nil
}

// GetServer returns a single live server by ID.
func (r *MemoryServerRegistry) GetServer(_ context.Context, serverID string) (ServerInfo, error) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	e, ok := r.servers[serverID]
	if !ok || !r.alive(e) {
		return ServerInfo{}, fmt.Errorf("server %s: %w", serverID, ErrNotFound)
	}
	return e.info, nil
}

func (r *MemoryServerRegistry) Deregister(_ context.Context, serverID string) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.servers, serverID)
	return nil
}

func (r *MemoryServerRegistry) FindByMapID(_ context.Context, mapID string) ([]ServerInfo, error) {
	r.mu.RLock()
	defer r.mu.RUnlock()
	var result []ServerInfo
	for _, e := range r.servers {
		if e.info.MapID == mapID && r.alive(e) {
			result = append(result, e.info)
		}
	}
	return result, nil
}

func (r *MemoryServerRegistry) UpdatePlayerCount(_ context.Context, serverID string, count int) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	e, ok := r.servers[serverID]
	if !ok || !r.alive(e) {
		return fmt.Errorf("server %s: %w", serverID, ErrNotFound)
	}
	e.info.PlayerCount = count
	return nil
}

// --- MemoryEventStream ---

// MemoryEventStream is an in-memory EventStream for testing.
type MemoryEventStream struct {
	mu          sync.RWMutex
	subscribers map[string][]func(Event)
	closed      bool
}

func NewMemoryEventStream() *MemoryEventStream {
	return &MemoryEventStream{subscribers: make(map[string][]func(Event))}
}

func (s *MemoryEventStream) Publish(_ context.Context, stream string, event Event) error {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if s.closed {
		return fmt.Errorf("event stream closed")
	}
	for _, handler := range s.subscribers[stream] {
		go handler(event)
	}
	return nil
}

func (s *MemoryEventStream) Subscribe(_ context.Context, stream string, handler func(Event)) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed {
		return fmt.Errorf("event stream closed")
	}
	s.subscribers[stream] = append(s.subscribers[stream], handler)
	return nil
}

func (s *MemoryEventStream) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.closed = true
	return nil
}
