package server

import (
	"log/slog"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/gameserver/game"
	"github.com/duycuong/rpg-mmo/gameserver/input"
	"github.com/duycuong/rpg-mmo/gameserver/snapshot"
)

// TickRunner manages the server tick loop.
type TickRunner struct {
	world       *game.World
	handler     *input.Handler
	connections *ConnectionManager
	tickRate    int
	tick        uint64
	aoiRadius   float32
	logger      *slog.Logger
	stopCh      chan struct{}
}

// NewTickRunner creates a tick runner.
func NewTickRunner(world *game.World, handler *input.Handler, conns *ConnectionManager, tickRate int, logger *slog.Logger) *TickRunner {
	return &TickRunner{
		world:       world,
		handler:     handler,
		connections: conns,
		tickRate:    tickRate,
		aoiRadius:   50.0,
		logger:      logger,
		stopCh:      make(chan struct{}),
	}
}

// Run starts the tick loop. Blocks until Stop() is called.
func (t *TickRunner) Run() {
	interval := time.Second / time.Duration(t.tickRate)
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-ticker.C:
			t.tickOnce()
		case <-t.stopCh:
			return
		}
	}
}

// Stop signals the tick loop to end.
func (t *TickRunner) Stop() {
	close(t.stopCh)
}

// TickOnce runs one tick iteration. Exported for testing.
func (t *TickRunner) TickOnce() {
	t.tickOnce()
}

func (t *TickRunner) tickOnce() {
	t.tick++

	// 1. Drain and process all pending inputs under one world write lock.
	inputs := t.world.DrainInputs()
	if len(inputs) > 0 {
		t.world.Update(func(get func(string) *game.Entity) {
			for _, pi := range inputs {
				t.handler.ProcessInputLocked(get, pi.UserID, pi.Input)
			}
		})
	}

	// 2. Build and send snapshots per player (AOI-filtered). The AOI query
	// returns value copies taken under the world lock, so encoding and
	// sending happen without holding it.
	t.connections.ForEach(func(conn *Connection) {
		var cx, cy float32
		found := false
		t.world.View(func(get func(string) *game.Entity) {
			if e := get(conn.UserID); e != nil {
				cx, cy = e.X, e.Y
				found = true
			}
		})
		if !found {
			return
		}
		nearby := snapshot.GetNearbyEntities(t.world, cx, cy, t.aoiRadius)
		snap := snapshot.EncodeSnapshot(t.tick, nearby)

		env, err := messages.NewEnvelope(messages.MsgSnapshot, snap)
		if err != nil {
			t.logger.Error("snapshot encode error", "user", conn.UserID, "err", err)
			return
		}
		conn.Send(env)
	})
}

// CurrentTick returns the current tick number.
func (t *TickRunner) CurrentTick() uint64 {
	return t.tick
}
