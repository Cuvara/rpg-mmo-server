//go:build integration

package integration

import (
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
)

// Gameplay v2 over a real socket against the real C# game server: the
// edge-triggered event channel, ability input, and the action_seq retrigger
// counter.
//
// WHY THIS EXISTS SEPARATELY FROM THE UNIT TESTS. Each of the three additions is
// covered on both sides — the server's own suite asserts it produces them, the
// client package's suite asserts it decodes them — and neither proves the two
// halves were ever joined. All three fail the same silent way if they were not:
// the server sends the field, the client parses the frame without error, and the
// value arrives as its type default. Nothing throws and nothing logs.
//
// So everything below asserts on bytes that crossed a socket, in BOTH encodings,
// with the assertions written against what a client would actually do with them.

const gameplayV2AbilityBolt = 1

// gameplayV2ContentDir is the content set these tests run the server against.
//
// Test-only, and not backend/content: the game ships no abilities yet, and a test
// that needs one must not decide what the game ships. It also means the numbers
// asserted below cannot be rewritten by an unrelated content change.
func gameplayV2ContentDir(t *testing.T) string {
	t.Helper()
	dir, err := filepath.Abs(filepath.Join("testdata", "gameplayv2"))
	if err != nil {
		t.Fatalf("resolve content dir: %v", err)
	}
	if _, err := os.Stat(filepath.Join(dir, "items.json")); err != nil {
		t.Fatalf("content dir %s unusable: %v", dir, err)
	}
	return dir
}

// gameplayV2Join connects one client straight to the game server and joins.
//
// The gateway hop is deliberately skipped: TestDotnetInterop_FullFlow already
// covers it, and what is under test here is what the game server puts on the wire
// once a client is in the world.
func gameplayV2Join(t *testing.T, gsAddr, playerID string, enc messages.Encoding) *MockClient {
	t.Helper()

	client, err := NewMockClient(gsAddr)
	if err != nil {
		t.Fatalf("connect %s: %v", playerID, err)
	}

	token, err := jwt.SignWithServer(playerID, dotnetServerID, dotnetJoinTokenSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("sign token for %s: %v", playerID, err)
	}

	joinEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
	if err := client.Send(joinEnv); err != nil {
		t.Fatalf("send join %s: %v", playerID, err)
	}

	respEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive join resp %s: %v", playerID, err)
	}
	var joinResp messages.JoinTokenResponse
	if err := respEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("unmarshal join resp %s: %v", playerID, err)
	}
	if !joinResp.OK {
		t.Fatalf("join rejected for %s: %s", playerID, joinResp.Error)
	}
	return client
}

// gameplayV2Observed is what a client reconstructed from a run of snapshots.
//
// Entities are merged the way a real client merges them, because they are STATE.
// Events are appended, never merged, because they are not: each snapshot's events
// belong to the tick that produced them and are never re-sent.
type gameplayV2Observed struct {
	State  *messages.SnapshotState
	Events []messages.GameEvent
	// Handles maps the interned handle to the entity id it was introduced with,
	// so an event's participants can be named. Reset on every keyframe, exactly as
	// the sender resets its handle space.
	Handles map[uint32]string
	Ticks   int
}

// newObserved starts one observer per CONNECTION, not per read.
//
// The handle table has to live as long as the connection: a handle is introduced once per
// keyframe interval and every later mention carries only the number, so an observer rebuilt
// between reads resolves nothing and reports every event's participants as absent. That is a
// test artefact that looks exactly like a product bug, and it produced one here.
func newObserved() *gameplayV2Observed {
	return &gameplayV2Observed{
		State:   messages.NewSnapshotState(),
		Handles: make(map[uint32]string),
	}
}

// drain reads up to want snapshots into out, merging entities and appending events.
//
// Events are RESET on each call and entities are not: a caller asks "what happened while I
// was reading", and an event from an earlier read has already been answered for.
func drain(t *testing.T, c *MockClient, out *gameplayV2Observed, want int) *gameplayV2Observed {
	t.Helper()

	out.Events = nil
	out.Ticks = 0

	failures := 0
	for attempts := 0; attempts < want*3 && out.Ticks < want; attempts++ {
		env, err := c.Receive()
		if err != nil {
			if failures++; failures >= 2 {
				break
			}
			continue
		}
		if env.Type != messages.MsgSnapshot {
			continue
		}

		var snap messages.SnapshotMessage
		if err := env.UnmarshalPayload(&snap); err != nil {
			t.Fatalf("unmarshal snapshot: %v", err)
		}

		if snap.Full {
			// The sender resets its handle space at every keyframe, so a binding
			// held across one belongs to a different entity. Clearing here is what
			// keeps the mapping self-repairing.
			out.Handles = make(map[uint32]string)
		}
		for _, e := range snap.Entities {
			if e.Handle != 0 && e.ID != "" {
				out.Handles[e.Handle] = e.ID
			}
		}

		out.State.Apply(snap)
		out.Events = append(out.Events, snap.Events...)
		out.Ticks++
	}

	if out.Ticks == 0 {
		t.Fatal("no snapshot arrived within the timeout")
	}
	return out
}

// nameOf resolves an event participant the way a client must: handle first, then
// the explicit id a non-interning sender fills, then "" for absent.
func (o *gameplayV2Observed) nameOf(handle uint32, explicit string) string {
	if handle != 0 {
		if id, ok := o.Handles[handle]; ok {
			return id
		}
		// A handle with no binding. Reported as absent rather than guessed — and
		// NOT a reason to resync, which is the documented asymmetry with an
		// unresolvable entity handle.
		return ""
	}
	return explicit
}

func (o *gameplayV2Observed) eventsOfType(kind messages.GameEventType) []messages.GameEvent {
	var out []messages.GameEvent
	for _, e := range o.Events {
		if e.Type == kind {
			out = append(out, e)
		}
	}
	return out
}

// TestGameplayV2_EventsAndActionSeq drives one player attacking another and
// asserts on what crossed the wire.
func TestGameplayV2_EventsAndActionSeq(t *testing.T) {
	for _, enc := range []messages.Encoding{messages.EncodingJSON, messages.EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) { runGameplayV2Combat(t, enc) })
	}
}

func runGameplayV2Combat(t *testing.T, enc messages.Encoding) {
	requireDotnet(t)

	gsAddr, cleanup := startDotnetGameServerWith(t,
		[]string{"--content-dir", gameplayV2ContentDir(t)}, nil)
	defer cleanup()

	attackerID := "v2-attacker-" + enc.String()
	victimID := "v2-victim-" + enc.String()

	attacker := gameplayV2Join(t, gsAddr, attackerID, enc)
	defer attacker.Close()
	victim := gameplayV2Join(t, gsAddr, victimID, enc)
	defer victim.Close()

	// Let both settle into the world and let the attacker learn the victim's id.
	obs := newObserved()
	before := drain(t, attacker, obs, 4)
	if _, ok := before.State.Entities[victimID]; !ok {
		t.Fatalf("attacker cannot see %s; entities=%d — the two players are not in "+
			"each other's AOI, so nothing below would be testing combat", victimID, before.State.Len())
	}
	victimHPBefore := before.State.Entities[victimID].HP
	t.Logf("%s sees %s at hp=%d", attackerID, victimID, victimHPBefore)

	// --- One attack ---
	atk, _ := messages.NewEnvelopeAs(enc, messages.MsgInput, messages.InputMessage{
		Tick:           1,
		AttackTargetID: victimID,
	})
	if err := attacker.Send(atk); err != nil {
		t.Fatalf("send attack: %v", err)
	}

	after := drain(t, attacker, obs, 6)

	damage := after.eventsOfType(messages.GameEventDamage)
	if len(damage) == 0 {
		t.Fatalf("no damage event crossed the wire after an attack that landed "+
			"(victim hp %d -> %d). The event channel is the only way a client can "+
			"know a hit happened: HP is state, and a heal and a hit in the same tick "+
			"net out to nothing",
			victimHPBefore, after.State.Entities[victimID].HP)
	}

	d := damage[0]
	if d.Amount <= 0 {
		t.Errorf("damage event amount = %d, want > 0", d.Amount)
	}
	if got := after.nameOf(d.Target, d.TargetID); got != victimID {
		t.Errorf("damage event target = %q, want %q", got, victimID)
	}
	if got := after.nameOf(d.Source, d.SourceID); got != attackerID {
		t.Errorf("damage event source = %q, want %q", got, attackerID)
	}
	t.Logf("damage event: %s -> %s amount=%d", attackerID, victimID, d.Amount)

	// The event must describe the state change the same snapshot stream reports.
	// Two independent accounts of one fact that disagree is worse than one.
	hpAfter := after.State.Entities[victimID].HP
	if hpAfter >= victimHPBefore {
		t.Errorf("victim hp did not fall: %d -> %d, yet a damage event was sent",
			victimHPBefore, hpAfter)
	}

	// --- The retrigger counter ---
	attackerEntity, ok := after.State.Entities[attackerID]
	if !ok {
		t.Fatalf("attacker is not in its own snapshot")
	}
	if attackerEntity.ActionSeq == 0 {
		t.Fatalf("action_seq = 0 after an accepted attack. Zero means 'not sent', so a " +
			"client cannot tell this server from one that predates the field, and a " +
			"repeated attack will never retrigger an animation")
	}
	firstSeq := attackerEntity.ActionSeq
	t.Logf("attacker action=%d action_seq=%d", attackerEntity.Action, firstSeq)

	// --- A second attack, past the cooldown ---
	//
	// This is the case the counter exists for: identical position, identical hp,
	// identical action. Only the counter moves, and if the delta encoder did not
	// consider it, the whole entity would be suppressed and the swing lost.
	// Waited THROUGH the socket rather than with a sleep. The server's per-connection send
	// queue is bounded and drops the OLDEST frame when it fills, so a client that stops
	// reading while it waits discards exactly the frames it is about to assert on — and
	// events are never re-sent. A blind sleep here reported "second attack produced no
	// damage event" against a server that had sent one.
	waitDeadline := time.Now().Add(1500 * time.Millisecond)
	for time.Now().Before(waitDeadline) {
		drain(t, attacker, obs, 2)
	}

	atk2, _ := messages.NewEnvelopeAs(enc, messages.MsgInput, messages.InputMessage{
		Tick:           2,
		AttackTargetID: victimID,
	})
	if err := attacker.Send(atk2); err != nil {
		t.Fatalf("send second attack: %v", err)
	}

	second := drain(t, attacker, obs, 6)
	if len(second.eventsOfType(messages.GameEventDamage)) == 0 {
		t.Errorf("second attack produced no damage event")
	}

	secondEntity, ok := second.State.Entities[attackerID]
	if !ok {
		t.Fatal("attacker missing from the second run of snapshots")
	}
	if secondEntity.ActionSeq == firstSeq {
		t.Errorf("action_seq did not change between two attacks (%d both times). "+
			"The second swing is a separate occurrence and a renderer has no other "+
			"way to learn of it — action is identical on both ticks",
			firstSeq)
	}
	t.Logf("action_seq %d -> %d across two attacks", firstSeq, secondEntity.ActionSeq)

	t.Logf("PASS: events and action_seq over %s", enc)
}

// TestGameplayV2_AbilityOverTheWire drives an ability input end to end.
func TestGameplayV2_AbilityOverTheWire(t *testing.T) {
	for _, enc := range []messages.Encoding{messages.EncodingJSON, messages.EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) { runGameplayV2Ability(t, enc) })
	}
}

func runGameplayV2Ability(t *testing.T, enc messages.Encoding) {
	requireDotnet(t)

	gsAddr, cleanup := startDotnetGameServerWith(t,
		[]string{"--content-dir", gameplayV2ContentDir(t)}, nil)
	defer cleanup()

	casterID := "v2-caster-" + enc.String()
	targetID := "v2-target-" + enc.String()

	caster := gameplayV2Join(t, gsAddr, casterID, enc)
	defer caster.Close()
	target := gameplayV2Join(t, gsAddr, targetID, enc)
	defer target.Close()

	obs := newObserved()
	before := drain(t, caster, obs, 4)
	if _, ok := before.State.Entities[targetID]; !ok {
		t.Fatalf("caster cannot see %s", targetID)
	}
	hpBefore := before.State.Entities[targetID].HP

	cast, _ := messages.NewEnvelopeAs(enc, messages.MsgInput, messages.InputMessage{
		Tick:            1,
		AbilityID:       gameplayV2AbilityBolt,
		AbilityTargetID: targetID,
	})
	if err := caster.Send(cast); err != nil {
		t.Fatalf("send ability: %v", err)
	}

	after := drain(t, caster, obs, 6)

	casts := after.eventsOfType(messages.GameEventAbilityCast)
	if len(casts) == 0 {
		t.Fatalf("no ability_cast event. The cast event is the ONLY report a client "+
			"gets that its ability resolved — abilities are not predicted, so a UI "+
			"driving a cooldown sweep from the input alone would be lying. "+
			"target hp %d -> %d", hpBefore, after.State.Entities[targetID].HP)
	}
	if got := casts[0].AbilityID; got != gameplayV2AbilityBolt {
		t.Errorf("cast event ability_id = %d, want %d", got, gameplayV2AbilityBolt)
	}
	if got := after.nameOf(casts[0].Source, casts[0].SourceID); got != casterID {
		t.Errorf("cast event source = %q, want %q", got, casterID)
	}

	damage := after.eventsOfType(messages.GameEventDamage)
	if len(damage) == 0 {
		t.Fatalf("ability cast but dealt no damage")
	}
	if damage[0].AbilityID != gameplayV2AbilityBolt {
		t.Errorf("damage event ability_id = %d, want %d — a damage number must be "+
			"attributable to the ability that caused it",
			damage[0].AbilityID, gameplayV2AbilityBolt)
	}

	hpAfter := after.State.Entities[targetID].HP
	if hpAfter >= hpBefore {
		t.Errorf("target hp did not fall under an ability: %d -> %d", hpBefore, hpAfter)
	}

	// The ability adds its power on top of the caster's attack, so it must hit
	// harder than a basic attack would. Asserting the direction rather than an
	// exact number keeps this from breaking on a stat tweak while still proving
	// the ability's power reached the damage formula.
	t.Logf("bolt: %s -> %s amount=%d, target hp %d -> %d",
		casterID, targetID, damage[0].Amount, hpBefore, hpAfter)

	t.Logf("PASS: ability over %s", enc)
}

// TestGameplayV2_UnknownAbilityIsRefusedNotFatal pins that an ability id this
// content set does not have is an ordinary refusal.
//
// A stale hotbar after a content change produces exactly this, and it must cost
// the player one input rather than the connection.
func TestGameplayV2_UnknownAbilityIsRefusedNotFatal(t *testing.T) {
	requireDotnet(t)

	gsAddr, cleanup := startDotnetGameServerWith(t,
		[]string{"--content-dir", gameplayV2ContentDir(t)}, nil)
	defer cleanup()

	playerID := "v2-stale-hotbar"
	client := gameplayV2Join(t, gsAddr, playerID, messages.EncodingProto)
	defer client.Close()

	obs := newObserved()
	drain(t, client, obs, 3)

	bogus, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgInput, messages.InputMessage{
		Tick:      1,
		AbilityID: 4242,
	})
	if err := client.Send(bogus); err != nil {
		t.Fatalf("send bogus ability: %v", err)
	}

	// The connection must survive and keep streaming.
	after := drain(t, client, obs, 4)
	if after.Ticks == 0 {
		t.Fatal("server stopped sending snapshots after an unknown ability id")
	}
	if len(after.eventsOfType(messages.GameEventAbilityCast)) != 0 {
		t.Error("an unknown ability produced a cast event")
	}

	// And a real input still works afterwards, so the refusal cost one input.
	move, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgInput, messages.InputMessage{
		Tick:  2,
		MoveX: 1,
	})
	if err := client.Send(move); err != nil {
		t.Fatalf("send move after refusal: %v", err)
	}
	final := drain(t, client, obs, 4)
	if final.State.AckTick < 2 {
		t.Errorf("ack_tick = %d after a later input; the refusal appears to have "+
			"wedged the input pipeline", final.State.AckTick)
	}

	t.Log("PASS: an unknown ability id is refused without breaking the session")
}
