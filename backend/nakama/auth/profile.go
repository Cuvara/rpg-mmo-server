package auth

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"time"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// Storage location of the player profile record.
const (
	// ProfileCollection is the Nakama storage collection holding player data.
	ProfileCollection = "player"
	// ProfileKey is the storage key of the profile record inside the collection.
	ProfileKey = "profile"
)

// StartingLevel is the level assigned to a freshly created player profile.
const StartingLevel = 1

// Profile is the player record stored in the Nakama storage engine under
// collection "player", key "profile", owned by the player itself.
type Profile struct {
	Level       int    `json:"level"`
	CreatedAt   int64  `json:"created_at"`
	DisplayName string `json:"display_name"`
}

// profileStore is the narrow slice of runtime.NakamaModule the profile logic
// needs. runtime.NakamaModule satisfies it, which keeps the logic unit-testable.
type profileStore interface {
	StorageRead(ctx context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error)
	StorageWrite(ctx context.Context, writes []*runtime.StorageWrite) ([]*api.StorageObjectAck, error)
}

// EnsureProfile creates the player profile on first login. It reports whether a
// new profile was written; an existing profile is left untouched.
func EnsureProfile(ctx context.Context, nk profileStore, userID, displayName string) (bool, error) {
	if userID == "" {
		return false, fmt.Errorf("ensure profile: empty user id")
	}

	objects, err := nk.StorageRead(ctx, []*runtime.StorageRead{{
		Collection: ProfileCollection,
		Key:        ProfileKey,
		UserID:     userID,
	}})
	if err != nil {
		return false, fmt.Errorf("ensure profile: read: %w", err)
	}
	if len(objects) > 0 {
		return false, nil
	}

	if displayName == "" {
		displayName = "Player-" + shortID(userID)
	}
	profile := Profile{
		Level:       StartingLevel,
		CreatedAt:   time.Now().Unix(),
		DisplayName: displayName,
	}
	value, err := json.Marshal(profile)
	if err != nil {
		return false, fmt.Errorf("ensure profile: marshal: %w", err)
	}

	if _, err := nk.StorageWrite(ctx, []*runtime.StorageWrite{{
		Collection:      ProfileCollection,
		Key:             ProfileKey,
		UserID:          userID,
		Value:           string(value),
		PermissionRead:  2, // public read
		PermissionWrite: 0, // server-authoritative writes only
	}}); err != nil {
		return false, fmt.Errorf("ensure profile: write: %w", err)
	}
	return true, nil
}

// shortID returns a short, stable suffix derived from a user ID.
func shortID(userID string) string {
	if len(userID) <= 8 {
		return userID
	}
	return userID[:8]
}

// AfterAuthenticateDevice bootstraps the player profile after a device login.
func AfterAuthenticateDevice(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, out *api.Session, _ *api.AuthenticateDeviceRequest) error {
	return afterAuthenticate(ctx, logger, nk, out, "device")
}

// AfterAuthenticateEmail bootstraps the player profile after an email login.
func AfterAuthenticateEmail(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, out *api.Session, _ *api.AuthenticateEmailRequest) error {
	return afterAuthenticate(ctx, logger, nk, out, "email")
}

func afterAuthenticate(ctx context.Context, logger runtime.Logger, nk runtime.NakamaModule, out *api.Session, method string) error {
	userID, _ := ctx.Value(runtime.RUNTIME_CTX_USER_ID).(string)
	username, _ := ctx.Value(runtime.RUNTIME_CTX_USERNAME).(string)
	if userID == "" {
		logger.Warn("after authenticate %s: missing user id in context", method)
		return nil
	}

	created, err := EnsureProfile(ctx, nk, userID, username)
	if err != nil {
		return fmt.Errorf("after authenticate %s: %w", method, err)
	}
	if created {
		logger.Info("created player profile for %s (%s login, new=%v)", userID, method, out.GetCreated())
	}
	return nil
}
