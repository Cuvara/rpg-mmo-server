package snapshot

import (
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/gameserver/game"
)

// EncodeSnapshot builds a SnapshotMessage from a tick number and entity list.
func EncodeSnapshot(tick uint64, entities []game.Entity) messages.SnapshotMessage {
	snaps := make([]messages.EntitySnapshot, 0, len(entities))
	for _, e := range entities {
		snaps = append(snaps, messages.EntitySnapshot{
			ID:    e.ID,
			Type:  e.Type,
			X:     e.X,
			Y:     e.Y,
			HP:    e.HP,
			MaxHP: e.MaxHP,
		})
	}
	return messages.SnapshotMessage{
		Tick:     tick,
		Entities: snaps,
	}
}
