package auth

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/duycuong/rpg-mmo/nakama/character"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/heroiclabs/nakama-common/runtime"
)

// TestGatewayTokenRPC_CharacterID pins ADR-31 on the issuing side: a requested
// character_id is minted as the `cid` claim only when the caller owns it; an
// absent one mints no claim (default character); another account's character
// and an unknown id get the same not-found answer.
func TestGatewayTokenRPC_CharacterID(t *testing.T) {
	env := map[string]string{"JWT_SECRET": "rpc-secret"}

	rosterOf := func(userID string, ids ...string) string {
		cs := make([]character.Character, 0, len(ids))
		for i, id := range ids {
			cs = append(cs, character.Character{ID: id, Slot: i, Name: "Hero"})
		}
		b, _ := json.Marshal(character.Roster{Characters: cs})
		return string(b)
	}

	tests := []struct {
		name     string
		userID   string
		payload  string
		seed     map[string]string // userID -> roster JSON
		readErr  error
		nilStore bool
		wantErr  error
		wantCID  string
	}{
		{name: "default character, no roster read", userID: "cid-u1", payload: "", wantCID: ""},
		{name: "owned character", userID: "cid-u2", payload: `{"character_id":"c-1"}`,
			seed: map[string]string{"cid-u2": rosterOf("cid-u2", "c-1", "c-2")}, wantCID: "c-1"},
		{name: "owned character with server id", userID: "cid-u3", payload: `{"character_id":"c-2","server_id":"gs-1"}`,
			seed: map[string]string{"cid-u3": rosterOf("cid-u3", "c-1", "c-2")}, wantCID: "c-2"},
		{name: "another account's character", userID: "cid-u4", payload: `{"character_id":"theirs"}`,
			seed:    map[string]string{"other": rosterOf("other", "theirs")},
			wantErr: character.ErrCharacterNotFound},
		{name: "unknown character", userID: "cid-u5", payload: `{"character_id":"nope"}`,
			seed:    map[string]string{"cid-u5": rosterOf("cid-u5", "c-1")},
			wantErr: character.ErrCharacterNotFound},
		{name: "storage failure", userID: "cid-u6", payload: `{"character_id":"c-1"}`,
			readErr: errors.New("db down"), wantErr: ErrInternal},
		{name: "no storage handle", userID: "cid-u7", payload: `{"character_id":"c-1"}`,
			nilStore: true, wantErr: ErrInternal},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_ENV, env) //nolint:staticcheck
			ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_USER_ID, tt.userID)         //nolint:staticcheck

			st := newMockStore()
			st.readErr = tt.readErr
			for uid, v := range tt.seed {
				st.seed(character.Collection, character.RosterKey, uid, v)
			}
			var store character.Store = st
			if tt.nilStore {
				store = nil
			}

			out, err := gatewayToken(ctx, noopLogger{}, store, tt.payload)
			if tt.wantErr != nil {
				if err != tt.wantErr {
					t.Fatalf("err = %v, want %v", err, tt.wantErr)
				}
				return
			}
			if err != nil {
				t.Fatalf("gatewayToken: %v", err)
			}
			if tt.payload == "" && st.readCalls != 0 {
				t.Errorf("default character read storage %d times, want 0", st.readCalls)
			}
			var resp GatewayTokenResponse
			if err := json.Unmarshal([]byte(out), &resp); err != nil {
				t.Fatalf("unmarshal: %v", err)
			}
			claims, err := jwt.Verify(resp.Token, env["JWT_SECRET"])
			if err != nil {
				t.Fatalf("verify: %v", err)
			}
			if claims.CharacterID != tt.wantCID || resp.CharacterID != tt.wantCID {
				t.Errorf("cid claim/echo = %q/%q, want %q", claims.CharacterID, resp.CharacterID, tt.wantCID)
			}
		})
	}
}
