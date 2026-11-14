package auth

import (
	"context"
	"database/sql"
	"errors"

	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// validateAuthGame keeps invalid runtime configuration out of client errors.
func validateAuthGame(ctx context.Context, logger runtime.Logger, vars map[string]string) error {
	_, err := gamectx.GameFromVars(ctx, vars)
	if errors.Is(err, gamectx.ErrGamesConfig) {
		logger.Error("before authenticate/refresh: invalid GAMES configuration: %v", err)
		return ErrInternal
	}
	return err
}

// BeforeAuthenticateApple validates the requested session game.
func BeforeAuthenticateApple(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.AuthenticateAppleRequest) (*api.AuthenticateAppleRequest, error) {
	if in == nil || in.GetAccount() == nil {
		return in, nil
	}
	if err := validateAuthGame(ctx, logger, in.GetAccount().GetVars()); err != nil {
		return nil, err
	}
	return in, nil
}

// BeforeAuthenticateCustom validates the requested session game.
func BeforeAuthenticateCustom(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.AuthenticateCustomRequest) (*api.AuthenticateCustomRequest, error) {
	if in == nil || in.GetAccount() == nil {
		return in, nil
	}
	if err := validateAuthGame(ctx, logger, in.GetAccount().GetVars()); err != nil {
		return nil, err
	}
	return in, nil
}

// BeforeAuthenticateDevice validates the requested session game.
func BeforeAuthenticateDevice(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.AuthenticateDeviceRequest) (*api.AuthenticateDeviceRequest, error) {
	if in == nil || in.GetAccount() == nil {
		return in, nil
	}
	if err := validateAuthGame(ctx, logger, in.GetAccount().GetVars()); err != nil {
		return nil, err
	}
	return in, nil
}

// BeforeAuthenticateFacebook validates the requested session game.
func BeforeAuthenticateFacebook(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.AuthenticateFacebookRequest) (*api.AuthenticateFacebookRequest, error) {
	if in == nil || in.GetAccount() == nil {
		return in, nil
	}
	if err := validateAuthGame(ctx, logger, in.GetAccount().GetVars()); err != nil {
		return nil, err
	}
	return in, nil
}

// BeforeAuthenticateFacebookInstantGame validates the requested session game.
func BeforeAuthenticateFacebookInstantGame(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.AuthenticateFacebookInstantGameRequest) (*api.AuthenticateFacebookInstantGameRequest, error) {
	if in == nil || in.GetAccount() == nil {
		return in, nil
	}
	if err := validateAuthGame(ctx, logger, in.GetAccount().GetVars()); err != nil {
		return nil, err
	}
	return in, nil
}

// BeforeAuthenticateGameCenter validates the requested session game.
func BeforeAuthenticateGameCenter(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.AuthenticateGameCenterRequest) (*api.AuthenticateGameCenterRequest, error) {
	if in == nil || in.GetAccount() == nil {
		return in, nil
	}
	if err := validateAuthGame(ctx, logger, in.GetAccount().GetVars()); err != nil {
		return nil, err
	}
	return in, nil
}

// BeforeAuthenticateGoogle validates the requested session game.
func BeforeAuthenticateGoogle(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.AuthenticateGoogleRequest) (*api.AuthenticateGoogleRequest, error) {
	if in == nil || in.GetAccount() == nil {
		return in, nil
	}
	if err := validateAuthGame(ctx, logger, in.GetAccount().GetVars()); err != nil {
		return nil, err
	}
	return in, nil
}

// BeforeAuthenticateSteam validates the requested session game.
func BeforeAuthenticateSteam(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.AuthenticateSteamRequest) (*api.AuthenticateSteamRequest, error) {
	if in == nil || in.GetAccount() == nil {
		return in, nil
	}
	if err := validateAuthGame(ctx, logger, in.GetAccount().GetVars()); err != nil {
		return nil, err
	}
	return in, nil
}

// BeforeSessionRefresh validates replacement session variables. An omitted or
// empty vars keeps the refresh token's authenticated variables: over JSON and
// protobuf an empty map cannot be told apart from an absent one, and real Nakama
// 3.40.0 keeps the token's vars for both (proven by
// TestSessionRefresh_GameVarLocalStack, 2026-11-14). A non-empty vars map
// replaces them and is validated like a login. A non-nil but empty map still
// validates the default game. Whether Nakama hands the hook nil or an empty map
// for an empty vars object is not established; either way the session keeps its
// game, so the check can only refuse (never switch games), and only when the
// default game is missing from GAMES.
func BeforeSessionRefresh(ctx context.Context, logger runtime.Logger, _ *sql.DB, _ runtime.NakamaModule, in *api.SessionRefreshRequest) (*api.SessionRefreshRequest, error) {
	if in == nil || in.Vars == nil {
		return in, nil
	}
	if err := validateAuthGame(ctx, logger, in.GetVars()); err != nil {
		return nil, err
	}
	return in, nil
}
