package server

import (
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
)

// TestGateway_EnterWorldCharacter pins ADR-31 on the gateway: the join token's
// `cid` is copied from the gateway token, never taken from the request, and a
// request naming a different character than the token is refused with the
// named error character_mismatch and no join token.
func TestGateway_EnterWorldCharacter(t *testing.T) {
	tests := []struct {
		name          string
		tokenCID      string // cid claim in the gateway (auth) token
		requestCID    string // EnterWorldRequest.CharacterID
		wantErr       string
		wantJoinCID   string
		wantJoinToken bool
	}{
		{name: "protocol 2 client, default character", tokenCID: "", requestCID: "", wantJoinCID: "", wantJoinToken: true},
		{name: "token names character, request silent", tokenCID: "char-a", requestCID: "", wantJoinCID: "char-a", wantJoinToken: true},
		{name: "token and request agree", tokenCID: "char-a", requestCID: "char-a", wantJoinCID: "char-a", wantJoinToken: true},
		{name: "request names another character", tokenCID: "char-a", requestCID: "char-b", wantErr: msgCharacterMismatch},
		{name: "request names character, token is default", tokenCID: "", requestCID: "char-b", wantErr: msgCharacterMismatch},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			gw := startTestGateway(t)
			defer gw.Shutdown()

			conn := dialGateway(t, gw)
			defer conn.Close()

			token, err := jwt.SignWithCharacter("user1", "", tt.tokenCID, testSecret, time.Hour)
			if err != nil {
				t.Fatalf("sign gateway token: %v", err)
			}
			authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
			sendEnvelope(t, conn, authEnv)
			readEnvelope(t, conn) // auth response

			enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld,
				messages.EnterWorldRequest{MapID: "map_forest", CharacterID: tt.requestCID})
			sendEnvelope(t, conn, enterEnv)

			resp := readEnvelope(t, conn)
			if resp.Type != messages.MsgEnterWorldResp {
				t.Fatalf("expected MsgEnterWorldResp, got %d", resp.Type)
			}
			var ew messages.EnterWorldResponse
			if err := resp.UnmarshalPayload(&ew); err != nil {
				t.Fatalf("unmarshal: %v", err)
			}
			if ew.Error != tt.wantErr {
				t.Fatalf("Error = %q, want %q", ew.Error, tt.wantErr)
			}
			if (ew.JoinToken != "") != tt.wantJoinToken {
				t.Fatalf("JoinToken present = %v, want %v", ew.JoinToken != "", tt.wantJoinToken)
			}
			if !tt.wantJoinToken {
				return
			}
			claims, err := jwt.Verify(ew.JoinToken, testSecret)
			if err != nil {
				t.Fatalf("verify join token: %v", err)
			}
			if claims.CharacterID != tt.wantJoinCID {
				t.Errorf("join token cid = %q, want %q", claims.CharacterID, tt.wantJoinCID)
			}
			if claims.ServerID != "srv1" || claims.Jti == "" {
				t.Errorf("join token sid/jti = %q/%q, want srv1/non-empty", claims.ServerID, claims.Jti)
			}
		})
	}
}
