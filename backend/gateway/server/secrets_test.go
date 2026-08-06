package server

import (
	"net"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/transfer"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
)

// TestAuthAcceptsRotatedSecret is the operator-visible payoff of the keyring:
// deploying JWT_SECRET="new,old" must not log out anyone holding a token signed
// with "old".
func TestAuthAcceptsRotatedSecret(t *testing.T) {
	const oldSecret, newSecret = "old-jwt-secret", "new-jwt-secret"

	tests := []struct {
		name       string
		signWith   string
		gwSecret   string
		wantAuthOK bool
	}{
		{name: "current secret", signWith: newSecret, gwSecret: newSecret + "," + oldSecret, wantAuthOK: true},
		{name: "previous secret during rotation", signWith: oldSecret, gwSecret: newSecret + "," + oldSecret, wantAuthOK: true},
		{name: "previous secret after rotation completes", signWith: oldSecret, gwSecret: newSecret, wantAuthOK: false},
		{name: "unrelated secret", signWith: "attacker", gwSecret: newSecret + "," + oldSecret, wantAuthOK: false},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			gw := startGatewayWithSecretAndOptions(t, tt.gwSecret)

			conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
			if err != nil {
				t.Fatalf("dial: %v", err)
			}
			defer conn.Close()

			token, err := jwt.Sign("player-rot", tt.signWith, time.Hour)
			if err != nil {
				t.Fatalf("sign: %v", err)
			}
			send(t, conn, messages.MsgAuth, messages.AuthRequest{Token: token})

			resp := readAuthResp(t, conn)
			if resp.OK != tt.wantAuthOK {
				t.Errorf("auth OK = %v, want %v (error: %q)", resp.OK, tt.wantAuthOK, resp.Error)
			}
		})
	}
}

// TestJoinTokenUsesSeparateSecret proves the split: the join token handed to
// the client is signed with JOIN_TOKEN_SECRET, NOT with JWT_SECRET. A game
// server that only knows the join secret must accept it, and a holder of the
// auth secret alone must not be able to verify (or therefore forge) it.
func TestJoinTokenUsesSeparateSecret(t *testing.T) {
	const authSecret, joinSecret = "auth-secret", "join-secret"

	gw2 := startGatewayWithSecretAndOptions(t, authSecret, WithJoinTokenSecret(joinSecret))

	conn, err := net.DialTimeout("tcp", gw2.Addr(), 2*time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	token, err := jwt.Sign("player-join", authSecret, time.Hour)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	send(t, conn, messages.MsgAuth, messages.AuthRequest{Token: token})
	if resp := readAuthResp(t, conn); !resp.OK {
		t.Fatalf("auth failed: %s", resp.Error)
	}

	send(t, conn, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_forest"})
	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	env, err := messages.Decode(conn)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}
	var ew messages.EnterWorldResponse
	if err := messages.UnmarshalPayload(env.Payload, &ew); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if ew.Error != "" {
		t.Fatalf("enter world failed: %s", ew.Error)
	}
	if ew.JoinToken == "" {
		t.Fatal("no join token issued")
	}

	// The game server (holding JOIN_TOKEN_SECRET) accepts it...
	userID, serverID, err := transfer.ValidateJoinToken(ew.JoinToken, joinSecret)
	if err != nil {
		t.Fatalf("join token must verify under JOIN_TOKEN_SECRET: %v", err)
	}
	if userID != "player-join" || serverID != "srv1" {
		t.Errorf("claims = (%q, %q), want (player-join, srv1)", userID, serverID)
	}

	// ...and the auth secret alone is useless against it. This is the whole
	// point of the split: compromising one hop does not compromise the other.
	if _, _, err := transfer.ValidateJoinToken(ew.JoinToken, authSecret); err == nil {
		t.Error("join token must NOT verify under JWT_SECRET once the secrets are split")
	}
}

// TestJoinTokenDefaultsToAuthSecret pins the backward-compatible fallback:
// with JOIN_TOKEN_SECRET unset the gateway keeps signing join tokens with
// JWT_SECRET, so an existing deployment does not break on upgrade.
func TestJoinTokenDefaultsToAuthSecret(t *testing.T) {
	const authSecret = "only-secret"
	gw := startGatewayWithSecretAndOptions(t, authSecret)

	conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	token, err := jwt.Sign("player-compat", authSecret, time.Hour)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	send(t, conn, messages.MsgAuth, messages.AuthRequest{Token: token})
	if resp := readAuthResp(t, conn); !resp.OK {
		t.Fatalf("auth failed: %s", resp.Error)
	}
	send(t, conn, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_forest"})

	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	env, err := messages.Decode(conn)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}
	var ew messages.EnterWorldResponse
	if err := messages.UnmarshalPayload(env.Payload, &ew); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if _, _, err := transfer.ValidateJoinToken(ew.JoinToken, authSecret); err != nil {
		t.Errorf("without JOIN_TOKEN_SECRET the join token must still verify under JWT_SECRET: %v", err)
	}
}
