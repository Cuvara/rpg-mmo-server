package session

import (
	"context"
	"fmt"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// SessionManager handles session lifecycle using a SessionStore.
//
// The store is an interface (shared/storage), so the manager behaves identically
// against the in-memory implementation (tests, single-process dev) and the Redis
// implementation (multi-instance production) — the gateway itself stays stateless.
type SessionManager struct {
	store storage.SessionStore
}

// NewSessionManager creates a SessionManager backed by the given store.
func NewSessionManager(store storage.SessionStore) *SessionManager {
	return &SessionManager{store: store}
}

// SessionKey returns the store key holding a user's session record
// (constants.SessionKeyPrefix + userID).
func SessionKey(userID string) string {
	return constants.SessionKeyPrefix + userID
}

// CreateSession stores a new session entry for the user.
// The session ID is the user ID prefixed with the standard key pattern.
// Returns the session key.
func (m *SessionManager) CreateSession(ctx context.Context, userID string) (string, error) {
	key := SessionKey(userID)
	if err := m.store.Set(ctx, key, []byte(userID), constants.SessionTTL); err != nil {
		return "", fmt.Errorf("create session: %w", err)
	}
	return key, nil
}

// ValidateSession checks if a session exists and is still valid.
// Returns the stored user ID.
func (m *SessionManager) ValidateSession(ctx context.Context, sessionID string) (string, error) {
	data, err := m.store.Get(ctx, sessionID)
	if err != nil {
		return "", fmt.Errorf("validate session: %w", err)
	}
	return string(data), nil
}

// RefreshSession extends the TTL of a live session. Called on client activity so
// an active player never expires mid-session. Returns an error when the session
// is already gone, letting the caller force a re-authentication.
func (m *SessionManager) RefreshSession(ctx context.Context, sessionID string) error {
	if err := m.store.Refresh(ctx, sessionID, constants.SessionTTL); err != nil {
		return fmt.Errorf("refresh session: %w", err)
	}
	return nil
}

// DestroySession removes a session from the store.
func (m *SessionManager) DestroySession(ctx context.Context, sessionID string) error {
	if err := m.store.Delete(ctx, sessionID); err != nil {
		return fmt.Errorf("destroy session: %w", err)
	}
	return nil
}
