package messages

import (
	"errors"
	"testing"
)

// Interning is protocol STATE, not a re-encoding. Round-tripping one message
// proves nothing about it: the risk is entirely in the two sides disagreeing
// about what a handle means, and a happy-path test passes on an implementation
// that is wrong for every reconnect.
//
// So these tests deliberately desynchronise the sender and the receiver, and
// assert RECOVERY — not that divergence never happens.

// A delta that references a handle the receiver never learned must be refused,
// not guessed at. Guessing attributes an update to the wrong entity, which is
// wrong state rather than absent state and far harder to notice downstream.
func TestUnknownHandleIsRefusedNotGuessed(t *testing.T) {
	state := NewSnapshotState()

	// The receiver missed the delta that introduced handle 7.
	err := state.Apply(SnapshotMessage{
		Tick: 10,
		Entities: []EntitySnapshot{
			{Handle: 7, X: 1, Y: 2, HP: 50, MaxHP: 100}, // no ID: handle-only
		},
	})

	if !errors.Is(err, ErrUnknownHandle) {
		t.Fatalf("Apply err = %v, want ErrUnknownHandle", err)
	}
	if state.Len() != 0 {
		t.Errorf("state gained %d entities from a snapshot it could not resolve", state.Len())
	}
}

// A snapshot that fails partway must leave NOTHING applied. A half-applied
// snapshot is worse than an unapplied one, because it looks like valid state.
func TestPartiallyResolvableSnapshotAppliesNothing(t *testing.T) {
	state := NewSnapshotState()
	mustApply(t, state, SnapshotMessage{
		Tick: 1, Full: true,
		Entities: []EntitySnapshot{{ID: "known", Handle: 1, X: 1}},
	})

	err := state.Apply(SnapshotMessage{
		Tick: 2,
		Entities: []EntitySnapshot{
			{Handle: 1, X: 99},  // resolvable, and would move "known"
			{Handle: 42, X: 50}, // not resolvable
		},
	})
	if !errors.Is(err, ErrUnknownHandle) {
		t.Fatalf("Apply err = %v, want ErrUnknownHandle", err)
	}

	got, ok := state.Get("known")
	if !ok {
		t.Fatal("known entity disappeared")
	}
	if got.X != 1 {
		t.Errorf("known.X = %v, want 1 — the resolvable half of a failed snapshot must not be applied", got.X)
	}
}

// The recovery path, end to end: a desynchronised receiver asks for a keyframe,
// and the keyframe repairs it. This is the acceptance bar for the whole feature.
func TestKeyframeRecoversADesynchronisedReceiver(t *testing.T) {
	state := NewSnapshotState()

	// Establish a binding, then simulate the receiver losing its table — a
	// reconnect, a dropped delta on an unreliable transport, a client restart.
	mustApply(t, state, SnapshotMessage{
		Tick: 1, Full: true,
		Entities: []EntitySnapshot{{ID: "e1", Handle: 1, X: 1}},
	})
	state.handles = map[uint32]string{} // the divergence

	// Now the sender, unaware, sends a handle-only delta. The receiver refuses.
	if err := state.Apply(SnapshotMessage{
		Tick: 2, Entities: []EntitySnapshot{{Handle: 1, X: 2}},
	}); !errors.Is(err, ErrUnknownHandle) {
		t.Fatalf("expected the desync to be detected, got %v", err)
	}

	// The receiver requests a keyframe (MsgResync). The keyframe re-introduces
	// every binding, and the receiver is whole again.
	mustApply(t, state, SnapshotMessage{
		Tick: 3, Full: true,
		Entities: []EntitySnapshot{{ID: "e1", Handle: 1, X: 3}},
	})

	got, ok := state.Get("e1")
	if !ok {
		t.Fatal("e1 missing after the recovering keyframe")
	}
	if got.X != 3 {
		t.Errorf("e1.X = %v, want 3", got.X)
	}

	// And the repaired binding works for subsequent handle-only deltas.
	mustApply(t, state, SnapshotMessage{
		Tick: 4, Entities: []EntitySnapshot{{Handle: 1, X: 4}},
	})
	if got, _ := state.Get("e1"); got.X != 4 {
		t.Errorf("e1.X = %v, want 4 — the rebuilt binding should resolve", got.X)
	}
}

// A keyframe resets the sender's handle space, so the receiver MUST drop its old
// bindings. If it kept them, a handle reused for a different entity in the next
// interval would silently resolve to the previous one — the exact
// wrong-state-not-absent-state failure this design avoids.
func TestKeyframeClearsStaleBindings(t *testing.T) {
	state := NewSnapshotState()
	mustApply(t, state, SnapshotMessage{
		Tick: 1, Full: true,
		Entities: []EntitySnapshot{{ID: "old", Handle: 1, X: 1}},
	})

	// New interval: handle 1 now means a different entity.
	mustApply(t, state, SnapshotMessage{
		Tick: 2, Full: true,
		Entities: []EntitySnapshot{{ID: "new", Handle: 1, X: 2}},
	})

	if _, ok := state.Get("old"); ok {
		t.Error("the keyframe should have dropped the previous entity set")
	}
	if err := state.Apply(SnapshotMessage{
		Tick: 3, Entities: []EntitySnapshot{{Handle: 1, X: 5}},
	}); err != nil {
		t.Fatalf("handle 1 should resolve to the NEW binding: %v", err)
	}
	if got, _ := state.Get("new"); got.X != 5 {
		t.Errorf("new.X = %v, want 5 — handle 1 must resolve to the current binding", got.X)
	}
	if _, ok := state.Get("old"); ok {
		t.Error("handle 1 resolved to the stale binding")
	}
}

// A sender that does not intern (handle 0) must keep working unchanged — that is
// what makes the field optional and the change safe for the JSON encoding.
func TestUninternedSnapshotsStillWork(t *testing.T) {
	state := NewSnapshotState()
	mustApply(t, state, SnapshotMessage{
		Tick: 1, Full: true,
		Entities: []EntitySnapshot{{ID: "e1", X: 1}, {ID: "e2", X: 2}},
	})
	mustApply(t, state, SnapshotMessage{
		Tick: 2, Entities: []EntitySnapshot{{ID: "e1", X: 9}},
	})

	if got, _ := state.Get("e1"); got.X != 9 {
		t.Errorf("e1.X = %v, want 9", got.X)
	}
	if state.Len() != 2 {
		t.Errorf("Len = %d, want 2", state.Len())
	}
}

// The id survives the protobuf round trip on the introducing message, and the
// handle survives on both.
func TestHandleAndIDRoundTripOverProtobuf(t *testing.T) {
	msg := SnapshotMessage{
		Tick: 5,
		Entities: []EntitySnapshot{
			{ID: "lt-000000000042", Handle: 3, Type: "player", X: 1, Y: 2, HP: 7, MaxHP: 8},
			{Handle: 4, Type: "mob", X: 3, Y: 4, HP: 1, MaxHP: 2},
		},
	}
	env, err := NewEnvelopeAs(EncodingProto, MsgSnapshot, msg)
	if err != nil {
		t.Fatal(err)
	}
	var got SnapshotMessage
	if err := env.UnmarshalPayload(&got); err != nil {
		t.Fatal(err)
	}

	if got.Entities[0].ID != "lt-000000000042" || got.Entities[0].Handle != 3 {
		t.Errorf("introducing entity = %+v, want id and handle both preserved", got.Entities[0])
	}
	if got.Entities[1].ID != "" || got.Entities[1].Handle != 4 {
		t.Errorf("handle-only entity = %+v, want empty id and handle 4", got.Entities[1])
	}
}

// The saving, asserted rather than assumed: after the introducing message, an
// entity costs a varint instead of a ~17-byte string.
func TestInterningShrinksRepeatedMentions(t *testing.T) {
	build := func(intern bool) int {
		m := SnapshotMessage{Tick: 100, AckTick: 99}
		for i := 0; i < 50; i++ {
			e := EntitySnapshot{ID: "lt-000000000042", Type: "player", X: float32(i), Y: float32(i), HP: 100, MaxHP: 100}
			if intern {
				e.ID = "" // already introduced this interval
				e.Handle = uint32(i + 1)
			}
			m.Entities = append(m.Entities, e)
		}
		env, _ := NewEnvelopeAs(EncodingProto, MsgSnapshot, m)
		b, _ := EncodeBody(env)
		return len(b)
	}

	plain, interned := build(false), build(true)
	saving := 1 - float64(interned)/float64(plain)
	t.Logf("50-entity delta: plain=%dB interned=%dB saving=%.1f%%", plain, interned, saving*100)

	if saving < 0.30 {
		t.Errorf("expected >= 30%% off a repeat-mention delta, got %.1f%%", saving*100)
	}
}

// A KEYFRAME carrying a bare handle must be refused even when that handle is
// still resolvable from the interval the keyframe is ending.
//
// This is the dangerous shape, and it is why the check must not consult the
// table: the lookup SUCCEEDS and returns the previous interval's entity, so
// resolving would silently rebind this entity to whatever last held that handle
// number. Nothing downstream can detect it — every handle resolved, no error was
// raised, and the client renders one entity's updates as another's.
//
// No well-formed sender can produce this frame: a sender clears its handle table
// and restarts numbering before encoding a keyframe (see
// GameServer/Snapshot/SnapshotDeltaState.EncodeFull), so every entity in a
// keyframe carries both id and handle. The guard therefore costs nothing on
// valid input and exists to defend against a future or third-party sender.
//
// If this test is ever in the way, the guard is what it is protecting — do not
// delete it without re-reading the interning rules in
// gameserver-dotnet/docs/API.md.
func TestKeyframeWithBareHandleIsRefusedNotResolvedAgainstStaleTable(t *testing.T) {
	state := NewSnapshotState()

	// Interval 1 binds handle 1 -> "alice".
	mustApply(t, state, SnapshotMessage{
		Tick: 1,
		Full: true,
		Entities: []EntitySnapshot{
			{ID: "alice", Handle: 1, Type: "player", HP: 100, MaxHP: 100},
		},
	})
	if got := state.Entities["alice"]; got.ID != "alice" {
		t.Fatalf("setup failed: alice not applied")
	}

	// A malformed keyframe reuses handle 1 with no id. Handle 1 IS still bound
	// (to alice), so a table lookup would succeed and silently mislabel this
	// entity as alice.
	err := state.Apply(SnapshotMessage{
		Tick: 2,
		Full: true,
		Entities: []EntitySnapshot{
			{Handle: 1, X: 42, Y: 42, HP: 10, MaxHP: 100}, // no ID
		},
	})

	if !errors.Is(err, ErrUnknownHandle) {
		t.Fatalf("Apply err = %v, want ErrUnknownHandle for a bare handle on a keyframe", err)
	}
	// All-or-nothing: the rejected keyframe must not have cleared or replaced
	// anything either. Rejecting AFTER clearing would leave an empty world until
	// a resync completed.
	if state.Len() != 1 {
		t.Errorf("state has %d entities after a rejected keyframe, want 1 (unchanged)", state.Len())
	}
	if got, ok := state.Entities["alice"]; !ok || got.HP != 100 {
		t.Errorf("alice = %+v (ok=%v); a rejected keyframe must leave state untouched", got, ok)
	}
	if _, ok := state.Entities[""]; ok {
		t.Error("state gained an entity under an empty id: the bare handle was applied, not rejected")
	}
}

func mustApply(t *testing.T, s *SnapshotState, msg SnapshotMessage) {
	t.Helper()
	if err := s.Apply(msg); err != nil {
		t.Fatalf("Apply(tick %d): %v", msg.Tick, err)
	}
}
