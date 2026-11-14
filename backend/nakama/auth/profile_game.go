package auth

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/heroiclabs/nakama-common/runtime"
)

// EnsureGameProfile creates the selected game's profile on first login or
// game-scoped RPC. Existing profiles are left untouched, as in EnsureProfile.
// The caller must validate game against the configured allow-list first.
func EnsureGameProfile(ctx context.Context, nk profileStore, game, userID, displayName string) (bool, error) {
	if userID == "" {
		return false, fmt.Errorf("ensure game profile: empty user id")
	}
	if nk == nil {
		return false, fmt.Errorf("ensure game profile: no storage")
	}
	collection := gamectx.Scoped(game, ProfileCollection)
	objects, err := nk.StorageRead(ctx, []*runtime.StorageRead{{
		Collection: collection,
		Key:        ProfileKey,
		UserID:     userID,
	}})
	if err != nil {
		return false, fmt.Errorf("ensure game profile %s: read: %w", game, err)
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
		return false, fmt.Errorf("ensure game profile %s: marshal: %w", game, err)
	}
	if _, err := nk.StorageWrite(ctx, []*runtime.StorageWrite{{
		Collection:      collection,
		Key:             ProfileKey,
		UserID:          userID,
		Value:           string(value),
		PermissionRead:  2,
		PermissionWrite: 0,
	}}); err != nil {
		return false, fmt.Errorf("ensure game profile %s: write: %w", game, err)
	}
	return true, nil
}
