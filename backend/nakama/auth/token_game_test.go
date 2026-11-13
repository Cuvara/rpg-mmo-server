package auth

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"strings"
	"testing"

	"github.com/duycuong/rpg-mmo/nakama/character"
	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/heroiclabs/nakama-common/runtime"
)

func gameTokenContext(userID, game, games string) context.Context {
	ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_ID, userID) //nolint:staticcheck // Nakama uses string context keys.
	ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_USERNAME, "Game Hero")             //nolint:staticcheck
	ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_ENV, map[string]string{            //nolint:staticcheck
		"GAMES": games, "JWT_SECRET": "game-token-secret",
	})
	return context.WithValue(ctx, runtime.RUNTIME_CTX_VARS, map[string]string{"game": game}) //nolint:staticcheck
}

func decodeGameToken(t *testing.T, output, secret string) (GatewayTokenResponse, jwt.Claims) {
	t.Helper()
	var response GatewayTokenResponse
	if err := json.Unmarshal([]byte(output), &response); err != nil {
		t.Fatalf("decode response: %v", err)
	}
	claims, err := jwt.Verify(response.Token, secret)
	if err != nil {
		t.Fatalf("verify token: %v", err)
	}
	return response, claims
}

func assertNoGameClaim(t *testing.T, response GatewayTokenResponse) {
	t.Helper()
	claims, err := jwt.ParseUnverified(response.Token)
	if err != nil {
		t.Fatalf("parse claims: %v", err)
	}
	if claims.GameID != "" || claims.Game() != "cuvara" || response.GameID != "" {
		t.Fatalf("default game claim/normalized/echo = %q/%q/%q", claims.GameID, claims.Game(), response.GameID)
	}
	segments := strings.Split(response.Token, ".")
	decoded, err := base64.RawURLEncoding.DecodeString(segments[1])
	if err != nil {
		t.Fatalf("decode raw claims: %v", err)
	}
	var raw map[string]json.RawMessage
	if err := json.Unmarshal(decoded, &raw); err != nil {
		t.Fatalf("decode raw claims JSON: %v", err)
	}
	if _, exists := raw["gid"]; exists {
		t.Fatalf("default token contains gid: %s", decoded)
	}
}

func TestGatewayTokenGame_DefaultWithoutEnvironment(t *testing.T) {
	t.Setenv("GAMES", "")
	t.Setenv("JWT_SECRET", "game-token-secret")
	ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_ID, t.Name()) //nolint:staticcheck
	store := newMockStore()
	output, err := gatewayToken(ctx, noopLogger{}, store, "")
	if err != nil {
		t.Fatalf("gatewayToken: %v", err)
	}
	response, _ := decodeGameToken(t, output, "game-token-secret")
	assertNoGameClaim(t, response)
	var raw map[string]json.RawMessage
	if err := json.Unmarshal([]byte(output), &raw); err != nil {
		t.Fatal(err)
	}
	if _, exists := raw["game_id"]; exists {
		t.Fatalf("default response contains game_id: %s", output)
	}
	if store.readCalls != 0 || len(store.writes) != 0 {
		t.Fatalf("default call read/writes = %d/%d, want 0/0", store.readCalls, len(store.writes))
	}
}

func TestGatewayTokenGame_ProfileCreatedOnce(t *testing.T) {
	ctx := gameTokenContext(t.Name(), "foo", "cuvara,foo")
	store := newMockStore()
	// A default-game profile must not suppress creation of the game's profile.
	store.seed(ProfileCollection, ProfileKey, t.Name(), `{"level":42,"display_name":"Default Hero"}`)
	for i := 0; i < 2; i++ {
		output, err := gatewayToken(ctx, noopLogger{}, store, `{"game_id":"foo"}`)
		if err != nil {
			t.Fatalf("call %d: %v", i, err)
		}
		response, claims := decodeGameToken(t, output, "game-token-secret")
		if claims.GameID != "foo" || claims.Game() != "foo" || response.GameID != "foo" {
			t.Fatalf("game claim/normalized/echo = %q/%q/%q", claims.GameID, claims.Game(), response.GameID)
		}
		if claims.UserID != t.Name() || response.UserID != t.Name() {
			t.Fatalf("user claim/echo = %q/%q", claims.UserID, response.UserID)
		}
	}
	if len(store.writes) != 1 {
		t.Fatalf("profile writes = %d, want 1", len(store.writes))
	}
	write := store.writes[0]
	if write.Collection != "foo.player" || write.Key != ProfileKey || write.UserID != t.Name() {
		t.Fatalf("profile target = %s/%s/%s", write.Collection, write.Key, write.UserID)
	}
	if write.PermissionRead != 2 || write.PermissionWrite != 0 {
		t.Fatalf("profile read/write permissions = %d/%d", write.PermissionRead, write.PermissionWrite)
	}
	var profile Profile
	if err := json.Unmarshal([]byte(write.Value), &profile); err != nil {
		t.Fatal(err)
	}
	if profile.DisplayName != "Game Hero" || profile.Level != StartingLevel || profile.CreatedAt == 0 {
		t.Fatalf("profile = %+v", profile)
	}
}

func TestGatewayTokenGame_Rejections(t *testing.T) {
	tests := []struct {
		name     string
		game     string
		games    string
		payload  string
		readErr  error
		writeErr error
		nilStore bool
		wantErr  error
	}{
		{name: "game outside allow-list", game: "bar", games: "cuvara,foo", wantErr: gamectx.ErrGameNotAllowed},
		{name: "payload overrides session", game: "foo", games: "cuvara,foo", payload: `{"game_id":"cuvara"}`, wantErr: gamectx.ErrGameMismatch},
		{name: "payload overrides missing var", games: "cuvara,foo", payload: `{"game_id":"foo"}`, wantErr: gamectx.ErrGameMismatch},
		{name: "invalid games config", game: "foo", games: "cuvara,INVALID", wantErr: ErrInternal},
		{name: "no profile store", game: "foo", games: "cuvara,foo", nilStore: true, wantErr: ErrInternal},
		{name: "profile read fails", game: "foo", games: "cuvara,foo", readErr: errStorage, wantErr: ErrInternal},
		{name: "profile write fails", game: "foo", games: "cuvara,foo", writeErr: errStorage, wantErr: ErrInternal},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			store := newMockStore()
			store.readErr, store.writeErr = tt.readErr, tt.writeErr
			var roster character.Store = store
			if tt.nilStore {
				roster = nil
			}
			output, err := gatewayToken(gameTokenContext(t.Name(), tt.game, tt.games), noopLogger{}, roster, tt.payload)
			if err != tt.wantErr || output != "" {
				t.Fatalf("gatewayToken = %q, %v; want empty output, %v", output, err, tt.wantErr)
			}
			if len(store.writes) != 0 {
				t.Fatalf("rejected call wrote %d profiles", len(store.writes))
			}
		})
	}
}

func TestGatewayTokenGame_Roster(t *testing.T) {
	tests := []struct {
		name       string
		game       string
		collection string
		owner      string
		value      string
		readErr    error
		nilStore   bool
		wantErr    error
	}{
		{name: "scoped roster owns character", game: "foo", collection: "foo.characters"},
		{name: "default roster cannot grant scoped character", game: "foo", collection: "characters", wantErr: character.ErrCharacterNotFound},
		{name: "another game's roster cannot grant character", game: "foo", collection: "bar.characters", wantErr: character.ErrCharacterNotFound},
		{name: "another user cannot grant character", game: "foo", collection: "foo.characters", owner: "other-user", wantErr: character.ErrCharacterNotFound},
		{name: "scoped roster missing", game: "foo", wantErr: character.ErrCharacterNotFound},
		{name: "scoped roster corrupt", game: "foo", collection: "foo.characters", value: "{bad-json", wantErr: ErrInternal},
		{name: "scoped roster read fails", game: "foo", readErr: errStorage, wantErr: ErrInternal},
		{name: "scoped roster store missing", game: "foo", nilStore: true, wantErr: ErrInternal},
		{name: "default roster still owns character", game: "cuvara", collection: "characters"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			store := newMockStore()
			store.readErr = tt.readErr
			if tt.collection != "" {
				owner := tt.owner
				if owner == "" {
					owner = t.Name()
				}
				value := tt.value
				if value == "" {
					data, err := json.Marshal(character.Roster{Characters: []character.Character{{ID: "c-game", Name: "Hero"}}})
					if err != nil {
						t.Fatal(err)
					}
					value = string(data)
				}
				store.seed(tt.collection, character.RosterKey, owner, value)
			}
			var roster character.Store = store
			if tt.nilStore {
				roster = nil
			}
			output, err := gatewayToken(gameTokenContext(t.Name(), tt.game, "cuvara,foo"), noopLogger{}, roster, `{"character_id":"c-game"}`)
			if tt.wantErr != nil {
				if err != tt.wantErr || output != "" {
					t.Fatalf("gatewayToken = %q, %v; want empty output, %v", output, err, tt.wantErr)
				}
				if len(store.writes) != 0 {
					t.Fatalf("rejected roster wrote %d profiles", len(store.writes))
				}
				return
			}
			if err != nil {
				t.Fatalf("gatewayToken: %v", err)
			}
			response, claims := decodeGameToken(t, output, "game-token-secret")
			if claims.CharacterID != "c-game" || response.CharacterID != "c-game" {
				t.Fatalf("character claim/echo = %q/%q", claims.CharacterID, response.CharacterID)
			}
			if tt.game == "cuvara" {
				assertNoGameClaim(t, response)
				if len(store.writes) != 0 {
					t.Fatalf("default character wrote %d profiles", len(store.writes))
				}
			} else if claims.GameID != "foo" || response.GameID != "foo" || len(store.writes) != 1 {
				t.Fatalf("scoped claim/echo/profile writes = %q/%q/%d", claims.GameID, response.GameID, len(store.writes))
			}
		})
	}
}

func TestIssueGatewayTokenForGame(t *testing.T) {
	for _, game := range []string{"", "cuvara", "foo"} {
		t.Run("game="+game, func(t *testing.T) {
			response, err := IssueGatewayTokenForGame(t.Name(), "", "c-game", game, testConfig())
			if err != nil {
				t.Fatal(err)
			}
			claims, err := jwt.Verify(response.Token, testConfig().JWTSecret)
			if err != nil {
				t.Fatal(err)
			}
			if claims.CharacterID != "c-game" || response.CharacterID != "c-game" {
				t.Fatalf("character claim/echo = %q/%q", claims.CharacterID, response.CharacterID)
			}
			if game == "foo" {
				if claims.GameID != game || response.GameID != game {
					t.Fatalf("game claim/echo = %q/%q", claims.GameID, response.GameID)
				}
			} else {
				assertNoGameClaim(t, response)
			}
		})
	}
}

func TestEnsureGameProfile(t *testing.T) {
	tests := []struct {
		name     string
		userID   string
		readErr  error
		writeErr error
		nilStore bool
		wantErr  bool
	}{
		{name: "fallback display name", userID: "profile-game-user"},
		{name: "empty user rejected", wantErr: true},
		{name: "nil store rejected", userID: "profile-nil", nilStore: true, wantErr: true},
		{name: "read error wrapped", userID: "profile-read", readErr: errStorage, wantErr: true},
		{name: "write error wrapped", userID: "profile-write", writeErr: errStorage, wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			store := newMockStore()
			store.readErr, store.writeErr = tt.readErr, tt.writeErr
			var profileStorage profileStore = store
			if tt.nilStore {
				profileStorage = nil
			}
			created, err := EnsureGameProfile(context.Background(), profileStorage, "foo", tt.userID, "")
			if tt.wantErr {
				if err == nil || created || len(store.writes) != 0 {
					t.Fatalf("EnsureGameProfile = %v, %v; writes %d", created, err, len(store.writes))
				}
				if (tt.readErr != nil || tt.writeErr != nil) && !errors.Is(err, errStorage) {
					t.Fatalf("error %v does not wrap storage failure", err)
				}
				return
			}
			if err != nil || !created || len(store.writes) != 1 {
				t.Fatalf("EnsureGameProfile = %v, %v; writes %d", created, err, len(store.writes))
			}
			var profile Profile
			if err := json.Unmarshal([]byte(store.writes[0].Value), &profile); err != nil {
				t.Fatal(err)
			}
			if profile.DisplayName != "Player-profile-" {
				t.Fatalf("fallback name = %q", profile.DisplayName)
			}
		})
	}
}
