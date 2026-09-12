package storage

import (
	"context"
	"errors"
	"time"
)

// ErrNotFound is returned when a requested key/record does not exist (or has
// expired). Callers can test for it with errors.Is.
var ErrNotFound = errors.New("not found")

// Compile-time checks that every implementation satisfies its interface.
var (
	_ PlayerStore    = (*MemoryPlayerStore)(nil)
	_ SessionStore   = (*MemorySessionStore)(nil)
	_ ServerRegistry = (*MemoryServerRegistry)(nil)
	_ EventStream    = (*MemoryEventStream)(nil)
)

// --- Data types ---

// PlayerState holds persistent player data.
type PlayerState struct {
	UserID string  `json:"user_id"`
	X      float32 `json:"x"`
	Y      float32 `json:"y"`
	HP     int     `json:"hp"`
	MaxHP  int     `json:"max_hp"`
	MapID  string  `json:"map_id"`
}

// ServerInfo describes a registered game server.
//
// Transport is the realtime transport the server listens with ("tcp" or
// "kcp"). Empty means "tcp" so entries written by older game servers stay
// valid; the gateway forwards this value to clients in EnterWorldResponse.
type ServerInfo struct {
	ServerID    string `json:"server_id"`
	MapID       string `json:"map_id"`
	Addr        string `json:"addr"`
	Transport   string `json:"transport,omitempty"`
	Capacity    int    `json:"capacity"`
	PlayerCount int    `json:"player_count"`
}

// Event is a cross-server event message.
type Event struct {
	Type    string `json:"type"`
	Payload []byte `json:"payload"`
}

// --- Interfaces ---

// PlayerStore persists player game state.
type PlayerStore interface {
	SavePlayer(ctx context.Context, state *PlayerState) error
	LoadPlayer(ctx context.Context, userID string) (*PlayerState, error)
	DeletePlayer(ctx context.Context, userID string) error
}

// SessionStore manages session data in a key-value store.
type SessionStore interface {
	Set(ctx context.Context, key string, value []byte, ttl time.Duration) error
	Get(ctx context.Context, key string) ([]byte, error)
	Delete(ctx context.Context, key string) error
	// Refresh extends the TTL of an existing key without rewriting its value.
	// Returns an error if the key is missing or already expired.
	Refresh(ctx context.Context, key string, ttl time.Duration) error
}

// ServerRegistry tracks active game server instances.
//
// Implementations backed by an expiring store (Redis) drop a server entry when
// it stops calling Heartbeat within constants.ServerHeartbeatTTL, so lookups
// only ever return live servers.
type ServerRegistry interface {
	Register(ctx context.Context, info ServerInfo) error
	Deregister(ctx context.Context, serverID string) error
	FindByMapID(ctx context.Context, mapID string) ([]ServerInfo, error)
	UpdatePlayerCount(ctx context.Context, serverID string, count int) error
	// Heartbeat marks the server as alive, resetting its liveness TTL.
	// Returns an error if the server is unknown (or already expired).
	Heartbeat(ctx context.Context, serverID string) error
	// GetServer returns a single server by ID.
	GetServer(ctx context.Context, serverID string) (ServerInfo, error)
}

// DungeonIndex records which game server instance belongs to which party.
//
// ADR-26 decision 2: a dungeon instance is keyed by the PARTY, not by the
// content id. The first member's EnterWorld allocates a pod and publishes it
// here; every later member reads the same entry and is handed the same address.
// Keying by content would give every party in the game one shared dungeon,
// which is the opposite of instancing.
//
// Two keys rather than one, because allocation and lookup answer different
// questions:
//
//   - ClaimAllocation elects ONE member to do the allocating. Without it, four
//     members entering together allocate four pods and three of them are
//     orphaned immediately -- nothing else would ever look them up, because the
//     lookup below is keyed on a party that now points at the fourth.
//   - Publish/Lookup carry the answer. Losers of the election poll Lookup
//     rather than allocating.
//
// Entries expire: a party that never finishes entering must not pin a stale
// server id forever, and a dungeon pod that dies takes its own
// servers:id: hash with it, so a lookup that survived would resolve to nothing.
type DungeonIndex interface {
	// ClaimAllocation attempts to become the allocator for partyID. It returns
	// true exactly once per party until the claim expires or is released.
	ClaimAllocation(ctx context.Context, partyID, holder string, ttl time.Duration) (bool, error)

	// Publish records the allocated instance for partyID.
	Publish(ctx context.Context, partyID, serverID string, ttl time.Duration) error

	// Lookup returns the instance recorded for partyID, or ErrNotFound.
	Lookup(ctx context.Context, partyID string) (string, error)

	// Release drops both the claim and the published entry. Called when the
	// allocation fails, so the next member to arrive retries rather than
	// polling for an entry that will never be published.
	Release(ctx context.Context, partyID string) error
}

// EventStream publishes and consumes cross-server events.
//
// Subscribe is non-blocking: it starts delivery in the background and returns.
// Durable implementations (Redis Streams) acknowledge a message only after the
// handler returns, giving at-least-once delivery.
type EventStream interface {
	Publish(ctx context.Context, stream string, event Event) error
	Subscribe(ctx context.Context, stream string, handler func(Event)) error
	Close() error
}
