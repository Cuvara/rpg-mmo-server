package jwt

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
)

func gamePayload(t *testing.T, token string) []byte {
	t.Helper()
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		t.Fatalf("invalid token: %q", token)
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		t.Fatal(err)
	}
	return payload
}

func TestGameIDClaim(t *testing.T) {
	const secret = "secret"
	k, err := NewKeyring(secret, "old")
	if err != nil {
		t.Fatal(err)
	}
	tests := []struct {
		name, game, wantClaim, wantGame string
		present                         bool
	}{
		{"absent", "", "", constants.DefaultGameID, false},
		{"default", constants.DefaultGameID, "", constants.DefaultGameID, false},
		{"other", "othergame", "othergame", "othergame", true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			signers := []struct {
				name string
				sign func() (string, error)
			}{
				{"direct", func() (string, error) { return SignWithGame("u", "s", "c", tt.game, secret, time.Hour) }},
				{"keyring", func() (string, error) { return k.SignWithGame("u", "s", "c", tt.game, time.Hour) }},
			}
			for _, signer := range signers {
				t.Run(signer.name, func(t *testing.T) {
					token, err := signer.sign()
					if err != nil {
						t.Fatal(err)
					}
					readers := []struct {
						name string
						read func(string) (Claims, error)
					}{
						{"Verify", func(token string) (Claims, error) { return Verify(token, secret) }},
						{"Keyring.Verify", k.Verify}, {"ParseUnverified", ParseUnverified},
					}
					for _, reader := range readers {
						claims, err := reader.read(token)
						if err != nil {
							t.Fatalf("%s: %v", reader.name, err)
						}
						if claims.GameID != tt.wantClaim || claims.Game() != tt.wantGame {
							t.Errorf("%s: GameID=%q Game()=%q, want %q and %q", reader.name, claims.GameID, claims.Game(), tt.wantClaim, tt.wantGame)
						}
					}
					var raw map[string]json.RawMessage
					if err := json.Unmarshal(gamePayload(t, token), &raw); err != nil {
						t.Fatal(err)
					}
					if _, ok := raw["gid"]; ok != tt.present {
						t.Errorf("gid present = %v, want %v", ok, tt.present)
					}
				})
			}
		})
	}
}

func TestSignWithGameRejectsInvalidID(t *testing.T) {
	k, err := NewKeyring("secret")
	if err != nil {
		t.Fatal(err)
	}
	for _, game := range []string{"Foo", "foo-bar", "a"} {
		t.Run(game, func(t *testing.T) {
			if _, err := SignWithGame("u", "", "", game, "secret", time.Hour); err == nil {
				t.Error("direct signing accepted invalid game")
			}
			if _, err := k.SignWithGame("u", "", "", game, time.Hour); err == nil {
				t.Error("keyring signing accepted invalid game")
			}
		})
	}
	if _, err := (Keyring{}).SignWithGame("u", "", "", "othergame", time.Hour); err == nil {
		t.Error("empty keyring signed token")
	}
}

func TestLegacyClaimBytes(t *testing.T) {
	type developClaims struct {
		UserID      string `json:"sub"`
		ServerID    string `json:"sid,omitempty"`
		Jti         string `json:"jti,omitempty"`
		CharacterID string `json:"cid,omitempty"`
		IssuedAt    int64  `json:"iat"`
		ExpireAt    int64  `json:"exp"`
	}
	const secret = "secret"
	tests := []struct {
		name string
		sign func() (string, error)
	}{
		{"Sign", func() (string, error) { return Sign("u", secret, time.Hour) }},
		{"SignWithServer", func() (string, error) { return SignWithServer("u", "s", secret, time.Hour) }},
		{"SignWithCharacter without cid", func() (string, error) { return SignWithCharacter("u", "s", "", secret, time.Hour) }},
		{"SignWithCharacter with cid", func() (string, error) { return SignWithCharacter("u", "s", "c", secret, time.Hour) }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			token, err := tt.sign()
			if err != nil {
				t.Fatal(err)
			}
			payload := gamePayload(t, token)
			var old developClaims
			if err := json.Unmarshal(payload, &old); err != nil {
				t.Fatal(err)
			}
			want, err := json.Marshal(old)
			if err != nil {
				t.Fatal(err)
			}
			if !bytes.Equal(payload, want) {
				t.Errorf("payload = %s, want %s", payload, want)
			}
		})
	}
}
