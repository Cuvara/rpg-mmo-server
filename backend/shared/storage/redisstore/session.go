package redisstore

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/redis/go-redis/v9"
)

// Compile-time checks that the Redis implementations satisfy the shared interfaces.
var (
	_ storage.SessionStore   = (*SessionStore)(nil)
	_ storage.ServerRegistry = (*ServerRegistry)(nil)
	_ storage.EventStream    = (*EventStream)(nil)
)

// NewRedisClient and the client tuning defaults live in client.go.

// --- RedisSessionStore ---

// RedisSessionStore is a Redis-backed SessionStore. Keys are used verbatim;
// callers build them with constants.SessionKeyPrefix + userID.
type SessionStore struct {
	client redis.UniversalClient
	owned  bool // true when the client was created by this store
}

// NewRedisSessionStore connects to Redis at addr and returns a SessionStore.
func NewSessionStore(addr, password string) *SessionStore {
	return &SessionStore{client: NewRedisClient(addr, password), owned: true}
}

// NewRedisSessionStoreWithClient wraps an existing client (shared pool, tests).
// Close does not shut the client down.
func NewSessionStoreWithClient(client redis.UniversalClient) *SessionStore {
	return &SessionStore{client: client}
}

// Set writes the session value with the given TTL.
func (s *SessionStore) Set(ctx context.Context, key string, value []byte, ttl time.Duration) error {
	if err := s.client.Set(ctx, key, value, ttl).Err(); err != nil {
		return fmt.Errorf("redis set session %s: %w", key, err)
	}
	return nil
}

// Get reads the session value. Returns ErrNotFound when missing or expired.
func (s *SessionStore) Get(ctx context.Context, key string) ([]byte, error) {
	val, err := s.client.Get(ctx, key).Bytes()
	if errors.Is(err, redis.Nil) {
		return nil, fmt.Errorf("session %s: %w", key, storage.ErrNotFound)
	}
	if err != nil {
		return nil, fmt.Errorf("redis get session %s: %w", key, err)
	}
	return val, nil
}

// Delete removes the session.
func (s *SessionStore) Delete(ctx context.Context, key string) error {
	if err := s.client.Del(ctx, key).Err(); err != nil {
		return fmt.Errorf("redis del session %s: %w", key, err)
	}
	return nil
}

// Refresh extends the TTL of an existing session without rewriting the value.
// Returns ErrNotFound when the key is gone.
func (s *SessionStore) Refresh(ctx context.Context, key string, ttl time.Duration) error {
	ok, err := s.client.Expire(ctx, key, ttl).Result()
	if err != nil {
		return fmt.Errorf("redis expire session %s: %w", key, err)
	}
	if !ok {
		return fmt.Errorf("session %s: %w", key, storage.ErrNotFound)
	}
	return nil
}

// Close releases the underlying client if this store created it.
func (s *SessionStore) Close() error {
	if !s.owned {
		return nil
	}
	if err := s.client.Close(); err != nil {
		return fmt.Errorf("close redis client: %w", err)
	}
	return nil
}
