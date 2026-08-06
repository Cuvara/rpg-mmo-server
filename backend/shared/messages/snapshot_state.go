package messages

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
func (s *SnapshotState) Apply(msg SnapshotMessage) {
	if s.Entities == nil {
		s.Entities = make(map[string]EntitySnapshot)
	}

	if msg.Full {
		clear(s.Entities)
		s.Keyframes++
	} else {
		s.Deltas++
	}

	for _, e := range msg.Entities {
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
}

// Get returns the reconstructed state of one entity.
func (s *SnapshotState) Get(id string) (EntitySnapshot, bool) {
	e, ok := s.Entities[id]
	return e, ok
}

// Len returns how many entities are currently visible.
func (s *SnapshotState) Len() int { return len(s.Entities) }
