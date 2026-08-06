package auth

import (
	"time"

	"github.com/duycuong/rpg-mmo/shared/ratelimit"
	"github.com/heroiclabs/nakama-common/runtime"
)

// codeResourceExhausted is the gRPC status code for a rate-limited call.
const codeResourceExhausted = 8

// ErrRateLimited is returned when a caller exceeds an RPC's rate limit.
var ErrRateLimited = runtime.NewError("rate limited", codeResourceExhausted)

// Rate limits for the gateway_token RPC, per authenticated user.
//
// A legitimate client calls gateway_token once per realtime connection: at
// login, and again after a disconnect. TokenBurst 5 covers a flapping mobile
// link reconnecting a few times in a row; TokenRatePerSec 0.2 (one call every
// 5s sustained) is far above any real client's need and far below what a
// scripted loop would want.
//
// The RPC is cheap (one HMAC), so the limit is not about CPU. It is about the
// token itself: an unbounded gateway_token loop is a free oracle for minting
// valid realtime credentials, which is the raw material for a connection flood
// against the gateway.
const (
	TokenRatePerSec = 0.2
	TokenBurst      = 5
	// TokenIdleTTL is how long an idle user's bucket is kept. Longer than the
	// bucket's own refill time (TokenBurst/TokenRatePerSec = 25s), so eviction
	// never hands anyone a free reset.
	TokenIdleTTL = 10 * time.Minute
)

// tokenLimiter rate-limits gateway_token per user id.
//
// IMPORTANT — multi-instance caveat: this limiter lives in the Nakama process's
// memory. With N Nakama instances behind a load balancer a single user gets
// N × TokenRatePerSec, because each instance keeps its own buckets and nothing
// synchronises them. That is accepted for the MVP (single Nakama instance, per
// the deployment tiers in the root CLAUDE.md) and is documented in
// nakama/docs/DESIGN.md. The production upgrade is a Redis-backed counter
// (INCR + EXPIRE keyed `ratelimit:gateway_token:{user_id}`), which is the same
// Redis the gateway already depends on.
//
// It is a package-level singleton because Nakama constructs RPC handlers as
// plain functions with no place to hang per-plugin state; InitModule runs once
// per process, so a package var has exactly the process lifetime we want.
var tokenLimiter = ratelimit.NewLimiter(TokenRatePerSec, TokenBurst, TokenIdleTTL)

func init() { tokenLimiter.StartCleanup(time.Minute) }

// allowGatewayToken reports whether userID may call gateway_token right now.
func allowGatewayToken(userID string) bool { return tokenLimiter.Allow(userID) }
