// Command dungeonprobe proves ADR-26 end to end against a running deployment:
// a real party, created through Nakama's RPCs, entering one dungeon instance
// through the real gateway.
//
// It exists because the unit tests on both sides can only prove their own
// halves. The gateway's tests drive a fake party authority and a fake
// allocator; the Nakama module's tests drive an in-process storage double.
// Neither can answer the one question that matters here: do two players who
// joined the same party through Nakama land on the SAME pod when they ask the
// gateway for a dungeon?
//
// The verdict is the ADDRESS. Not "both calls succeeded" -- two successful
// calls that returned two different pods is precisely the failure this whole
// design exists to prevent (ADR-26 decision 2), and it looks like success from
// every angle except the one this probe checks.
package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
)

func main() {
	nakamaURL := flag.String("nakama", "http://127.0.0.1:7001", "Nakama base URL")
	serverKey := flag.String("server-key", "", "Nakama server key (HTTP Basic user)")
	gatewayAddr := flag.String("gateway", "127.0.0.1:7000", "gateway host:port")
	content := flag.String("content", "dungeon_01", "dungeon content id (sent as map_id)")
	flag.Parse()

	hc := &http.Client{Timeout: 15 * time.Second}
	suffix := fmt.Sprintf("%d", time.Now().UnixNano())

	// Three accounts: two party members and one outsider. The outsider is not
	// decoration -- without it, a gateway that skipped the membership check
	// entirely would pass every other assertion here.
	leader := newPlayer(hc, *nakamaURL, *serverKey, "dungeonprobe-leader-"+suffix)
	member := newPlayer(hc, *nakamaURL, *serverKey, "dungeonprobe-member-"+suffix)
	outsider := newPlayer(hc, *nakamaURL, *serverKey, "dungeonprobe-outsider-"+suffix)
	say("authenticated leader=%s member=%s outsider=%s", short(leader.userID), short(member.userID), short(outsider.userID))

	// --- C2: a real party through Nakama's RPCs -----------------------------

	partyID := rpcString(hc, *nakamaURL, leader.session, "party_create", `{}`, "party_id")
	say("party created: %s (leader)", partyID)

	joined := rpcString(hc, *nakamaURL, member.session, "party_join",
		fmt.Sprintf(`{"party_id":%q}`, partyID), "party_id")
	if joined != partyID {
		fail("member joined %q, wanted %q", joined, partyID)
	}
	say("member joined the same party")

	// --- The negative control, FIRST ----------------------------------------
	//
	// Before the members enter, not after, for two reasons. It is the security
	// assertion, and an assertion that runs only when everything else already
	// worked is the one that gets skipped on the day it would have fired. And
	// it needs no allocated pod, so it still runs -- and still means something
	// -- against a fleet scaled to zero.
	//
	// The refusal must name membership. "All servers busy" here would be a
	// pass-looking result from a gateway that never checked at all.

	outAddr, outErr := enterWorldAllowError(*gatewayAddr, outsider.jwt, *content, partyID)
	if outErr == "" {
		fail("SECURITY: a non-member was admitted to %s by naming someone else's party", outAddr)
	}
	if !strings.Contains(strings.ToLower(outErr), "party") {
		fail("SECURITY: the outsider was refused with %q, which does not mention the party -- "+
			"the membership check may not have run at all", outErr)
	}
	say("outsider refused, and the reason names the party: %q", outErr)

	// --- C1: both members enter, and must land on ONE instance --------------

	leaderAddr, _ := enterWorld(*gatewayAddr, leader.jwt, *content, partyID)
	say("leader  -> %s", leaderAddr)

	memberAddr, _ := enterWorld(*gatewayAddr, member.jwt, *content, partyID)
	say("member  -> %s", memberAddr)

	if leaderAddr == "" || memberAddr == "" {
		fail("an empty server address means the gateway refused the entry")
	}
	if leaderAddr != memberAddr {
		fail("SPLIT PARTY: leader on %s, member on %s -- the instance is not party-keyed",
			leaderAddr, memberAddr)
	}

	// A map entry must still work, unchanged, on the same gateway: the dungeon
	// branch shares handleEnterWorld with it, and breaking maps to add dungeons
	// would be a poor trade.
	mapAddr, _ := enterWorld(*gatewayAddr, outsider.jwt, "map_01", "")
	say("map entry still works: %s", mapAddr)

	say("DUNGEON OK: one party, one instance at %s; outsider refused; maps unaffected", leaderAddr)
}

// --- helpers ---------------------------------------------------------------

type player struct {
	session string
	jwt     string
	userID  string
}

func newPlayer(hc *http.Client, nakamaURL, serverKey, deviceID string) player {
	session := deviceAuth(hc, nakamaURL, serverKey, deviceID)
	jwt, userID := gatewayToken(hc, nakamaURL, session)
	return player{session: session, jwt: jwt, userID: userID}
}

func short(id string) string {
	if len(id) > 8 {
		return id[:8]
	}
	return id
}

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
	inner := rpcRaw(hc, nakamaURL, sessionToken, "gateway_token", `{}`)
	var out struct {
		Token  string `json:"token"`
		UserID string `json:"user_id"`
	}
	if err := json.Unmarshal(inner, &out); err != nil || out.Token == "" {
		fail("gateway_token: no token in %s", inner)
	}
	return out.Token, out.UserID
}

// rpcRaw calls a Nakama RPC as a logged-in client and returns the decoded
// payload. Nakama double-encodes in both directions: the request body is a JSON
// string containing JSON, and the reply is {"payload":"<json string>"}.
func rpcRaw(hc *http.Client, nakamaURL, sessionToken, rpc, payload string) []byte {
	body, _ := json.Marshal(payload)
	url := strings.TrimRight(nakamaURL, "/") + "/v2/rpc/" + rpc
	req, _ := http.NewRequest(http.MethodPost, url, bytes.NewReader(body))
	req.Header.Set("Authorization", "Bearer "+sessionToken)
	req.Header.Set("Content-Type", "application/json")

	resp, err := hc.Do(req)
	if err != nil {
		fail("POST %s: %v", url, err)
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode != http.StatusOK {
		fail("%s: status %d: %s", rpc, resp.StatusCode, raw)
	}
	var wrapper struct {
		Payload string `json:"payload"`
	}
	if err := json.Unmarshal(raw, &wrapper); err == nil && wrapper.Payload != "" {
		return []byte(wrapper.Payload)
	}
	return raw
}

func rpcString(hc *http.Client, nakamaURL, sessionToken, rpc, payload, field string) string {
	inner := rpcRaw(hc, nakamaURL, sessionToken, rpc, payload)
	var out map[string]any
	if err := json.Unmarshal(inner, &out); err != nil {
		fail("%s: undecodable reply %s", rpc, inner)
	}
	v, ok := out[field].(string)
	if !ok || v == "" {
		fail("%s: no %q in %s", rpc, field, inner)
	}
	return v
}

func enterWorld(gatewayAddr, jwt, mapID, partyID string) (string, string) {
	addr, token, errMsg := enterWorldRaw(gatewayAddr, jwt, mapID, partyID)
	if errMsg != "" {
		fail("enter world rejected: %s", errMsg)
	}
	return addr, token
}

func enterWorldAllowError(gatewayAddr, jwt, mapID, partyID string) (string, string) {
	addr, _, errMsg := enterWorldRaw(gatewayAddr, jwt, mapID, partyID)
	return addr, errMsg
}

func enterWorldRaw(gatewayAddr, jwt, mapID, partyID string) (string, string, string) {
	conn, err := net.DialTimeout("tcp", gatewayAddr, 10*time.Second)
	if err != nil {
		fail("dial gateway %s: %v", gatewayAddr, err)
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(45 * time.Second))

	var authResp messages.AuthResponse
	if err := roundTrip(conn, messages.MsgAuth, messages.AuthRequest{Token: jwt},
		messages.MsgAuthResp, &authResp); err != nil {
		fail("gateway auth: %v", err)
	}
	if !authResp.OK {
		fail("gateway auth rejected: %s", authResp.Error)
	}

	var enterResp messages.EnterWorldResponse
	if err := roundTrip(conn, messages.MsgEnterWorld,
		messages.EnterWorldRequest{MapID: mapID, PartyID: partyID},
		messages.MsgEnterWorldResp, &enterResp); err != nil {
		fail("enter world: %v", err)
	}
	return enterResp.ServerAddr, enterResp.JoinToken, enterResp.Error
}

func say(format string, args ...any) {
	fmt.Printf("[dungeonprobe] "+format+"\n", args...)
}

func fail(format string, args ...any) {
	say("FAILED: "+format, args...)
	os.Exit(1)
}

// roundTrip writes one request and reads until the wanted reply type, skipping
// interleaved frames (the gateway may send a heartbeat mid-exchange). Bounded,
// because an unbounded loop here would hang on a peer that never answers.
func roundTrip(conn net.Conn, reqType messages.MsgType, req any, wantType messages.MsgType, out any) error {
	env, err := messages.NewEnvelopeAs(messages.EncodingProto, reqType, req)
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
	for i := 0; i < 32; i++ {
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
