package auth

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"testing"

	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// Each typed hook must preserve the exact request on success, including vars.
type beforeGameHook struct {
	name      string
	call      func(context.Context, map[string]string) error
	malformed func(context.Context, bool) error
}

func newBeforeGameHook[T any](name string, hook func(context.Context, runtime.Logger, *sql.DB, runtime.NakamaModule, *T) (*T, error), request func(map[string]string) *T) beforeGameHook {
	return beforeGameHook{
		name: name,
		call: func(ctx context.Context, vars map[string]string) error {
			in := request(vars)
			out, err := hook(ctx, noopLogger{}, nil, nil, in)
			if err == nil && out != in {
				return fmt.Errorf("hook did not preserve request")
			}
			return err
		},
		malformed: func(ctx context.Context, nilRequest bool) error {
			in := new(T)
			if nilRequest {
				in = nil
			}
			out, err := hook(ctx, noopLogger{}, nil, nil, in)
			if err == nil && out != in {
				return fmt.Errorf("hook did not preserve malformed request")
			}
			return err
		},
	}
}

func hooksGameContext(games string) context.Context {
	return context.WithValue(context.Background(), runtime.RUNTIME_CTX_ENV, map[string]string{"GAMES": games}) //nolint:staticcheck // Nakama uses string context keys.
}

func TestBeforeAuthenticateHooks_Game(t *testing.T) {
	t.Setenv("GAMES", "")
	hooks := []beforeGameHook{
		newBeforeGameHook("apple", BeforeAuthenticateApple, func(vars map[string]string) *api.AuthenticateAppleRequest {
			return &api.AuthenticateAppleRequest{Account: &api.AccountApple{Vars: vars}}
		}),
		newBeforeGameHook("custom", BeforeAuthenticateCustom, func(vars map[string]string) *api.AuthenticateCustomRequest {
			return &api.AuthenticateCustomRequest{Account: &api.AccountCustom{Vars: vars}}
		}),
		newBeforeGameHook("device", BeforeAuthenticateDevice, func(vars map[string]string) *api.AuthenticateDeviceRequest {
			return &api.AuthenticateDeviceRequest{Account: &api.AccountDevice{Vars: vars}}
		}),
		newBeforeGameHook("email", BeforeAuthenticateEmail, func(vars map[string]string) *api.AuthenticateEmailRequest {
			return &api.AuthenticateEmailRequest{Account: &api.AccountEmail{Email: "Player@Example.COM", Password: "sup3rsecret", Vars: vars}}
		}),
		newBeforeGameHook("facebook", BeforeAuthenticateFacebook, func(vars map[string]string) *api.AuthenticateFacebookRequest {
			return &api.AuthenticateFacebookRequest{Account: &api.AccountFacebook{Vars: vars}}
		}),
		newBeforeGameHook("facebookinstantgame", BeforeAuthenticateFacebookInstantGame, func(vars map[string]string) *api.AuthenticateFacebookInstantGameRequest {
			return &api.AuthenticateFacebookInstantGameRequest{Account: &api.AccountFacebookInstantGame{Vars: vars}}
		}),
		newBeforeGameHook("gamecenter", BeforeAuthenticateGameCenter, func(vars map[string]string) *api.AuthenticateGameCenterRequest {
			return &api.AuthenticateGameCenterRequest{Account: &api.AccountGameCenter{Vars: vars}}
		}),
		newBeforeGameHook("google", BeforeAuthenticateGoogle, func(vars map[string]string) *api.AuthenticateGoogleRequest {
			return &api.AuthenticateGoogleRequest{Account: &api.AccountGoogle{Vars: vars}}
		}),
		newBeforeGameHook("steam", BeforeAuthenticateSteam, func(vars map[string]string) *api.AuthenticateSteamRequest {
			return &api.AuthenticateSteamRequest{Account: &api.AccountSteam{Vars: vars}}
		}),
	}
	tests := []struct {
		name    string
		games   string
		vars    map[string]string
		wantErr error
	}{
		{name: "unset games no vars"},
		{name: "unset games explicit default", vars: map[string]string{"game": "cuvara"}},
		{name: "unset games denies foo", vars: map[string]string{"game": "foo"}, wantErr: gamectx.ErrGameNotAllowed},
		{name: "configured foo", games: "cuvara,foo", vars: map[string]string{"game": "foo", "other": "kept"}},
		{name: "invalid id", games: "cuvara,foo", vars: map[string]string{"game": "Bad.ID"}, wantErr: gamectx.ErrGameNotAllowed},
		{name: "default excluded", games: "foo", wantErr: gamectx.ErrGameNotAllowed},
		{name: "invalid config", games: "cuvara,BAD", vars: map[string]string{"game": "cuvara"}, wantErr: ErrInternal},
	}
	for _, hook := range hooks {
		t.Run(hook.name, func(t *testing.T) {
			for _, tt := range tests {
				t.Run(tt.name, func(t *testing.T) {
					ctx := hooksGameContext(tt.games)
					if tt.games == "" {
						// No runtime GAMES entry, and the process fallback is unset.
						ctx = context.Background()
					}
					if err := hook.call(ctx, tt.vars); !errors.Is(err, tt.wantErr) {
						t.Fatalf("hook error = %v, want %v", err, tt.wantErr)
					}
				})
			}
			for _, nilRequest := range []bool{true, false} {
				t.Run(fmt.Sprintf("malformed_nil_%v", nilRequest), func(t *testing.T) {
					var wantErr error
					if hook.name == "email" {
						wantErr = ErrInvalidEmail
					}
					if err := hook.malformed(hooksGameContext("foo"), nilRequest); !errors.Is(err, wantErr) {
						t.Fatalf("malformed request error = %v, want %v", err, wantErr)
					}
				})
			}
		})
	}
}

func TestBeforeAuthenticateEmail_GameAndCredentials(t *testing.T) {
	tests := []struct {
		name, game, email, password string
		wantErr                     error
	}{
		{name: "normalizes email", game: "foo", email: "Player@Example.COM", password: "sup3rsecret"},
		{name: "valid game invalid email", game: "foo", email: "bad", password: "sup3rsecret", wantErr: ErrInvalidEmail},
		{name: "valid game weak password", game: "foo", email: "player@example.com", password: "abc", wantErr: ErrWeakPassword},
		{name: "game checked before credentials", game: "bar", email: "bad", password: "abc", wantErr: gamectx.ErrGameNotAllowed},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			in := &api.AuthenticateEmailRequest{Account: &api.AccountEmail{
				Email: tt.email, Password: tt.password, Vars: map[string]string{"game": tt.game},
			}}
			out, err := BeforeAuthenticateEmail(hooksGameContext("cuvara,foo"), noopLogger{}, nil, nil, in)
			if !errors.Is(err, tt.wantErr) {
				t.Fatalf("error = %v, want %v", err, tt.wantErr)
			}
			if tt.wantErr == nil && (out != in || out.GetAccount().GetEmail() != "player@example.com" || out.GetAccount().GetVars()["game"] != "foo") {
				t.Fatalf("normalized request = %v", out)
			}
		})
	}
}

func TestBeforeSessionRefresh_Game(t *testing.T) {
	tests := []struct {
		name, games string
		in          *api.SessionRefreshRequest
		wantErr     error
	}{
		{name: "nil request", games: "foo"},
		{name: "nil vars preserve token game", games: "foo", in: &api.SessionRefreshRequest{Token: "refresh"}},
		{name: "empty vars preserve token game", games: "foo", in: &api.SessionRefreshRequest{Vars: map[string]string{}}},
		{name: "allowed replacement", games: "cuvara,foo", in: &api.SessionRefreshRequest{Vars: map[string]string{"game": "foo"}}},
		{name: "disallowed replacement", games: "cuvara", in: &api.SessionRefreshRequest{Vars: map[string]string{"game": "foo"}}, wantErr: gamectx.ErrGameNotAllowed},
		{name: "replacement without game selects default", games: "cuvara,foo", in: &api.SessionRefreshRequest{Vars: map[string]string{"other": "value"}}},
		{name: "replacement without game default excluded", games: "foo", in: &api.SessionRefreshRequest{Vars: map[string]string{"other": "value"}}, wantErr: gamectx.ErrGameNotAllowed},
		{name: "invalid game", games: "cuvara,foo", in: &api.SessionRefreshRequest{Vars: map[string]string{"game": "Bad.ID"}}, wantErr: gamectx.ErrGameNotAllowed},
		{name: "invalid config", games: "BAD", in: &api.SessionRefreshRequest{Vars: map[string]string{"game": "foo"}}, wantErr: ErrInternal},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			out, err := BeforeSessionRefresh(hooksGameContext(tt.games), noopLogger{}, nil, nil, tt.in)
			if !errors.Is(err, tt.wantErr) {
				t.Fatalf("error = %v, want %v", err, tt.wantErr)
			}
			if err == nil && out != tt.in {
				t.Fatal("refresh request was changed")
			}
		})
	}
}

// This models only the variables visible to the plugin. The tagged localstack
// test must prove that real Nakama carries those variables across a refresh.
func TestSessionRefresh_GameVarSeenByPlugin(t *testing.T) {
	for _, game := range []string{"cuvara", "foo"} {
		t.Run(game, func(t *testing.T) {
			ctx := context.WithValue(hooksGameContext("cuvara,foo"), runtime.RUNTIME_CTX_USER_ID, t.Name()) //nolint:staticcheck
			ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_ENV, map[string]string{                        //nolint:staticcheck
				"GAMES": "cuvara,foo", "JWT_SECRET": "refresh-game-test-secret",
			})
			vars := map[string]string{"game": game}
			before := context.WithValue(ctx, runtime.RUNTIME_CTX_VARS, vars) //nolint:staticcheck
			refresh := &api.SessionRefreshRequest{Token: "token-with-game-vars"}
			if out, err := BeforeSessionRefresh(ctx, noopLogger{}, nil, nil, refresh); err != nil || out != refresh {
				t.Fatalf("refresh hook = %v, %v", out, err)
			}
			// After refresh, an RPC context contains the token's vars again.
			after := context.WithValue(ctx, runtime.RUNTIME_CTX_VARS, map[string]string{"game": vars["game"]}) //nolint:staticcheck
			store := newMockStore()
			var gids []string
			for _, rpcCtx := range []context.Context{before, after} {
				output, err := gatewayToken(rpcCtx, noopLogger{}, store, "{}")
				if err != nil {
					t.Fatalf("gatewayToken: %v", err)
				}
				var response GatewayTokenResponse
				if err := json.Unmarshal([]byte(output), &response); err != nil {
					t.Fatalf("decode gateway response: %v", err)
				}
				claims, err := jwt.ParseUnverified(response.Token)
				if err != nil {
					t.Fatalf("parse token: %v", err)
				}
				gids = append(gids, claims.GameID)
			}
			want := game
			if game == "cuvara" {
				want = ""
			}
			if gids[0] != want || gids[1] != want {
				t.Fatalf("gid before/after = %q/%q, want %q", gids[0], gids[1], want)
			}
		})
	}
}

func TestAfterAuthenticateHooks_GameProfiles(t *testing.T) {
	for _, method := range []string{"device", "email"} {
		for _, tt := range []struct {
			name       string
			vars       map[string]string
			collection string
			wantErr    error
		}{
			{name: "default", collection: "player"},
			{name: "foo", vars: map[string]string{"game": "foo"}, collection: "foo.player"},
			{name: "denied", vars: map[string]string{"game": "bar"}, wantErr: gamectx.ErrGameNotAllowed},
		} {
			t.Run(method+"/"+tt.name, func(t *testing.T) {
				ctx := context.WithValue(hooksGameContext("cuvara,foo"), runtime.RUNTIME_CTX_USER_ID, t.Name()) //nolint:staticcheck
				// Request vars, rather than context vars, select the login game.
				ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_VARS, map[string]string{"game": "bar"}) //nolint:staticcheck
				nk := newMockNakama()
				var err error
				if method == "device" {
					err = AfterAuthenticateDevice(ctx, noopLogger{}, nil, nk, &api.Session{Created: true}, &api.AuthenticateDeviceRequest{Account: &api.AccountDevice{Vars: tt.vars}})
				} else {
					err = AfterAuthenticateEmail(ctx, noopLogger{}, nil, nk, &api.Session{Created: true}, &api.AuthenticateEmailRequest{Account: &api.AccountEmail{Vars: tt.vars}})
				}
				if !errors.Is(err, tt.wantErr) {
					t.Fatalf("hook error = %v, want %v", err, tt.wantErr)
				}
				if tt.wantErr != nil {
					if nk.readCalls != 0 || len(nk.writes) != 0 {
						t.Fatalf("rejected game touched storage: reads=%d writes=%d", nk.readCalls, len(nk.writes))
					}
					return
				}
				if len(nk.writes) != 1 || nk.writes[0].Collection != tt.collection || nk.writes[0].UserID != t.Name() {
					t.Fatalf("profile writes = %+v, want collection %s", nk.writes, tt.collection)
				}
			})
		}
	}
}
