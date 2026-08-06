package auth

import (
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/ratelimit"
)

// TestGatewayTokenLimiterShape checks the configured limits behave the way the
// RPC needs: a reconnect burst passes, a scripted loop does not, and users are
// isolated from each other.
func TestGatewayTokenLimiterShape(t *testing.T) {
	base := time.Date(2026, 8, 6, 12, 0, 0, 0, time.UTC)

	tests := []struct {
		name string
		// calls are (user, offset) pairs with the expected verdict.
		calls []struct {
			user string
			at   time.Duration
			want bool
		}
	}{
		{
			name: "reconnect burst is admitted",
			calls: []struct {
				user string
				at   time.Duration
				want bool
			}{
				{"u1", 0, true}, {"u1", 0, true}, {"u1", 0, true},
				{"u1", 0, true}, {"u1", 0, true},
			},
		},
		{
			name: "sustained loop is blocked past the burst",
			calls: []struct {
				user string
				at   time.Duration
				want bool
			}{
				{"u1", 0, true}, {"u1", 0, true}, {"u1", 0, true},
				{"u1", 0, true}, {"u1", 0, true},
				{"u1", 0, false}, {"u1", time.Second, false},
			},
		},
		{
			name: "tokens refill over time",
			calls: []struct {
				user string
				at   time.Duration
				want bool
			}{
				{"u1", 0, true}, {"u1", 0, true}, {"u1", 0, true},
				{"u1", 0, true}, {"u1", 0, true}, {"u1", 0, false},
				// At 0.2/s one token takes 5s.
				{"u1", 5 * time.Second, true},
				{"u1", 5 * time.Second, false},
			},
		},
		{
			name: "users are isolated",
			calls: []struct {
				user string
				at   time.Duration
				want bool
			}{
				{"u1", 0, true}, {"u1", 0, true}, {"u1", 0, true},
				{"u1", 0, true}, {"u1", 0, true}, {"u1", 0, false},
				// One abusive account must not lock out everyone else.
				{"u2", 0, true}, {"u2", 0, true},
			},
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			// A fresh limiter per case, configured exactly like the package
			// singleton, so tests never depend on each other's token state.
			l := ratelimit.NewLimiter(TokenRatePerSec, TokenBurst, TokenIdleTTL)
			for i, c := range tt.calls {
				if got := l.AllowAt(c.user, base.Add(c.at)); got != c.want {
					t.Errorf("call %d (%s at +%v): Allow = %v, want %v", i, c.user, c.at, got, c.want)
				}
			}
		})
	}
}

// TestTokenIdleTTLExceedsRefill guards the eviction invariant: a bucket must
// never be evicted before it would have refilled on its own, otherwise eviction
// itself becomes a bypass.
func TestTokenIdleTTLExceedsRefill(t *testing.T) {
	refill := time.Duration(float64(time.Second) * TokenBurst / TokenRatePerSec)
	if TokenIdleTTL <= refill {
		t.Errorf("TokenIdleTTL (%v) must exceed the full-refill time (%v)", TokenIdleTTL, refill)
	}
}

func TestErrRateLimited(t *testing.T) {
	if ErrRateLimited == nil {
		t.Fatal("ErrRateLimited must be defined")
	}
	// Pins the gRPC code clients switch on.
	if got := ErrRateLimited.Error(); got != "rate limited" {
		t.Errorf("ErrRateLimited message = %q, want %q", got, "rate limited")
	}
	if codeResourceExhausted != 8 {
		t.Errorf("codeResourceExhausted = %d, want 8 (gRPC RESOURCE_EXHAUSTED)", codeResourceExhausted)
	}
}

// TestAllowGatewayTokenSingleton smoke-tests the real package singleton the RPC
// uses, on a user id no other test touches.
func TestAllowGatewayTokenSingleton(t *testing.T) {
	const user = "singleton-probe-user"
	for i := 0; i < TokenBurst; i++ {
		if !allowGatewayToken(user) {
			t.Fatalf("call %d within burst should be allowed", i)
		}
	}
	if allowGatewayToken(user) {
		t.Error("call past the burst should be blocked")
	}
}
