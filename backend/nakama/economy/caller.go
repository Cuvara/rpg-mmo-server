package economy

import (
	"context"

	"github.com/heroiclabs/nakama-common/runtime"
)

// codePermissionDenied is the gRPC status code Nakama maps to HTTP 403.
const codePermissionDenied = 7

// ErrServerOnly is returned by every economy mutation RPC when the caller is a
// client session rather than the game server.
var ErrServerOnly = runtime.NewError("server-only rpc", codePermissionDenied)

// requireServerCaller enforces the server-to-server boundary on economy
// mutation RPCs (reward_kill, reward_kills, submit_kill).
//
// Nakama exposes every registered RPC to authenticated clients as well as to
// callers presenting runtime.http_key. The two are distinguishable only by the
// request context: a client-session call carries the caller's user id (and
// session id / expiry), a runtime.http_key call carries none of them
// (https://heroiclabs.com/docs/nakama/server-framework/introduction/). The
// mutation RPCs take the beneficiary user id from the payload, so a client
// reaching them could mint gold and score for anyone; this check must run
// BEFORE the payload is parsed or anything is granted.
//
// It rejects on any of the three session markers, not just the user id, so a
// future Nakama version populating them differently still fails closed.
func requireServerCaller(ctx context.Context) error {
	if v, ok := ctx.Value(runtime.RUNTIME_CTX_USER_ID).(string); ok && v != "" {
		return ErrServerOnly
	}
	if v, ok := ctx.Value(runtime.RUNTIME_CTX_SESSION_ID).(string); ok && v != "" {
		return ErrServerOnly
	}
	if v, ok := ctx.Value(runtime.RUNTIME_CTX_USER_SESSION_EXP).(int64); ok && v != 0 {
		return ErrServerOnly
	}
	return nil
}
