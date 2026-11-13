package auth

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/duycuong/rpg-mmo/nakama/character"
	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/heroiclabs/nakama-common/runtime"
)

// ownsGameCharacter checks the selected game's roster without falling back to
// the default collection. The default game continues to use character.Owns.
func ownsGameCharacter(ctx context.Context, nk character.Store, game, userID, characterID string) (bool, error) {
	collection := gamectx.Scoped(game, character.Collection)
	objects, err := nk.StorageRead(ctx, []*runtime.StorageRead{{
		Collection: collection,
		Key:        character.RosterKey,
		UserID:     userID,
	}})
	if err != nil {
		return false, fmt.Errorf("read game roster %s/%s: %w", game, userID, err)
	}
	for _, object := range objects {
		if object.GetCollection() != collection || object.GetKey() != character.RosterKey || object.GetUserId() != userID {
			continue
		}
		var roster character.Roster
		if err := json.Unmarshal([]byte(object.GetValue()), &roster); err != nil {
			return false, fmt.Errorf("corrupt game roster %s/%s: %w", game, userID, err)
		}
		for _, entry := range roster.Characters {
			if entry.ID == characterID {
				return true, nil
			}
		}
	}
	return false, nil
}
