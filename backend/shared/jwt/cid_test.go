package jwt

import (
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
	"time"
)

// TestCharacterIDClaim pins the ADR-31 `cid` claim: it round-trips when set, it
// is OMITTED from the payload when empty (so a default-character token is
// byte-for-byte the shape every pre-protocol-3 reader already accepts), and the
// jti rule is unchanged (jti iff a server id is present).
func TestCharacterIDClaim(t *testing.T) {
	const secret = "test-secret"
	tests := []struct {
		name        string
		serverID    string
		characterID string
		wantCIDKey  bool
		wantJTI     bool
	}{
		{name: "gateway token with character", serverID: "", characterID: "c-1", wantCIDKey: true, wantJTI: false},
		{name: "gateway token default character", serverID: "", characterID: "", wantCIDKey: false, wantJTI: false},
		{name: "join token with character", serverID: "gs-1", characterID: "c-2", wantCIDKey: true, wantJTI: true},
		{name: "join token default character", serverID: "gs-1", characterID: "", wantCIDKey: false, wantJTI: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			tok, err := SignWithCharacter("user-1", tt.serverID, tt.characterID, secret, time.Hour)
			if err != nil {
				t.Fatalf("SignWithCharacter: %v", err)
			}
			claims, err := Verify(tok, secret)
			if err != nil {
				t.Fatalf("Verify: %v", err)
			}
			if claims.CharacterID != tt.characterID {
				t.Errorf("CharacterID = %q, want %q", claims.CharacterID, tt.characterID)
			}
			if claims.ServerID != tt.serverID {
				t.Errorf("ServerID = %q, want %q", claims.ServerID, tt.serverID)
			}
			if (claims.Jti != "") != tt.wantJTI {
				t.Errorf("Jti present = %v, want %v", claims.Jti != "", tt.wantJTI)
			}

			payload, err := base64.RawURLEncoding.DecodeString(strings.Split(tok, ".")[1])
			if err != nil {
				t.Fatalf("decode payload: %v", err)
			}
			var raw map[string]any
			if err := json.Unmarshal(payload, &raw); err != nil {
				t.Fatalf("unmarshal payload: %v", err)
			}
			if _, ok := raw["cid"]; ok != tt.wantCIDKey {
				t.Errorf("cid key present = %v, want %v (payload %s)", ok, tt.wantCIDKey, payload)
			}
		})
	}
}

// TestCharacterIDClaim_AbsentIsTolerated verifies a token minted without any
// cid (an older issuer) still verifies, with an empty CharacterID.
func TestCharacterIDClaim_AbsentIsTolerated(t *testing.T) {
	const secret = "test-secret"
	tests := []struct {
		name string
		sign func() (string, error)
	}{
		{name: "Sign", sign: func() (string, error) { return Sign("u", secret, time.Hour) }},
		{name: "SignWithServer", sign: func() (string, error) { return SignWithServer("u", "gs", secret, time.Hour) }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			tok, err := tt.sign()
			if err != nil {
				t.Fatalf("sign: %v", err)
			}
			claims, err := Verify(tok, secret)
			if err != nil {
				t.Fatalf("Verify: %v", err)
			}
			if claims.CharacterID != "" {
				t.Errorf("CharacterID = %q, want empty", claims.CharacterID)
			}
		})
	}
}

// TestKeyringSignWithCharacter checks the keyring path carries cid and that an
// empty keyring refuses to sign.
func TestKeyringSignWithCharacter(t *testing.T) {
	tests := []struct {
		name    string
		secrets []string
		wantErr bool
	}{
		{name: "single secret", secrets: []string{"current"}},
		{name: "rotation list", secrets: []string{"current", "previous"}},
		{name: "empty keyring", secrets: nil, wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var k Keyring
			if len(tt.secrets) > 0 {
				var err error
				k, err = NewKeyring(tt.secrets...)
				if err != nil {
					t.Fatalf("NewKeyring: %v", err)
				}
			}
			tok, err := k.SignWithCharacter("u", "gs", "c-9", time.Hour)
			if tt.wantErr {
				if err == nil {
					t.Fatal("want error from empty keyring")
				}
				return
			}
			if err != nil {
				t.Fatalf("SignWithCharacter: %v", err)
			}
			claims, err := k.Verify(tok)
			if err != nil {
				t.Fatalf("Verify: %v", err)
			}
			if claims.CharacterID != "c-9" {
				t.Errorf("CharacterID = %q, want c-9", claims.CharacterID)
			}
		})
	}
}
