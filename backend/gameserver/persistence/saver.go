package persistence

import (
	"context"
	"log/slog"
	"time"

	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/gameserver/game"
)

// Saver periodically saves player state from the world to persistent storage.
type Saver struct {
	store    storage.PlayerStore
	world    *game.World
	mapID    string
	interval time.Duration
	logger   *slog.Logger
	stopCh   chan struct{}
}

// NewSaver creates a persistence saver.
func NewSaver(store storage.PlayerStore, world *game.World, mapID string, interval time.Duration, logger *slog.Logger) *Saver {
	return &Saver{
		store:    store,
		world:    world,
		mapID:    mapID,
		interval: interval,
		logger:   logger,
		stopCh:   make(chan struct{}),
	}
}

// Run starts the periodic save loop. Blocks until Stop() is called.
func (s *Saver) Run() {
	ticker := time.NewTicker(s.interval)
	defer ticker.Stop()

	for {
		select {
		case <-ticker.C:
			s.SaveAll()
		case <-s.stopCh:
			s.SaveAll()
			return
		}
	}
}

// Stop signals the saver to do a final save and exit.
func (s *Saver) Stop() {
	close(s.stopCh)
}

// SaveAll persists all player entities in the world. It works on value
// copies taken under the world lock, so it never races the tick loop.
func (s *Saver) SaveAll() {
	ctx := context.Background()
	players := s.world.PlayerStates()
	for _, p := range players {
		state := &storage.PlayerState{
			UserID: p.ID,
			X:      p.X,
			Y:      p.Y,
			HP:     p.HP,
			MaxHP:  p.MaxHP,
			MapID:  s.mapID,
		}
		if err := s.store.SavePlayer(ctx, state); err != nil {
			s.logger.Error("save player failed", "user", p.ID, "err", err)
		}
	}
	if len(players) > 0 {
		s.logger.Debug("saved players", "count", len(players))
	}
}
