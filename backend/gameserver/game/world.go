package game

import (
	"math"
	"sync"

	"github.com/duycuong/rpg-mmo/shared/messages"
)

// PendingInput pairs a user ID with their input for the current tick.
type PendingInput struct {
	UserID string
	Input  messages.InputMessage
}

// World holds all entities and pending inputs for the game simulation.
type World struct {
	mu       sync.RWMutex
	entities map[string]*Entity
	pending  []PendingInput
}

// NewWorld creates an empty world.
func NewWorld() *World {
	return &World{
		entities: make(map[string]*Entity),
	}
}

// AddEntity adds an entity to the world.
func (w *World) AddEntity(e *Entity) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.entities[e.ID] = e
}

// RemoveEntity removes an entity from the world.
func (w *World) RemoveEntity(id string) {
	w.mu.Lock()
	defer w.mu.Unlock()
	delete(w.entities, id)
}

// GetEntity returns an entity by ID, or nil if not found.
func (w *World) GetEntity(id string) *Entity {
	w.mu.RLock()
	defer w.mu.RUnlock()
	return w.entities[id]
}

// GetEntitiesInRange returns value copies of all entities within radius of
// (cx, cy). Copies are taken under the read lock so callers can use them
// without racing entity mutations done under Update.
func (w *World) GetEntitiesInRange(cx, cy, radius float32) []Entity {
	w.mu.RLock()
	defer w.mu.RUnlock()
	r2 := radius * radius
	var result []Entity
	for _, e := range w.entities {
		dx := e.X - cx
		dy := e.Y - cy
		if dx*dx+dy*dy <= r2 {
			result = append(result, *e)
		}
	}
	return result
}

// Update runs fn while holding the world write lock. Every entity field
// mutation that can be observed from another goroutine (tick loop, saver,
// reconnect reattach, tests) must go through here. get returns live entity
// pointers without additional locking — do not retain them past fn.
func (w *World) Update(fn func(get func(id string) *Entity)) {
	w.mu.Lock()
	defer w.mu.Unlock()
	fn(func(id string) *Entity { return w.entities[id] })
}

// View runs fn while holding the world read lock, for multi-entity reads
// that must be consistent with writers using Update. get returns live entity
// pointers — read-only, do not retain them past fn.
func (w *World) View(fn func(get func(id string) *Entity)) {
	w.mu.RLock()
	defer w.mu.RUnlock()
	fn(func(id string) *Entity { return w.entities[id] })
}

// PlayerStates returns value copies of all player entities, taken under the
// read lock so they are safe to persist from the saver goroutine.
func (w *World) PlayerStates() []Entity {
	w.mu.RLock()
	defer w.mu.RUnlock()
	var result []Entity
	for _, e := range w.entities {
		if e.Type == "player" {
			result = append(result, *e)
		}
	}
	return result
}

// AllEntities returns a snapshot of all entities.
func (w *World) AllEntities() []*Entity {
	w.mu.RLock()
	defer w.mu.RUnlock()
	result := make([]*Entity, 0, len(w.entities))
	for _, e := range w.entities {
		result = append(result, e)
	}
	return result
}

// PlayerEntities returns entities of type "player".
func (w *World) PlayerEntities() []*Entity {
	w.mu.RLock()
	defer w.mu.RUnlock()
	var result []*Entity
	for _, e := range w.entities {
		if e.Type == "player" {
			result = append(result, e)
		}
	}
	return result
}

// PushInput enqueues an input for processing in the next tick.
func (w *World) PushInput(userID string, input messages.InputMessage) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.pending = append(w.pending, PendingInput{UserID: userID, Input: input})
}

// DrainInputs returns and clears all pending inputs.
func (w *World) DrainInputs() []PendingInput {
	w.mu.Lock()
	defer w.mu.Unlock()
	inputs := w.pending
	w.pending = nil
	return inputs
}

// LastInputTick returns the last acknowledged client input tick for a player,
// or 0 if the entity is unknown. Exposed for input acknowledgement until the
// wire protocol carries an ack field (see docs/DESIGN.md).
func (w *World) LastInputTick(id string) uint64 {
	w.mu.RLock()
	defer w.mu.RUnlock()
	e := w.entities[id]
	if e == nil {
		return 0
	}
	return e.LastInputTick
}

// EntityCount returns the number of entities in the world.
func (w *World) EntityCount() int {
	w.mu.RLock()
	defer w.mu.RUnlock()
	return len(w.entities)
}

// Distance returns the distance between two points.
func Distance(x1, y1, x2, y2 float32) float32 {
	dx := float64(x2 - x1)
	dy := float64(y2 - y1)
	return float32(math.Sqrt(dx*dx + dy*dy))
}
