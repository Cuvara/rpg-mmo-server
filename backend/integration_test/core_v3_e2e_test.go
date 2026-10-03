//go:build integration

package integration

import (
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
)

// Protocol 3 (ADR-28..31) against the real C# game server, over real sockets, in
// both encodings: the command channel answers one CommandResult per request, a
// protocol 2 client still joins and is served the protocol 2 shape, and the
// character id a gateway token carries comes back on the join.

// Opcodes from Shared.GameLogic's gameplay.proto. The Go side only moves bytes.
const (
	opcodeInventory = 1
	opcodePickup    = 2
)

// joinVersioned connects straight to the game server and joins advertising
// protocolVersion. A non-empty characterID becomes the join token's cid claim.
func joinVersioned(t *testing.T, gsAddr, playerID string, protocolVersion uint32,
	enc messages.Encoding, characterID string) (*MockClient, messages.JoinTokenResponse) {
	t.Helper()

	client, err := NewMockClient(gsAddr)
	if err != nil {
		t.Fatalf("connect %s: %v", playerID, err)
	}
	token, err := jwt.SignWithCharacter(playerID, dotnetServerID, characterID, dotnetJoinTokenSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("sign token for %s: %v", playerID, err)
	}
	joinEnv, _ := messages.NewEnvelopeAs(enc, messages.MsgJoinToken,
		messages.JoinTokenRequest{Token: token, ProtocolVersion: protocolVersion})
	if err := client.Send(joinEnv); err != nil {
		t.Fatalf("send join %s: %v", playerID, err)
	}
	respEnv, err := client.Receive()
	if err != nil {
		t.Fatalf("receive join resp %s: %v", playerID, err)
	}
	var resp messages.JoinTokenResponse
	if err := respEnv.UnmarshalPayload(&resp); err != nil {
		t.Fatalf("unmarshal join resp %s: %v", playerID, err)
	}
	if !resp.OK {
		t.Fatalf("join rejected for %s (protocol %d): %s", playerID, protocolVersion, resp.Error)
	}
	// The server echoes the NEGOTIATED version: the client's own inside the
	// window, because a protocol 2 client accepts only an exact echo.
	if want := messages.NegotiatedProtocolVersion(protocolVersion); resp.ProtocolVersion != want {
		t.Fatalf("join resp protocol_version = %d, want negotiated %d", resp.ProtocolVersion, want)
	}
	return client, resp
}

// sendCommand sends one CommandRequest and reads until its CommandResult,
// skipping snapshots, pings and pushes. It then keeps reading for `quiet` and
// fails if a second CommandResult for the same seq arrives.
func sendCommand(t *testing.T, c *MockClient, enc messages.Encoding, seq, opcode uint32, payload []byte,
	quiet time.Duration) messages.CommandResult {
	t.Helper()

	env, err := messages.NewEnvelopeAs(enc, messages.MsgCommand,
		messages.CommandRequest{Seq: seq, Opcode: opcode, Payload: payload})
	if err != nil {
		t.Fatalf("build command: %v", err)
	}
	if err := c.Send(env); err != nil {
		t.Fatalf("send command: %v", err)
	}

	var result *messages.CommandResult
	deadline := time.Now().Add(10 * time.Second)
	for result == nil && time.Now().Before(deadline) {
		got, err := c.Receive()
		if err != nil {
			t.Fatalf("waiting for CommandResult seq %d: %v", seq, err)
		}
		if got.Type != messages.MsgCommandResult {
			continue
		}
		if got.Enc != enc {
			t.Fatalf("CommandResult encoding = %v, want %v", got.Enc, enc)
		}
		var r messages.CommandResult
		if err := got.UnmarshalPayload(&r); err != nil {
			t.Fatalf("unmarshal CommandResult: %v", err)
		}
		if r.Seq != seq {
			t.Fatalf("CommandResult seq = %d, want %d", r.Seq, seq)
		}
		result = &r
	}
	if result == nil {
		t.Fatalf("no CommandResult for seq %d within 10s", seq)
	}

	until := time.Now().Add(quiet)
	for time.Now().Before(until) {
		got, err := c.Receive()
		if err != nil {
			break
		}
		if got.Type == messages.MsgCommandResult {
			var r messages.CommandResult
			_ = got.UnmarshalPayload(&r)
			if r.Seq == seq {
				t.Fatalf("a second CommandResult for seq %d: each request gets exactly one", seq)
			}
		}
	}
	return *result
}

// TestCoreV3_CommandInventory: a protocol 3 client sends InventoryRequest
// (opcode 1) and gets exactly one CommandResult, ok, carrying an InventoryView
// (empty for a new character, so a zero-length payload). A protocol 2 client on
// the same server is answered unknown_opcode: the channel does not exist in its
// protocol, but it is answered rather than dropped.
func TestCoreV3_CommandInventory(t *testing.T) {
	for _, enc := range []messages.Encoding{messages.EncodingJSON, messages.EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) {
			requireDotnet(t)
			gsAddr, cleanup := startDotnetGameServer(t)
			defer cleanup()

			v3, _ := joinVersioned(t, gsAddr, "v3-cmd-"+enc.String(), 3, enc, "")
			defer v3.Close()
			res := sendCommand(t, v3, enc, 1, opcodeInventory, nil, time.Second)
			if !res.OK {
				t.Fatalf("InventoryRequest refused: %q", res.Error)
			}
			if len(res.Payload) != 0 {
				t.Errorf("a new character's InventoryView should encode to zero bytes, got %d", len(res.Payload))
			}

			// An unknown opcode is answered, by name.
			unknown := sendCommand(t, v3, enc, 2, 99, nil, 0)
			if unknown.OK || unknown.Error != "unknown_opcode" {
				t.Errorf("opcode 99: ok=%v error=%q, want unknown_opcode", unknown.OK, unknown.Error)
			}
			// A pick-up of nothing is a named refusal too, not a dropped request.
			missing := sendCommand(t, v3, enc, 3, opcodePickup, []byte{0x0a, 0x04, 'n', 'o', 'p', 'e'}, 0)
			if missing.OK || missing.Error != "not_found" {
				t.Errorf("pickup of a missing entity: ok=%v error=%q, want not_found", missing.OK, missing.Error)
			}

			v2, _ := joinVersioned(t, gsAddr, "v2-cmd-"+enc.String(), 2, enc, "")
			defer v2.Close()
			refused := sendCommand(t, v2, enc, 1, opcodeInventory, nil, 0)
			if refused.OK || refused.Error != "unknown_opcode" {
				t.Errorf("protocol 2 command: ok=%v error=%q, want unknown_opcode", refused.OK, refused.Error)
			}
		})
	}
}

// TestCoreV3_ProtocolTwoPeerGetsTheProtocolTwoShape: a protocol 2 client and a
// protocol 3 client stand in one world and the protocol 3 one jumps. The
// protocol 3 client receives its height and the content stat block; the
// protocol 2 client, watching the same player, receives no protocol 3 field and
// no protocol 3 mask bit at all.
func TestCoreV3_ProtocolTwoPeerGetsTheProtocolTwoShape(t *testing.T) {
	for _, enc := range []messages.Encoding{messages.EncodingJSON, messages.EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) {
			requireDotnet(t)
			gsAddr, cleanup := startDotnetGameServer(t)
			defer cleanup()

			jumperID := "v3-jumper-" + enc.String()
			watcherID := "v2-watcher-" + enc.String()
			jumper, _ := joinVersioned(t, gsAddr, jumperID, 3, enc, "")
			defer jumper.Close()
			watcher, _ := joinVersioned(t, gsAddr, watcherID, 2, enc, "")
			defer watcher.Close()

			type observed struct {
				sawZ, sawStats, sawJumper bool
				v3Violation               string
				snaps                     int
			}
			read := func(c *MockClient, until time.Time, out *observed, isV2 bool) {
				for time.Now().Before(until) {
					env, err := c.Receive()
					if err != nil {
						return
					}
					if env.Type != messages.MsgSnapshot {
						continue
					}
					var s messages.SnapshotMessage
					if err := env.UnmarshalPayload(&s); err != nil {
						t.Errorf("unmarshal snapshot: %v", err)
						return
					}
					out.snaps++
					for _, e := range s.Entities {
						if e.ID == jumperID {
							out.sawJumper = true
						}
						if e.Z > 0 {
							out.sawZ = true
						}
						if len(e.Stats) > 0 {
							out.sawStats = true
						}
						if isV2 && out.v3Violation == "" {
							switch {
							case e.Z != 0 || e.VelX != 0 || e.VelY != 0 || e.VelZ != 0:
								out.v3Violation = "z/velocity"
							case e.Owner != 0 || e.OwnerID != "" || e.SpawnSeq != 0:
								out.v3Violation = "owner/spawn_seq"
							case len(e.Stats) > 0 || len(e.StatsRemoved) > 0 || len(e.Statuses) > 0 || len(e.StatusesRemoved) > 0:
								out.v3Violation = "stats/statuses"
							case e.ChangedFields&^uint32(0x01FF) != 0:
								out.v3Violation = "a protocol 3 mask bit"
							case e.Type == "projectile" || e.Type == "item":
								out.v3Violation = "a protocol 3 entity kind"
							}
						}
					}
					for _, ev := range s.Events {
						if isV2 && out.v3Violation == "" && (ev.Type > 6 || ev.EffectID != 0) {
							out.v3Violation = "a protocol 3 event"
						}
					}
				}
			}

			// Settle, then jump (inputs are sent from this goroutine between reads).
			var v3obs, v2obs observed
			read(jumper, time.Now().Add(300*time.Millisecond), &v3obs, false)
			for tick := uint64(1); tick <= 3; tick++ {
				in, _ := messages.NewEnvelopeAs(enc, messages.MsgInput,
					messages.InputMessage{Tick: tick, Jump: tick == 1})
				if err := jumper.Send(in); err != nil {
					t.Fatalf("send input: %v", err)
				}
			}
			done := make(chan struct{})
			go func() {
				defer close(done)
				read(watcher, time.Now().Add(1500*time.Millisecond), &v2obs, true)
			}()
			read(jumper, time.Now().Add(1500*time.Millisecond), &v3obs, false)
			<-done

			if !v3obs.sawStats {
				t.Errorf("protocol 3 client never received a stat block (%d snapshots)", v3obs.snaps)
			}
			if !v3obs.sawZ {
				t.Errorf("protocol 3 client never saw a height after jumping (%d snapshots)", v3obs.snaps)
			}
			if !v2obs.sawJumper {
				t.Fatalf("protocol 2 watcher never saw the jumper (%d snapshots): the shape check proves nothing", v2obs.snaps)
			}
			if v2obs.v3Violation != "" {
				t.Errorf("protocol 2 client received %s", v2obs.v3Violation)
			}
			t.Logf("v3 snapshots=%d (z=%v stats=%v), v2 snapshots=%d clean", v3obs.snaps, v3obs.sawZ, v3obs.sawStats, v2obs.snaps)
		})
	}
}

// TestCoreV3_CharacterIDEchoedThroughTheGateway: Nakama mints a gateway token
// with a cid claim; the gateway copies it into the join token; the game server
// echoes it in JoinTokenResponse.character_id. Without cid the field is empty.
func TestCoreV3_CharacterIDEchoedThroughTheGateway(t *testing.T) {
	for _, enc := range []messages.Encoding{messages.EncodingJSON, messages.EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) {
			requireDotnet(t)
			gsAddr, gsCleanup := startDotnetGameServer(t)
			defer gsCleanup()
			gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
			defer gwCleanup()

			for _, cid := range []string{"char-slot-2", ""} {
				userID := "v3-cid-" + enc.String() + "-" + cid
				gw, err := NewMockClient(gwAddr)
				if err != nil {
					t.Fatalf("connect gateway: %v", err)
				}
				token, _ := jwt.SignWithCharacter(userID, "", cid, dotnetJWTSecret, 5*time.Minute)
				auth, _ := messages.NewEnvelopeAs(enc, messages.MsgAuth,
					messages.AuthRequest{Token: token, ProtocolVersion: messages.WireProtocolVersion})
				if err := gw.Send(auth); err != nil {
					t.Fatalf("send auth: %v", err)
				}
				authEnv, err := gw.Receive()
				if err != nil {
					t.Fatalf("auth resp: %v", err)
				}
				var authResp messages.AuthResponse
				_ = authEnv.UnmarshalPayload(&authResp)
				if !authResp.OK {
					t.Fatalf("auth refused: %s", authResp.Error)
				}

				enter, _ := messages.NewEnvelopeAs(enc, messages.MsgEnterWorld,
					messages.EnterWorldRequest{MapID: dotnetMapID, CharacterID: cid})
				if err := gw.Send(enter); err != nil {
					t.Fatalf("send enter world: %v", err)
				}
				enterEnv, err := gw.Receive()
				if err != nil {
					t.Fatalf("enter world resp: %v", err)
				}
				var enterResp messages.EnterWorldResponse
				_ = enterEnv.UnmarshalPayload(&enterResp)
				gw.Close()
				if enterResp.Error != "" || enterResp.JoinToken == "" {
					t.Fatalf("enter world failed: %q", enterResp.Error)
				}

				gs, err := NewMockClient(enterResp.ServerAddr)
				if err != nil {
					t.Fatalf("connect game server: %v", err)
				}
				join, _ := messages.NewEnvelopeAs(enc, messages.MsgJoinToken,
					messages.JoinTokenRequest{Token: enterResp.JoinToken, ProtocolVersion: messages.WireProtocolVersion})
				if err := gs.Send(join); err != nil {
					t.Fatalf("send join: %v", err)
				}
				joinEnv, err := gs.Receive()
				if err != nil {
					t.Fatalf("join resp: %v", err)
				}
				var joinResp messages.JoinTokenResponse
				_ = joinEnv.UnmarshalPayload(&joinResp)
				gs.Close()
				if !joinResp.OK {
					t.Fatalf("join refused: %s", joinResp.Error)
				}
				if joinResp.CharacterID != cid {
					t.Errorf("character_id = %q, want %q (cid carried gateway token -> join token -> echo)", joinResp.CharacterID, cid)
				}
			}
		})
	}
}
