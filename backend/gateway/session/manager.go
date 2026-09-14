package session

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// SessionData is the JSON object stored for each active session in the session
// store. It replaces the plain user_id string that was stored previously.
type SessionData struct {
	GatewayID string `json:"gateway_id"`
	ServerID  string `json:"server_id"`
	MapID     string `json:"map_id"`
	// JoinTokenJTI is the jti claim of the join token minted for this session's
	// most recent map assignment. It is the discriminator the duplicate-login
	// kick is keyed on: the game server tags each connection with the jti it
	// joined with, and a SessionSupersededEvent kicks only the connection whose
	// jti matches — so a supersede event that arrives AFTER the newer login has
	// already joined can never kick the newer connection (newest login wins).
	// Empty until EnterWorld, and for sessions written before this field
	// existed (both mean: nothing joined a game server under this session, so
	// there is nothing to kick).
	JoinTokenJTI string `json:"join_token_jti,omitempty"`
	CreatedAt    int64  `json:"created_at"`
	LastActivity int64  `json:"last_activity"`
}

// SessionManager handles session lifecycle using a SessionStore.
//
// The store is an interface (shared/storage), so the manager behaves identically
// against the in-memory implementation (tests, single-process dev) and the Redis
// implementation (multi-instance production) — the gateway itself stays stateless.
type SessionManager struct {
	store     storage.SessionStore
	gatewayID string
}

// NewSessionManager creates a SessionManager backed by the given store.
// gatewayID identifies the owning gateway instance (hostname or --instance-id).
func NewSessionManager(store storage.SessionStore, gatewayID ...string) *SessionManager {
	gid := ""
	if len(gatewayID) > 0 {
		gid = gatewayID[0]
	}
	return &SessionManager{store: store, gatewayID: gid}
}

// SessionKey returns the store key holding a user's session record
// (constants.SessionKeyPrefix + userID).
func SessionKey(userID string) string {
	return constants.SessionKeyPrefix + userID
}

// CreateSession stores a new session entry for the user as a JSON object.
// Returns the session key.
func (m *SessionManager) CreateSession(ctx context.Context, userID string) (string, error) {
	key := SessionKey(userID)
	now := time.Now().Unix()
	data := SessionData{
		GatewayID:    m.gatewayID,
		CreatedAt:    now,
		LastActivity: now,
	}
	value, err := json.Marshal(data)
	if err != nil {
		return "", fmt.Errorf("create session: marshal: %w", err)
	}
	if err := m.store.Set(ctx, key, value, constants.SessionTTL); err != nil {
		return "", fmt.Errorf("create session: %w", err)
	}
	return key, nil
}

// GetSession reads and decodes the session data for the given user. Returns
// ErrNotFound (via the store) when no session exists.
func (m *SessionManager) GetSession(ctx context.Context, userID string) (SessionData, error) {
	key := SessionKey(userID)
	raw, err := m.store.Get(ctx, key)
	if err != nil {
		return SessionData{}, fmt.Errorf("get session: %w", err)
	}
	var sd SessionData
	if err := json.Unmarshal(raw, &sd); err != nil {
		return SessionData{}, fmt.Errorf("get session: unmarshal: %w", err)
	}
	return sd, nil
}

// ValidateSession checks if a session exists and is still valid.
// Returns the stored user ID (backward-compatible: callers used to receive a
// plain user_id string; now the user_id is the key suffix).
func (m *SessionManager) ValidateSession(ctx context.Context, sessionID string) (string, error) {
	data, err := m.store.Get(ctx, sessionID)
	if err != nil {
		return "", fmt.Errorf("validate session: %w", err)
	}
	var sd SessionData
	if json.Unmarshal(data, &sd) == nil && sd.CreatedAt > 0 {
		userID := sessionID
		if len(userID) > len(constants.SessionKeyPrefix) {
			userID = userID[len(constants.SessionKeyPrefix):]
		}
		return userID, nil
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

// UpdateSession reads the session, applies mutFn, and writes it back with the
// full SessionTTL. This is used to attach server_id/map_id after map assignment.
func (m *SessionManager) UpdateSession(ctx context.Context, userID string, mutFn func(*SessionData)) error {
	key := SessionKey(userID)
	raw, err := m.store.Get(ctx, key)
	if err != nil {
		return fmt.Errorf("update session: read: %w", err)
	}
	var sd SessionData
	if err := json.Unmarshal(raw, &sd); err != nil {
		return fmt.Errorf("update session: unmarshal: %w", err)
	}
	mutFn(&sd)
	sd.LastActivity = time.Now().Unix()
	value, err := json.Marshal(sd)
	if err != nil {
		return fmt.Errorf("update session: marshal: %w", err)
	}
	if err := m.store.Set(ctx, key, value, constants.SessionTTL); err != nil {
		return fmt.Errorf("update session: write: %w", err)
	}
	return nil
}

// GatewayID returns the gateway instance ID this manager was created with.
func (m *SessionManager) GatewayID() string {
	return m.gatewayID
}

// DestroySession removes a session from the store.
func (m *SessionManager) DestroySession(ctx context.Context, sessionID string) error {
	if err := m.store.Delete(ctx, sessionID); err != nil {
		return fmt.Errorf("destroy session: %w", err)
	}
	return nil
}
