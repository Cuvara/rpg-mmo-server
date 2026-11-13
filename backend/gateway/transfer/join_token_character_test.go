package transfer

import (
	"encoding/base64"
	"encoding/json"
	"os"
	"strings"
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
			tok, err := GenerateJoinTokenCharacter("user-1", tt.serverID, tt.characterID, "", keys)
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

// Each game's join token uses only its own signing secret. cuvara keeps its
// pre-ADR-33 claim set, including when the game is supplied explicitly.
func TestJoinTokensUseOnlyTheirGamesKeyring(t *testing.T) {
	t.Setenv("JOIN_TOKEN_SECRET", "cuvara-join-secret")
	t.Setenv("JOIN_TOKEN_SECRET_EMBER", "ember-join-secret")
	cuvara, err := jwt.ParseKeyring(os.Getenv("JOIN_TOKEN_SECRET"))
	if err != nil {
		t.Fatal(err)
	}
	ember, err := jwt.ParseKeyring(os.Getenv("JOIN_TOKEN_SECRET_EMBER"))
	if err != nil {
		t.Fatal(err)
	}
	keyrings, err := NewJoinKeyrings(cuvara, map[string]jwt.Keyring{"ember": ember})
	if err != nil {
		t.Fatal(err)
	}
	for _, game := range []string{"", "cuvara", "ember"} {
		t.Run("game="+game, func(t *testing.T) {
			keys, ok := keyrings.For(game)
			if !ok {
				t.Fatal("missing configured game's keys")
			}
			token, err := GenerateJoinTokenCharacter("u1", "ember-gs-1", "char-1", game, keys)
			if err != nil {
				t.Fatal(err)
			}
			claims, err := keys.Verify(token)
			if err != nil {
				t.Fatal(err)
			}
			if claims.UserID != "u1" || claims.CharacterID != "char-1" || claims.ServerID != "ember-gs-1" || claims.Jti == "" {
				t.Fatalf("join claims lost identity: %+v", claims)
			}
			payload, err := base64.RawURLEncoding.DecodeString(strings.Split(token, ".")[1])
			if err != nil {
				t.Fatal(err)
			}
			var raw map[string]json.RawMessage
			if err := json.Unmarshal(payload, &raw); err != nil {
				t.Fatal(err)
			}
			_, hasGame := raw["gid"]
			other := ember
			if game == "ember" {
				other = cuvara
				if !hasGame || claims.GameID != game {
					t.Fatalf("non-default game's gid missing: %s", payload)
				}
			} else if hasGame || claims.GameID != "" {
				t.Fatalf("cuvara token gained gid: %s", payload)
			}
			if _, err := other.Verify(token); err == nil {
				t.Fatal("another game's secret verified this token")
			}
		})
	}
}

func TestJoinKeyringsNeverFallBackAndCopyConfiguration(t *testing.T) {
	cuvara, err := jwt.NewKeyring("cuvara-secret")
	if err != nil {
		t.Fatal(err)
	}
	ember, err := jwt.NewKeyring("ember-secret")
	if err != nil {
		t.Fatal(err)
	}
	games := map[string]jwt.Keyring{"ember": ember}
	keyrings, err := NewJoinKeyrings(cuvara, games)
	if err != nil {
		t.Fatal(err)
	}
	games["ember"] = cuvara
	delete(games, "ember")
	if keys, ok := keyrings.For("ember"); !ok || keys.Signing() != ember.Signing() {
		t.Fatal("constructor retained the caller's mutable map")
	}
	for _, game := range []string{"other", "Ember", "../cuvara"} {
		if keys, ok := keyrings.For(game); ok || keys.Valid() {
			t.Fatalf("game %q fell back to another game's keys", game)
		}
	}
	var zero JoinKeyrings
	if keys, ok := zero.For(""); ok || keys.Valid() {
		t.Fatal("zero keyring set returned keys")
	}
	for _, game := range []string{"", "Ember", "x", "map", "../cuvara"} {
		if _, err := NewJoinKeyrings(cuvara, map[string]jwt.Keyring{game: ember}); err == nil {
			t.Errorf("invalid game ID %q accepted", game)
		}
	}
	if _, err := NewJoinKeyrings(jwt.Keyring{}, nil); err == nil {
		t.Fatal("missing default keys accepted")
	}
	if _, err := NewJoinKeyrings(cuvara, map[string]jwt.Keyring{"ember": {}}); err == nil {
		t.Fatal("empty per-game keys accepted")
	}
}
