package character

import (
	"time"

	"github.com/duycuong/rpg-mmo/shared/ratelimit"
)

// Rate limits for the roster RPCs, per authenticated user.
//
// These are operational limits (they protect meta-DB write throughput), not
// game rules. A real client creates or deletes a character a handful of times
// per session at most: RosterWriteBurst 5 covers a player fiddling with the
// character screen, and a sustained RosterWriteRatePerSec 0.2 (one every 5 s)
// is far above real use. character_create and character_delete share ONE
// bucket, for the same reason the party RPCs do: the abuse shape is a
// create/delete loop, and per-RPC buckets would let it run at the sum.
//
// character_list is read-only and cheaper, but still a storage read per call,
// so it gets its own, looser bucket instead of none.
const (
	RosterWriteRatePerSec = 0.2
	RosterWriteBurst      = 5
	RosterReadRatePerSec  = 1
	RosterReadBurst       = 10
	// RosterIdleTTL is how long an idle user's buckets are kept. Longer than
	// either bucket's refill time (25 s and 10 s), so eviction never hands
	// anyone a free reset.
	RosterIdleTTL = 10 * time.Minute
)

// Package-level singletons for the same reason as the auth and party limiters:
// Nakama builds RPC handlers as plain functions with nowhere to hang state,
// and InitModule runs once per process. Same multi-instance caveat too: each
// Nakama instance keeps its own buckets (accepted for the single-instance MVP).
var (
	rosterWriteLimiter = ratelimit.NewLimiter(RosterWriteRatePerSec, RosterWriteBurst, RosterIdleTTL)
	rosterReadLimiter  = ratelimit.NewLimiter(RosterReadRatePerSec, RosterReadBurst, RosterIdleTTL)
)

func init() {
	rosterWriteLimiter.StartCleanup(time.Minute)
	rosterReadLimiter.StartCleanup(time.Minute)
}

// allowRosterWrite reports whether userID may create or delete a character now.
func allowRosterWrite(userID string) bool { return rosterWriteLimiter.Allow(userID) }

// allowRosterRead reports whether userID may list their roster now.
func allowRosterRead(userID string) bool { return rosterReadLimiter.Allow(userID) }
