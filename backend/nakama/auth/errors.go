package auth

import "github.com/heroiclabs/nakama-common/runtime"

// gRPC status codes used for client-facing runtime errors.
const (
	codeInvalidArgument = 3
	codeInternal        = 13
	codeUnauthenticated = 16
)

// Client-facing errors returned by the auth RPCs and hooks.
var (
	// ErrUnauthenticated is returned when an RPC is called without a session.
	ErrUnauthenticated = runtime.NewError("unauthenticated", codeUnauthenticated)
	// ErrInvalidPayload is returned when an RPC payload cannot be decoded.
	ErrInvalidPayload = runtime.NewError("invalid payload", codeInvalidArgument)
	// ErrInternal is returned for unexpected server-side failures.
	ErrInternal = runtime.NewError("internal error", codeInternal)
	// ErrInvalidEmail is returned when the supplied email is malformed.
	ErrInvalidEmail = runtime.NewError("invalid email address", codeInvalidArgument)
	// ErrWeakPassword is returned when the supplied password is too short.
	ErrWeakPassword = runtime.NewError("password too short", codeInvalidArgument)
)
