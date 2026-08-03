package storage

import (
	"context"
	"time"
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
type ServerInfo struct {
	ServerID    string `json:"server_id"`
	MapID       string `json:"map_id"`
	Addr        string `json:"addr"`
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
}

// ServerRegistry tracks active game server instances.
type ServerRegistry interface {
	Register(ctx context.Context, info ServerInfo) error
	Deregister(ctx context.Context, serverID string) error
	FindByMapID(ctx context.Context, mapID string) ([]ServerInfo, error)
	UpdatePlayerCount(ctx context.Context, serverID string, count int) error
}

// EventStream publishes and consumes cross-server events.
type EventStream interface {
	Publish(ctx context.Context, stream string, event Event) error
	Subscribe(ctx context.Context, stream string, handler func(Event)) error
	Close() error
}
