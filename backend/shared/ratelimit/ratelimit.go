// Package ratelimit is the shared token-bucket rate limiter used on every
// abuse-facing entry point: gateway connection accepts, gateway per-connection
// message rates, and Nakama client-facing RPCs.
//
// Two types, deliberately separated by allocation profile:
//
//	Bucket  — a single token bucket, a plain struct with no lock and no map.
//	          Embed it by value in whatever object already has a lifetime
//	          (a connection, a session). Allow() does no allocation at all,
//	          which is what makes it safe in a per-message hot path.
//	Limiter — a keyed set of buckets (per IP, per user id) behind one mutex,
//	          with TTL eviction so an attacker cycling keys cannot grow the map
//	          without bound.
//
// # Semantics
//
// A bucket holds up to Burst tokens and refills at Rate tokens per second.
// Allow() consumes one token and reports whether one was available. That gives
// exactly the behaviour an abuse control wants: a short burst is admitted in
// full (clients legitimately reconnect a few times in a row, and a client
// legitimately sends a handshake pair back to back), while a sustained rate
// above Rate is throttled to Rate.
//
// # Scope caveat
//
// This limiter is per process and in memory. Two gateway replicas each admit
// Rate connections per IP, so the effective cluster limit is
// Rate × replicas. That is accepted for the MVP: the goal is to stop a single
// host from trivially exhausting a single process, not to enforce a global
// quota. The production upgrade is a Redis-backed counter (INCR + EXPIRE, or a
// Lua token bucket) keyed the same way — see shared/docs/DESIGN.md.
package ratelimit

import (
	"sync"
	"time"
)

// Bucket is a single token bucket. The zero value is an *unlimited* bucket
// (Rate 0 means "no limiting"), which makes "feature disabled" the natural
// default for an unconfigured service.
//
// A Bucket is NOT safe for concurrent use. Embed it in an object that is
// already owned by one goroutine (e.g. a connection's read loop) or guard it
// externally.
type Bucket struct {
	// Rate is the sustained refill rate in tokens per second. Zero or negative
	// disables limiting entirely (Allow always returns true).
	Rate float64
	// Burst is the bucket capacity: the largest instantaneous burst admitted.
	// Values below 1 are treated as 1, otherwise no request could ever pass.
	Burst float64

	tokens float64
	last   time.Time
}

// NewBucket returns a full bucket with the given sustained rate and capacity.
func NewBucket(rate, burst float64) Bucket {
	if burst < 1 {
		burst = 1
	}
	return Bucket{Rate: rate, Burst: burst, tokens: burst}
}

// Enabled reports whether the bucket actually limits anything.
func (b *Bucket) Enabled() bool { return b.Rate > 0 }

// Allow consumes one token at the current time.
func (b *Bucket) Allow() bool { return b.AllowAt(time.Now()) }

// AllowAt consumes one token at an explicit instant. Tests drive time through
// this method instead of sleeping.
//
// No allocation, no lock, three float ops — safe to call per inbound message.
func (b *Bucket) AllowAt(now time.Time) bool {
	if b.Rate <= 0 {
		return true
	}
	if b.Burst < 1 {
		b.Burst = 1
	}
	if b.last.IsZero() {
		// First use: start full so a fresh connection is never penalised for
		// the handshake it must send immediately.
		b.tokens = b.Burst
		b.last = now
	}
	if elapsed := now.Sub(b.last); elapsed > 0 {
		b.tokens += elapsed.Seconds() * b.Rate
		if b.tokens > b.Burst {
			b.tokens = b.Burst
		}
		b.last = now
	}
	if b.tokens < 1 {
		return false
	}
	b.tokens--
	return true
}

// Limiter is a keyed set of buckets: one bucket per IP, user id, or whatever
// string identifies the caller. Safe for concurrent use.
//
// A nil *Limiter allows everything, so callers can treat "limiting disabled"
// and "no limiter configured" identically without a nil check at each call.
type Limiter struct {
	rate  float64
	burst float64
	// idleTTL is how long an untouched bucket is kept before Cleanup evicts it.
	// It must exceed the refill time of a full bucket (burst/rate), otherwise
	// eviction would hand an attacker a free reset.
	idleTTL time.Duration

	mu      sync.Mutex
	buckets map[string]*entry

	stop chan struct{}
	once sync.Once
}

type entry struct {
	bucket Bucket
	seen   time.Time
}

// DefaultIdleTTL is the fallback eviction age when NewLimiter is given a
// non-positive TTL: 10 minutes, comfortably longer than any bucket configured
// here takes to refill.
const DefaultIdleTTL = 10 * time.Minute

// NewLimiter builds a keyed limiter admitting `burst` requests instantly and
// `rate` requests per second sustained, per key. A non-positive rate returns a
// limiter that allows everything.
func NewLimiter(rate, burst float64, idleTTL time.Duration) *Limiter {
	if idleTTL <= 0 {
		idleTTL = DefaultIdleTTL
	}
	if burst < 1 {
		burst = 1
	}
	return &Limiter{
		rate:    rate,
		burst:   burst,
		idleTTL: idleTTL,
		buckets: make(map[string]*entry),
		stop:    make(chan struct{}),
	}
}

// Enabled reports whether the limiter actually limits anything.
func (l *Limiter) Enabled() bool { return l != nil && l.rate > 0 }

// Allow consumes one token from key's bucket at the current time.
func (l *Limiter) Allow(key string) bool { return l.AllowAt(key, time.Now()) }

// AllowAt consumes one token from key's bucket at an explicit instant.
func (l *Limiter) AllowAt(key string, now time.Time) bool {
	if !l.Enabled() {
		return true
	}
	l.mu.Lock()
	defer l.mu.Unlock()

	e, ok := l.buckets[key]
	if !ok {
		b := NewBucket(l.rate, l.burst)
		e = &entry{bucket: b}
		l.buckets[key] = e
	}
	e.seen = now
	return e.bucket.AllowAt(now)
}

// Len returns how many keys are currently tracked. Exported for tests and for
// a future gauge metric on limiter memory.
func (l *Limiter) Len() int {
	if l == nil {
		return 0
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	return len(l.buckets)
}

// Cleanup evicts buckets untouched for longer than the idle TTL and returns how
// many were removed.
//
// Eviction is safe only because an idle bucket has necessarily refilled to
// full: dropping it and recreating it on the next request yields the same
// verdict, so an attacker gains nothing by pausing.
func (l *Limiter) Cleanup(now time.Time) int {
	if l == nil {
		return 0
	}
	l.mu.Lock()
	defer l.mu.Unlock()
	removed := 0
	for k, e := range l.buckets {
		if now.Sub(e.seen) > l.idleTTL {
			delete(l.buckets, k)
			removed++
		}
	}
	return removed
}

// StartCleanup runs Cleanup on a ticker until Stop is called. Callers that
// already own a goroutine can call Cleanup directly instead.
func (l *Limiter) StartCleanup(every time.Duration) {
	if !l.Enabled() {
		return
	}
	if every <= 0 {
		every = time.Minute
	}
	go func() {
		t := time.NewTicker(every)
		defer t.Stop()
		for {
			select {
			case <-t.C:
				l.Cleanup(time.Now())
			case <-l.stop:
				return
			}
		}
	}()
}

// Stop halts the background cleanup goroutine. Safe to call more than once,
// and safe on a limiter whose cleanup was never started.
func (l *Limiter) Stop() {
	if l == nil {
		return
	}
	l.once.Do(func() { close(l.stop) })
}
