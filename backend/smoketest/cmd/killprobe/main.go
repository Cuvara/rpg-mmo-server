// Command killprobe drives one REAL kill through a deployed stack, so the reward
// path can be observed end to end rather than inferred.
//
// Everything up to the join is the smoketest's flow: device-auth to Nakama, the
// gateway_token RPC, MsgAuth + MsgEnterWorld on the gateway, MsgJoinToken on the
// game server. What this adds is the part no existing harness had — it walks to a
// mob and hits it until it dies.
//
// The game server batches kills and flushes them to Nakama's reward_kills RPC
// every 3 seconds (KillRewardBatcher). So a kill here is what makes that call
// happen; this program's job is to cause one and say so.
package main

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"math"
	"net"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
)

const (
	attackRange = 3.0 // GameConstants.AttackRange
	arriveAt    = 2.0 // stop a little inside it, so jitter cannot push us out
)

func main() {
	nakamaURL := flag.String("nakama", "http://127.0.0.1:7001", "Nakama base URL")
	serverKey := flag.String("server-key", "", "Nakama server key (HTTP Basic user)")
	gatewayAddr := flag.String("gateway", "127.0.0.1:7000", "gateway host:port")
	mapID := flag.String("map", "map_01", "map id")
	budget := flag.Duration("budget", 90*time.Second, "give up after this long")
	flag.Parse()

	if *serverKey == "" {
		fail("-server-key is required")
	}

	deadline := time.Now().Add(*budget)
	hc := &http.Client{Timeout: 15 * time.Second}

	// ---- Nakama device auth ---------------------------------------------------
	suffix := make([]byte, 8)
	if _, err := rand.Read(suffix); err != nil {
		fail("random device id: %v", err)
	}
	deviceID := "killprobe-" + hex.EncodeToString(suffix)

	sessionToken := deviceAuth(hc, *nakamaURL, *serverKey, deviceID)
	say("device auth ok, device=%s", deviceID)

	// ---- gateway token --------------------------------------------------------
	jwt, userID := gatewayToken(hc, *nakamaURL, sessionToken)
	say("gateway token ok, user=%s", userID)

	// ---- gateway: auth + enter world -----------------------------------------
	serverAddr, joinToken := enterWorld(*gatewayAddr, jwt, *mapID)
	say("enter world ok, server=%s", serverAddr)

	// ---- game server: join ----------------------------------------------------
	conn, err := net.DialTimeout("tcp", dialable(serverAddr), 10*time.Second)
	if err != nil {
		fail("dial game server %s: %v", serverAddr, err)
	}
	defer conn.Close()

	var joinResp messages.JoinTokenResponse
	if err := roundTrip(conn, messages.MsgJoinToken,
		messages.JoinTokenRequest{Token: joinToken}, messages.MsgJoinTokenResp, &joinResp); err != nil {
		fail("join: %v", err)
	}
	if !joinResp.OK {
		fail("join rejected: %s", joinResp.Error)
	}
	say("joined as %s (tick rate %d)", joinResp.UserID, joinResp.TickRate)

	// ---- walk to a mob and hit it --------------------------------------------
	state := messages.NewSnapshotState()
	var (
		tick      uint64
		me        messages.EntitySnapshot
		targetID  string
		lastHP    = -1
		attacks   int
		haveMe    bool
		killedMsg string
	)

	_ = conn.SetReadDeadline(deadline)

	for time.Now().Before(deadline) {
		env, err := messages.Decode(conn)
		if err != nil {
			fail("read: %v", err)
		}
		// Answer the heartbeat. Without this the server drops the connection
		// mid-fight with "Heartbeat timeout", which arrives as a bare EOF and
		// looks like the game server crashed. The first run of this probe killed
		// its mob fast enough not to notice; the second did not.
		if env.Type == messages.MsgPing {
			if err := sendTyped(conn, messages.MsgPong, struct{}{}); err != nil {
				fail("pong: %v", err)
			}
			continue
		}
		if env.Type != messages.MsgSnapshot {
			continue
		}

		var snap messages.SnapshotMessage
		if err := env.UnmarshalPayload(&snap); err != nil {
			fail("decode snapshot: %v", err)
		}
		if err := state.Apply(snap); err != nil {
			fail("apply snapshot: %v", err)
		}
		tick = snap.Tick

		// Who am I? The join reply names the user; the player entity carries the
		// same id.
		if e, ok := state.Get(joinResp.UserID); ok {
			me, haveMe = e, true
		}
		if !haveMe {
			continue
		}

		// Did the thing we were hitting die? "Died" here is the server's word for
		// it: the entity leaves the world, so it stops being in the state we can
		// read. HP reaching 0 in the last snapshot that mentions it is the other
		// half of the evidence.
		if targetID != "" {
			if t, ok := state.Get(targetID); ok {
				lastHP = t.HP
			} else if attacks > 0 {
				killedMsg = fmt.Sprintf("mob %s is gone from the world after %d attacks (last HP seen %d)",
					targetID, attacks, lastHP)
				break
			}
		}

		if targetID == "" {
			targetID = nearestMob(state, me)
			if targetID != "" {
				t, _ := state.Get(targetID)
				say("target %s at (%.1f,%.1f) hp=%d/%d, me at (%.1f,%.1f)",
					targetID, t.X, t.Y, t.HP, t.MaxHP, me.X, me.Y)
			}
		}

		tick++
		in := messages.InputMessage{Tick: tick}

		if targetID != "" {
			if t, ok := state.Get(targetID); ok {
				dx, dy := float64(t.X-me.X), float64(t.Y-me.Y)
				dist := math.Hypot(dx, dy)
				if dist > arriveAt {
					// Normalised direction; the server validates magnitude.
					in.MoveX = float32(dx / dist)
					in.MoveY = float32(dy / dist)
				} else if dist <= attackRange {
					in.AttackTargetID = targetID
					attacks++
				}
			}
		}

		if err := send(conn, in); err != nil {
			fail("send input: %v", err)
		}
	}

	if killedMsg == "" {
		fail("no kill within the budget (attacks sent: %d, last target HP: %d)", attacks, lastHP)
	}

	say("KILL: %s", killedMsg)
	say("the game server should now flush reward_kills to Nakama within ~3s")
}

// ---- the flow's HTTP half ---------------------------------------------------

func deviceAuth(hc *http.Client, nakamaURL, serverKey, deviceID string) string {
	body, _ := json.Marshal(map[string]string{"id": deviceID})
	url := strings.TrimRight(nakamaURL, "/") + "/v2/account/authenticate/device?create=true"
	req, _ := http.NewRequest(http.MethodPost, url, bytes.NewReader(body))
	req.SetBasicAuth(serverKey, "")
	req.Header.Set("Content-Type", "application/json")

	resp, err := hc.Do(req)
	if err != nil {
		fail("POST %s: %v", url, err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode != http.StatusOK {
		fail("device auth: status %d: %s", resp.StatusCode, raw)
	}
	var out struct {
		Token string `json:"token"`
	}
	if err := json.Unmarshal(raw, &out); err != nil || out.Token == "" {
		fail("device auth: no session token in %s", raw)
	}
	return out.Token
}

func gatewayToken(hc *http.Client, nakamaURL, sessionToken string) (string, string) {
	url := strings.TrimRight(nakamaURL, "/") + "/v2/rpc/gateway_token"
	// The RPC payload is a JSON-encoded *string* containing the request JSON.
	req, _ := http.NewRequest(http.MethodPost, url, strings.NewReader(`"{}"`))
	req.Header.Set("Authorization", "Bearer "+sessionToken)
	req.Header.Set("Content-Type", "application/json")

	resp, err := hc.Do(req)
	if err != nil {
		fail("POST %s: %v", url, err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode != http.StatusOK {
		fail("gateway_token: status %d: %s", resp.StatusCode, raw)
	}
	// Nakama wraps an RPC reply as {"payload":"<json string>"}.
	var wrapper struct {
		Payload string `json:"payload"`
	}
	inner := raw
	if err := json.Unmarshal(raw, &wrapper); err == nil && wrapper.Payload != "" {
		inner = []byte(wrapper.Payload)
	}
	var out struct {
		Token  string `json:"token"`
		UserID string `json:"user_id"`
	}
	if err := json.Unmarshal(inner, &out); err != nil || out.Token == "" {
		fail("gateway_token: no token in %s", raw)
	}
	return out.Token, out.UserID
}

func enterWorld(gatewayAddr, jwt, mapID string) (string, string) {
	conn, err := net.DialTimeout("tcp", gatewayAddr, 10*time.Second)
	if err != nil {
		fail("dial gateway %s: %v", gatewayAddr, err)
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(30 * time.Second))

	var authResp messages.AuthResponse
	if err := roundTrip(conn, messages.MsgAuth, messages.AuthRequest{Token: jwt},
		messages.MsgAuthResp, &authResp); err != nil {
		fail("gateway auth: %v", err)
	}
	if !authResp.OK {
		fail("gateway auth rejected: %s", authResp.Error)
	}

	var enterResp messages.EnterWorldResponse
	if err := roundTrip(conn, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: mapID},
		messages.MsgEnterWorldResp, &enterResp); err != nil {
		fail("enter world: %v", err)
	}
	if enterResp.Error != "" {
		fail("enter world rejected: %s", enterResp.Error)
	}
	return enterResp.ServerAddr, enterResp.JoinToken
}

// ---- wire helpers -----------------------------------------------------------

func send(conn net.Conn, payload any) error {
	var t messages.MsgType
	switch payload.(type) {
	case messages.InputMessage:
		t = messages.MsgInput
	default:
		return fmt.Errorf("send: unknown payload %T", payload)
	}
	return sendTyped(conn, t, payload)
}

func sendTyped(conn net.Conn, t messages.MsgType, payload any) error {
	env, err := messages.NewEnvelopeAs(messages.EncodingJSON, t, payload)
	if err != nil {
		return err
	}
	data, err := messages.Encode(env)
	if err != nil {
		return err
	}
	_, err = conn.Write(data)
	return err
}

func roundTrip(conn net.Conn, reqType messages.MsgType, req any, wantType messages.MsgType, out any) error {
	env, err := messages.NewEnvelopeAs(messages.EncodingJSON, reqType, req)
	if err != nil {
		return err
	}
	data, err := messages.Encode(env)
	if err != nil {
		return err
	}
	if _, err := conn.Write(data); err != nil {
		return err
	}
	for i := 0; i < 32; i++ { // bounded skip of interleaved frames
		resp, err := messages.Decode(conn)
		if err != nil {
			return err
		}
		if resp.Type != wantType {
			continue
		}
		return resp.UnmarshalPayload(out)
	}
	return fmt.Errorf("no frame of type %d received", wantType)
}

func nearestMob(state *messages.SnapshotState, me messages.EntitySnapshot) string {
	best, bestDist := "", math.MaxFloat64
	for id, e := range state.Entities {
		if e.Type != "mob" || e.HP <= 0 {
			continue
		}
		d := math.Hypot(float64(e.X-me.X), float64(e.Y-me.Y))
		if d < bestDist {
			best, bestDist = id, d
		}
	}
	return best
}

// dialable strips a listen-style address ("0.0.0.0:9000" / ":9000") down to
// something a client can actually connect to on this box.
func dialable(addr string) string {
	if strings.HasPrefix(addr, ":") {
		return "127.0.0.1" + addr
	}
	if strings.HasPrefix(addr, "0.0.0.0:") {
		return "127.0.0.1" + strings.TrimPrefix(addr, "0.0.0.0")
	}
	return addr
}

func say(format string, args ...any)  { fmt.Printf("[killprobe] "+format+"\n", args...) }
func fail(format string, args ...any) { say("FAILED: "+format, args...); os.Exit(1) }
