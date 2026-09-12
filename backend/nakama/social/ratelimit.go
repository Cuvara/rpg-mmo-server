package social

import (
	"time"

	"github.com/duycuong/rpg-mmo/shared/ratelimit"
)

// Rate limits for the party mutation RPCs (party_create, party_join,
// party_leave), per authenticated user, sharing one bucket across all three.
//
// A legitimate client mutates party membership a handful of times a session:
// create or join once, leave once, plus a retry after a dropped connection.
// PartyWriteBurst 10 covers a player fiddling with the party UI; a sustained
// PartyWriteRatePerSec 0.5 (one every 2s) is far above any real use.
//
// One bucket for all three, not one each, because the abuse shape is a
// create/leave or join/leave loop, and per-RPC buckets would let a loop run at
// the sum of the limits. Each mutation is a read plus a MultiUpdate against
// the meta Postgres, and a create/leave loop is also a party-record churn
// loop — the cost is DB write throughput, which is the same scarce resource
// the batched reward RPC exists to protect (rpg-mmo-server#233).
//
// party_get is not limited here; see the note on PartyGetRPC for why (it has
// no user id to key on when the gateway calls it).
const (
	PartyWriteRatePerSec = 0.5
	PartyWriteBurst      = 10
	// PartyWriteIdleTTL is how long an idle user's bucket is kept. Longer than
	// the bucket's own refill time (PartyWriteBurst/PartyWriteRatePerSec =
	// 20s), so eviction never hands anyone a free reset.
	PartyWriteIdleTTL = 10 * time.Minute
)

// partyWriteLimiter limits party mutations per user id.
//
// Same multi-instance caveat as auth's token limiter: it lives in the Nakama
// process's memory, so N Nakama instances behind a load balancer give one user
// N x PartyWriteRatePerSec. Accepted for the MVP (single Nakama instance, per
// the deployment tiers in the root CLAUDE.md); the production upgrade is the
// same Redis-backed counter. Note that the limiter is NOT what protects the
// member cap — the storage version check is (see the package doc). This only
// bounds request rate.
//
// Package-level singleton for the same reason as auth's: Nakama constructs RPC
// handlers as plain functions with nowhere to hang per-plugin state, and
// InitModule runs once per process.
var partyWriteLimiter = ratelimit.NewLimiter(PartyWriteRatePerSec, PartyWriteBurst, PartyWriteIdleTTL)

func init() { partyWriteLimiter.StartCleanup(time.Minute) }

// allowPartyWrite reports whether userID may mutate party state right now.
func allowPartyWrite(userID string) bool { return partyWriteLimiter.Allow(userID) }
