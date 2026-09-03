//go:build integration

package integration

import (
	"context"
	"encoding/json"
	"log/slog"
	"math"
	"os"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"

	gwevents "github.com/duycuong/rpg-mmo/gateway/events"
)

// deathEventPayload mirrors the C# DeathPayload record (EventPublisher.cs) —
// the cross-language contract this test exists to pin.
type deathEventPayload struct {
	VictimID   string `json:"victim_id"`
	VictimType string `json:"victim_type"`
	KillerID   string `json:"killer_id"`
	MapID      string `json:"map_id"`
}

// TestDotnetInterop_DeathEventReachesRedisAndRelay is the live end-to-end proof
// for the Redis-backed IEventStream: a real client joins the real C# game
// server through the real gateway handshake, walks up to a scaffolding enemy
// and attacks it until it dies, and the death event must come out the other
// side of Redis through the SAME consumer path the production gateway uses
// (redisstore.EventStream with consumer-group ACK, wrapped in
// gateway/events.Relay reading the default stream).
//
// This covers what the unit and wire-contract tests cannot: the full chain
// world tick -> OnEntityDeath -> EventPublisher queue -> RedisEventStream XADD
// -> events:game -> XREADGROUP -> Relay -> Sink, across both languages, with
// nothing mocked but the Redis process itself (miniredis, in-process — same
// rationale as the self-registration flow test: it keeps this in the default
// integration suite instead of behind a docker gate).
func TestDotnetInterop_DeathEventReachesRedisAndRelay(t *testing.T) {
	mr := miniredis.RunT(t)
	t.Logf("miniredis listening on %s", mr.Addr())

	// Game server with the Redis event stream enabled — the same REDIS_ADDR
	// wiring production uses (Program.cs selects RedisEventStream when set).
	gsAddr, gsCleanup := startDotnetGameServerWith(t, nil, []string{
		"REDIS_ADDR=" + mr.Addr(),
	})
	defer gsCleanup()

	gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
	defer gwCleanup()

	// The production consumer path: redisstore EventStream + gateway Relay on
	// the default stream name ("" -> constants.GameEventStream -> events:game).
	logger := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelDebug}))
	es := redisstore.NewEventStream(mr.Addr(), "", "e2e-relay-group", "e2e-consumer-1")
	defer es.Close()

	deaths := make(chan storage.Event, 16)
	relay := gwevents.NewRelay(es, "", gwevents.SinkFunc(func(ev storage.Event) {
		select {
		case deaths <- ev:
		default:
		}
	}), logger)

	relayCtx, relayCancel := context.WithCancel(context.Background())
	defer relayCancel()
	if err := relay.Start(relayCtx); err != nil {
		t.Fatalf("start relay: %v", err)
	}
	defer func() { _ = relay.Stop() }()

	// --- Full client handshake, Protobuf encoding (the production default) ---
	enc := messages.EncodingProto
	userID := "e2e-death-hunter"

	gwClient, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("connect to gateway: %v", err)
	}
	token, err := jwt.Sign(userID, dotnetJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}
	authEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgAuth, messages.AuthRequest{Token: token})
	if err := gwClient.Send(authEnv); err != nil {
		t.Fatalf("send auth: %v", err)
	}
	if _, err := gwClient.Receive(); err != nil {
		t.Fatalf("auth response: %v", err)
	}
	enterEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: dotnetMapID})
	if err := gwClient.Send(enterEnv); err != nil {
		t.Fatalf("send enter world: %v", err)
	}
	enterRespEnv, err := gwClient.Receive()
	if err != nil {
		t.Fatalf("enter world response: %v", err)
	}
	var enterResp messages.EnterWorldResponse
	if err := enterRespEnv.UnmarshalPayload(&enterResp); err != nil {
		t.Fatalf("unmarshal enter world: %v", err)
	}
	gwClient.Close()

	gsClient, err := NewMockClient(enterResp.ServerAddr)
	if err != nil {
		t.Fatalf("connect to game server: %v", err)
	}
	defer gsClient.Close()
	joinEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgJoinToken, messages.JoinTokenRequest{Token: enterResp.JoinToken})
	if err := gsClient.Send(joinEnv); err != nil {
		t.Fatalf("send join: %v", err)
	}
	joinRespEnv, err := gsClient.Receive()
	if err != nil {
		t.Fatalf("join response: %v", err)
	}
	var joinResp messages.JoinTokenResponse
	if err := joinRespEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("unmarshal join: %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("join rejected: %s", joinResp.Error)
	}
	t.Log("joined game server; hunting a mob")

	// --- Hunt: merge snapshots, walk toward the nearest mob, attack in range.
	//
	// Scaffolding enemies spawn in waves and walk toward the origin; the player
	// spawns near the origin, so convergence is quick. AttackRange is 3.0 and
	// mobs die in two hits (#239), but cooldowns and spawn timing make the
	// exact tick count irrelevant — the loop just keeps pressing the attack
	// until the relay hands us a death or the deadline passes.
	state := messages.NewSnapshotState()
	var tick uint64 = 1
	deadline := time.Now().Add(90 * time.Second)

	for time.Now().Before(deadline) {
		// Drain one snapshot (5s receive deadline inside MockClient).
		env, err := gsClient.Receive()
		if err == nil && env.Type == messages.MsgSnapshot {
			var snap messages.SnapshotMessage
			if err := env.UnmarshalPayload(&snap); err != nil {
				t.Fatalf("unmarshal snapshot: %v", err)
			}
			if err := state.Apply(snap); err != nil {
				t.Fatalf("apply snapshot: %v", err)
			}
		}

		var me, mob *messages.EntitySnapshot
		for id := range state.Entities {
			e := state.Entities[id]
			switch {
			case e.ID == userID:
				me = &e
			case e.Type == "mob" && e.HP > 0:
				if mob == nil {
					mob = &e
				} else if hypot(e.X-state.Entities[userID].X, e.Y-state.Entities[userID].Y) <
					hypot(mob.X-state.Entities[userID].X, mob.Y-state.Entities[userID].Y) {
					mob = &e
				}
			}
		}

		input := messages.InputMessage{Tick: tick}
		if me != nil && mob != nil {
			dx, dy := mob.X-me.X, mob.Y-me.Y
			if hypot(dx, dy) > 2.5 {
				n := hypot(dx, dy)
				input.MoveX, input.MoveY = dx/n, dy/n
			} else {
				input.AttackTargetID = mob.ID
			}
		} else {
			// No mob visible yet (spawn wave pending) — nudge toward origin so
			// the AOI window overlaps the mobs' convergence point.
			input.MoveX, input.MoveY = -sign(mePos(me).x), -sign(mePos(me).y)
		}
		inputEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgInput, input)
		if err := gsClient.Send(inputEnv); err != nil {
			t.Fatalf("send input: %v", err)
		}
		tick++

		select {
		case ev := <-deaths:
			// --- The assertion this whole file exists for. ---
			if ev.Type != "entity_killed" {
				t.Fatalf("event type = %q, want entity_killed", ev.Type)
			}
			var p deathEventPayload
			if err := json.Unmarshal(ev.Payload, &p); err != nil {
				t.Fatalf("death payload is not the documented JSON: %v (raw: %s)", err, ev.Payload)
			}
			if p.VictimType != "mob" {
				t.Errorf("victim_type = %q, want mob", p.VictimType)
			}
			if p.KillerID != userID {
				t.Errorf("killer_id = %q, want %q", p.KillerID, userID)
			}
			if p.MapID != dotnetMapID {
				t.Errorf("map_id = %q, want %q", p.MapID, dotnetMapID)
			}
			t.Logf("PASS: death event consumed via events:game consumer group: victim=%s killer=%s map=%s",
				p.VictimID, p.KillerID, p.MapID)
			return
		default:
		}
	}

	t.Fatalf("no death event arrived within 90s (entities seen: %d)", state.Len())
}

func hypot(x, y float32) float32 { return float32(math.Hypot(float64(x), float64(y))) }

func sign(v float32) float32 {
	switch {
	case v > 0:
		return 1
	case v < 0:
		return -1
	default:
		return 0
	}
}

type vec2f struct{ x, y float32 }

func mePos(me *messages.EntitySnapshot) vec2f {
	if me == nil {
		return vec2f{}
	}
	return vec2f{me.X, me.Y}
}
