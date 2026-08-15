package messages

import "fmt"

// SnapshotState reconstructs authoritative world state from the keyframe/delta
// snapshot stream produced by the game server.
//
// The game server sends a full keyframe on join, on MsgResync request, and every
// N ticks; every other snapshot only carries entities whose visible state changed
// plus a list of entities that left the AOI. Any client (Go smoketest, integration
// test, Unity) must merge them the same way, so the merge lives here next to the
// wire types rather than being reimplemented per consumer.
//
// SnapshotState is not safe for concurrent use.
type SnapshotState struct {
	// Tick is the server tick of the most recently applied snapshot.
	Tick uint64
	// AckTick is the newest input acknowledgement seen. It is monotonic: a
	// snapshot that omits ack_tick (zero) never lowers it.
	AckTick uint64
	// Entities is the reconstructed AOI set, keyed by entity ID.
	Entities map[string]EntitySnapshot
	// Keyframes and Deltas count the snapshots applied so far.
	Keyframes int
	Deltas    int

	// handles maps interned entity handles to ids for the current keyframe
	// interval. Cleared on every keyframe, because the sender resets its handle
	// space there too — that shared reset point is what makes the mapping
	// self-repairing rather than something both sides have to agree to forget.
	handles map[uint32]string
}

// NewSnapshotState returns an empty state ready to accept snapshots.
func NewSnapshotState() *SnapshotState {
	return &SnapshotState{Entities: make(map[string]EntitySnapshot)}
}

// Apply merges one snapshot into the state.
//
// A keyframe (Full=true) replaces the entity set outright. A delta upserts the
// carried entities and deletes the ones listed in Removed. Out-of-order or
// duplicated snapshots (Tick <= current Tick) are still applied — the transport
// is ordered, and dropping them would silently diverge — but Tick itself never
// moves backwards.
//
// Returns ErrUnknownHandle when a delta references an interned handle this state
// has no binding for, or when a KEYFRAME carries a bare handle at all (which a
// well-formed sender never emits). The state is left untouched in either case: a
// partially applied snapshot is worse than an unapplied one, because it looks
// like valid state. The caller must request a keyframe (MsgResync) and apply
// that instead.
func (s *SnapshotState) Apply(msg SnapshotMessage) error {
	if s.Entities == nil {
		s.Entities = make(map[string]EntitySnapshot)
	}
	if s.handles == nil {
		s.handles = make(map[uint32]string)
	}

	// Resolve every entity BEFORE mutating anything, so an unknown handle
	// aborts the whole snapshot rather than leaving half of it applied.
	resolved := make([]EntitySnapshot, len(msg.Entities))
	for i, e := range msg.Entities {
		if e.Handle != 0 && e.ID == "" {
			// A KEYFRAME must introduce every binding it uses, so a bare handle
			// here means the sender is malformed. Reject it WITHOUT consulting
			// the table: those bindings belong to the interval this keyframe is
			// ending, so resolving against them would silently attach this
			// entity to whatever held the same handle number last interval —
			// the wrong-entity corruption interning is most likely to cause,
			// and the one nothing downstream can detect. The check can never
			// fire on valid input: a sender clears its handle table and
			// restarts numbering before encoding a keyframe, so every entity in
			// one carries both id and handle.
			if msg.Full {
				return fmt.Errorf("handle %d on keyframe at tick %d (a keyframe must carry the id): %w",
					e.Handle, msg.Tick, ErrUnknownHandle)
			}
			id, ok := s.handles[e.Handle]
			if !ok {
				return fmt.Errorf("handle %d at tick %d: %w", e.Handle, msg.Tick, ErrUnknownHandle)
			}
			e.ID = id
		}
		resolved[i] = e
	}

	if msg.Full {
		clear(s.Entities)
		// The sender restarts its handle space at every keyframe, so old
		// bindings are not merely stale, they are actively misleading.
		clear(s.handles)
		s.Keyframes++
	} else {
		s.Deltas++
	}

	for _, e := range resolved {
		if e.Handle != 0 {
			s.handles[e.Handle] = e.ID
		}
		s.Entities[e.ID] = e
	}
	for _, id := range msg.Removed {
		delete(s.Entities, id)
	}

	if msg.Tick > s.Tick {
		s.Tick = msg.Tick
	}
	if msg.AckTick > s.AckTick {
		s.AckTick = msg.AckTick
	}
	return nil
}

// Get returns the reconstructed state of one entity.
func (s *SnapshotState) Get(id string) (EntitySnapshot, bool) {
	e, ok := s.Entities[id]
	return e, ok
}

// Len returns how many entities are currently visible.
func (s *SnapshotState) Len() int { return len(s.Entities) }
