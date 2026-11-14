package gamectx

import (
	"context"
	"errors"
	"fmt"
	"reflect"
	"strings"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/heroiclabs/nakama-common/runtime"
)

func TestParseAllowList(t *testing.T) {
	tests := []struct {
		name     string
		raw      string
		want     []string
		badEntry string
		wantErr  bool
	}{
		{name: "absent", want: []string{constants.DefaultGameID}},
		{name: "whitespace", raw: " \t\n ", want: []string{constants.DefaultGameID}},
		{name: "default", raw: "cuvara", want: []string{"cuvara"}},
		{name: "multiple", raw: "cuvara,foo", want: []string{"cuvara", "foo"}},
		{name: "trim entries", raw: " foo , bar ", want: []string{"foo", "bar"}},
		{name: "duplicates retain order", raw: "foo,cuvara,foo,bar,cuvara", want: []string{"foo", "cuvara", "bar"}},
		{name: "no implicit default", raw: "foo", want: []string{"foo"}},
		{name: "empty interior", raw: "a,,b", badEntry: "a", wantErr: true},
		{name: "empty valid interior", raw: "foo,,bar", badEntry: "", wantErr: true},
		{name: "trailing comma", raw: "foo,", badEntry: "", wantErr: true},
		{name: "leading comma", raw: ",foo", badEntry: "", wantErr: true},
		{name: "whitespace entry", raw: "foo, \t,bar", badEntry: "", wantErr: true},
		{name: "uppercase", raw: "Foo", badEntry: "Foo", wantErr: true},
		{name: "hyphen", raw: "foo-bar", badEntry: "foo-bar", wantErr: true},
		{name: "too short", raw: "a", badEntry: "a", wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := ParseAllowList(tt.raw)
			if tt.wantErr {
				if !errors.Is(err, ErrGamesConfig) {
					t.Fatalf("ParseAllowList(%q) error = %v, want ErrGamesConfig", tt.raw, err)
				}
				if !strings.Contains(err.Error(), fmt.Sprintf("entry %q", tt.badEntry)) {
					t.Errorf("error %q does not name entry %q", err, tt.badEntry)
				}
				if got != nil {
					t.Errorf("invalid list returned partial games %v", got)
				}
				return
			}
			if err != nil || !reflect.DeepEqual(got, tt.want) {
				t.Fatalf("ParseAllowList(%q) = %v, %v; want %v, nil", tt.raw, got, err, tt.want)
			}
		})
	}
}

func TestAllowList(t *testing.T) {
	tests := []struct {
		name    string
		process string
		env     map[string]string
		want    []string
		wantErr bool
	}{
		{name: "process unset", want: []string{constants.DefaultGameID}},
		{name: "process fallback", process: "foo,bar", want: []string{"foo", "bar"}},
		{name: "missing key fallback", process: "foo", env: map[string]string{"OTHER": "bar"}, want: []string{"foo"}},
		{name: "runtime precedence", process: "bar", env: map[string]string{EnvGames: "foo"}, want: []string{"foo"}},
		{name: "runtime empty overrides process", process: "foo", env: map[string]string{EnvGames: ""}, want: []string{constants.DefaultGameID}},
		{name: "runtime whitespace overrides process", process: "foo", env: map[string]string{EnvGames: " \t "}, want: []string{constants.DefaultGameID}},
		{name: "runtime valid overrides invalid process", process: "Foo", env: map[string]string{EnvGames: "bar"}, want: []string{"bar"}},
		{name: "runtime invalid", process: "foo", env: map[string]string{EnvGames: "Foo"}, wantErr: true},
		{name: "process invalid", process: "foo,", wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Setenv(EnvGames, tt.process)
			ctx := context.Background()
			if tt.env != nil {
				ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_ENV, tt.env)
			}
			got, err := AllowList(ctx)
			if tt.wantErr {
				if !errors.Is(err, ErrGamesConfig) {
					t.Fatalf("AllowList error = %v, want ErrGamesConfig", err)
				}
				return
			}
			if err != nil || !reflect.DeepEqual(got, tt.want) {
				t.Fatalf("AllowList = %v, %v; want %v, nil", got, err, tt.want)
			}
		})
	}
}

func TestAllowed(t *testing.T) {
	tests := []struct {
		name  string
		games string
		game  string
		want  bool
		err   error
	}{
		{name: "default only", game: "cuvara", want: true},
		{name: "unset refuses other", game: "foo"},
		{name: "listed", games: "cuvara,foo", game: "foo", want: true},
		{name: "unlisted", games: "foo", game: "bar"},
		{name: "default not implicitly listed", games: "foo", game: "cuvara"},
		{name: "exact entry", games: "foobar", game: "foo"},
		{name: "configuration error", games: "Foo", game: "foo", err: ErrGamesConfig},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := Allowed(gameContext(tt.games, "", nil), tt.game)
			if got != tt.want || !errors.Is(err, tt.err) {
				t.Fatalf("Allowed(%q) = %t, %v; want %t, %v", tt.game, got, err, tt.want, tt.err)
			}
		})
	}
}

func TestGameFromVars(t *testing.T) {
	tests := []struct {
		name  string
		games string
		vars  map[string]string
		want  string
		err   error
	}{
		{name: "nil", want: constants.DefaultGameID},
		{name: "missing", vars: map[string]string{"other": "foo"}, want: constants.DefaultGameID},
		{name: "empty", vars: map[string]string{VarGame: ""}, want: constants.DefaultGameID},
		{name: "explicit default", vars: map[string]string{VarGame: "cuvara"}, want: constants.DefaultGameID},
		{name: "allowed", games: "cuvara,foo", vars: map[string]string{VarGame: "foo"}, want: "foo"},
		{name: "not allowed", games: "cuvara,foo", vars: map[string]string{VarGame: "bar"}, err: ErrGameNotAllowed},
		{name: "invalid id", games: "cuvara,foo", vars: map[string]string{VarGame: "Foo"}, err: ErrGameNotAllowed},
		{name: "no trimming session vars", games: "foo", vars: map[string]string{VarGame: " foo "}, err: ErrGameNotAllowed},
		{name: "unset refuses nondefault", vars: map[string]string{VarGame: "foo"}, err: ErrGameNotAllowed},
		{name: "configured list refuses missing default", games: "foo", err: ErrGameNotAllowed},
		{name: "configured list refuses explicit default", games: "foo", vars: map[string]string{VarGame: "cuvara"}, err: ErrGameNotAllowed},
		{name: "invalid configuration", games: "foo,", vars: map[string]string{VarGame: "foo"}, err: ErrGamesConfig},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := GameFromVars(gameContext(tt.games, "", nil), tt.vars)
			if got != tt.want || !errors.Is(err, tt.err) {
				t.Fatalf("GameFromVars(%v) = %q, %v; want %q, %v", tt.vars, got, err, tt.want, tt.err)
			}
			if errors.Is(err, ErrGamesConfig) && !strings.Contains(err.Error(), `entry ""`) {
				t.Errorf("configuration error does not name invalid entry: %v", err)
			}
		})
	}
}

func TestGameFromContext(t *testing.T) {
	tests := []struct {
		name    string
		games   string
		userID  string
		vars    map[string]string
		payload string
		want    string
		err     error
	}{
		{name: "session default", userID: "user", want: constants.DefaultGameID},
		{name: "session empty var", userID: "user", vars: map[string]string{VarGame: ""}, want: constants.DefaultGameID},
		{name: "session allowed", games: "cuvara,foo", userID: "user", vars: map[string]string{VarGame: "foo"}, want: "foo"},
		{name: "session unlisted", games: "cuvara,foo", userID: "user", vars: map[string]string{VarGame: "bar"}, err: ErrGameNotAllowed},
		{name: "session invalid", games: "cuvara,foo", userID: "user", vars: map[string]string{VarGame: "Foo"}, err: ErrGameNotAllowed},
		{name: "session equal payload", games: "foo", userID: "user", vars: map[string]string{VarGame: "foo"}, payload: "foo", want: "foo"},
		{name: "session default equal payload", userID: "user", payload: "cuvara", want: constants.DefaultGameID},
		{name: "session cannot override foo", games: "cuvara,foo", userID: "user", vars: map[string]string{VarGame: "foo"}, payload: "cuvara", err: ErrGameMismatch},
		{name: "session cannot override default", games: "cuvara,foo", userID: "user", payload: "foo", err: ErrGameMismatch},
		{name: "session invalid payload differs", games: "cuvara,foo", userID: "user", vars: map[string]string{VarGame: "foo"}, payload: "Foo", err: ErrGameMismatch},
		{name: "session unset refuses nondefault", userID: "user", vars: map[string]string{VarGame: "foo"}, err: ErrGameNotAllowed},
		{name: "session default omitted from list", games: "foo", userID: "user", err: ErrGameNotAllowed},
		{name: "server allowed payload", games: "foo", payload: "foo", want: "foo"},
		{name: "server unlisted payload", games: "foo", payload: "bar", err: ErrGameNotAllowed},
		{name: "server invalid payload", games: "foo", payload: "Foo", err: ErrGameNotAllowed},
		{name: "server unset refuses nondefault", payload: "foo", err: ErrGameNotAllowed},
		{name: "server empty payload", want: constants.DefaultGameID},
		{name: "server explicit default", payload: "cuvara", want: constants.DefaultGameID},
		{name: "server ignores vars", vars: map[string]string{VarGame: "foo"}, want: constants.DefaultGameID},
		{name: "server payload ignores conflicting vars", games: "foo,bar", vars: map[string]string{VarGame: "bar"}, payload: "foo", want: "foo"},
		{name: "server default omitted from list", games: "foo", err: ErrGameNotAllowed},
		{name: "session invalid configuration", games: "foo,", userID: "user", vars: map[string]string{VarGame: "foo"}, err: ErrGamesConfig},
		{name: "server invalid configuration", games: "foo,", payload: "foo", err: ErrGamesConfig},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := GameFromContext(gameContext(tt.games, tt.userID, tt.vars), tt.payload)
			if got != tt.want || !errors.Is(err, tt.err) {
				t.Fatalf("GameFromContext(%q) = %q, %v; want %q, %v", tt.payload, got, err, tt.want, tt.err)
			}
		})
	}
}

func TestGameFromContext_AfterSessionRefresh(t *testing.T) {
	// ADR-33 D1: the plugin sees refreshed session vars through RUNTIME_CTX_VARS
	// in the same context shape as authenticated session vars. This unit test
	// checks resolution for that shape, not Nakama's token refresh machinery.
	// Real Nakama refresh persistence remains HUMAN_REQUIRED until the local-stack
	// gate is approved and a real authenticate/refresh/RPC flow is exercised.
	for _, game := range []string{constants.DefaultGameID, "foo"} {
		t.Run(game, func(t *testing.T) {
			authenticated := gameContext("cuvara,foo", "user", map[string]string{VarGame: game})
			before, err := GameFromContext(authenticated, "")
			if err != nil || before != game {
				t.Fatalf("authenticated context = %q, %v; want %q, nil", before, err, game)
			}
			// A separate map represents vars supplied on a later refreshed request.
			refreshed := gameContext("cuvara,foo", "user", map[string]string{VarGame: game})
			after, err := GameFromContext(refreshed, "")
			if err != nil || after != before {
				t.Fatalf("refreshed context = %q, %v; want %q, nil", after, err, before)
			}
		})
	}
}

func TestScoped(t *testing.T) {
	tests := []struct {
		game string
		name string
		want string
	}{
		{"", "player", "player"},
		{"cuvara", "player", "player"},
		{"foo", "player", "foo.player"},
		{"cuvara", "characters", "characters"},
		{"foo", "characters", "foo.characters"},
	}
	for _, tt := range tests {
		t.Run(tt.game+"/"+tt.name, func(t *testing.T) {
			if got := Scoped(tt.game, tt.name); got != tt.want {
				t.Fatalf("Scoped(%q, %q) = %q, want %q", tt.game, tt.name, got, tt.want)
			}
		})
	}
}

func TestRuntimeErrors(t *testing.T) {
	tests := []struct {
		name string
		err  *runtime.Error
		text string
		code int
	}{
		{"not allowed", ErrGameNotAllowed, "game not allowed", 3},
		{"mismatch", ErrGameMismatch, "game_id does not match the session game", 7},
		{"configuration", ErrGamesConfig, "internal error", 13},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if tt.err.Error() != tt.text || tt.err.Code != tt.code {
				t.Fatalf("runtime error = %q, code %d; want %q, code %d", tt.err.Error(), tt.err.Code, tt.text, tt.code)
			}
		})
	}
}

func gameContext(games, userID string, vars map[string]string) context.Context {
	ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_ENV, map[string]string{EnvGames: games})
	if userID != "" {
		ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_USER_ID, userID)
	}
	if vars != nil {
		ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_VARS, vars)
	}
	return ctx
}
