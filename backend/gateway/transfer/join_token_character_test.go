package transfer

import (
	"testing"

	"github.com/duycuong/rpg-mmo/shared/jwt"
)

// TestGenerateJoinTokenCharacter checks the join token carries the `cid`
// claim it was given (ADR-31), omits it when empty, and still refuses an empty
// server id.
func TestGenerateJoinTokenCharacter(t *testing.T) {
	keys, err := jwt.NewKeyring("join-secret")
	if err != nil {
		t.Fatalf("NewKeyring: %v", err)
	}
	tests := []struct {
		name        string
		serverID    string
		characterID string
		wantErr     bool
	}{
		{name: "selected character", serverID: "gs-1", characterID: "char-1"},
		{name: "default character", serverID: "gs-1", characterID: ""},
		{name: "no server id", serverID: "", characterID: "char-1", wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			tok, err := GenerateJoinTokenCharacter("user-1", tt.serverID, tt.characterID, keys)
			if tt.wantErr {
				if err == nil {
					t.Fatal("want error")
				}
				return
			}
			if err != nil {
				t.Fatalf("GenerateJoinTokenCharacter: %v", err)
			}
			claims, err := keys.Verify(tok)
			if err != nil {
				t.Fatalf("Verify: %v", err)
			}
			if claims.CharacterID != tt.characterID {
				t.Errorf("cid = %q, want %q", claims.CharacterID, tt.characterID)
			}
			if claims.ServerID != tt.serverID || claims.Jti == "" {
				t.Errorf("sid/jti = %q/%q", claims.ServerID, claims.Jti)
			}
		})
	}
}
