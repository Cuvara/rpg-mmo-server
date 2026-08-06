package ratelimit

import (
	"sync"
	"testing"
	"time"
)

// base is a fixed instant; every test drives time explicitly through AllowAt /
// Cleanup rather than sleeping, so the suite is deterministic and fast.
var base = time.Date(2026, 8, 6, 12, 0, 0, 0, time.UTC)

func TestBucketAllowAt(t *testing.T) {
	tests := []struct {
		name  string
		rate  float64
		burst float64
		// calls are (offset from base, expected verdict) pairs.
		calls []struct {
			at   time.Duration
			want bool
		}
	}{
		{
			name: "burst is admitted in full", rate: 1, burst: 3,
			calls: []struct {
				at   time.Duration
				want bool
			}{{0, true}, {0, true}, {0, true}, {0, false}},
		},
		{
			name: "sustained traffic above rate is blocked", rate: 10, burst: 2,
			calls: []struct {
				at   time.Duration
				want bool
			}{
				{0, true}, {0, true}, {0, false},
				// 10ms later only 0.1 tokens have accrued: still blocked.
				{10 * time.Millisecond, false},
			},
		},
		{
			name: "refill over time restores capacity", rate: 10, burst: 2,
			calls: []struct {
				at   time.Duration
				want bool
			}{
				{0, true}, {0, true}, {0, false},
				{100 * time.Millisecond, true},  // +1 token
				{100 * time.Millisecond, false}, // spent again
				{300 * time.Millisecond, true},  // +2 tokens, capped at burst
				{300 * time.Millisecond, true},
				{300 * time.Millisecond, false},
			},
		},
		{
			name: "refill is capped at burst", rate: 1, burst: 2,
			calls: []struct {
				at   time.Duration
				want bool
			}{
				// One hour of idling must not bank 3600 tokens.
				{time.Hour, true}, {time.Hour, true}, {time.Hour, false},
			},
		},
		{
			name: "zero rate disables limiting", rate: 0, burst: 1,
			calls: []struct {
				at   time.Duration
				want bool
			}{{0, true}, {0, true}, {0, true}, {0, true}},
		},
		{
			name: "burst below one is clamped to one", rate: 1, burst: 0,
			calls: []struct {
				at   time.Duration
				want bool
			}{{0, true}, {0, false}},
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			b := NewBucket(tt.rate, tt.burst)
			for i, c := range tt.calls {
				if got := b.AllowAt(base.Add(c.at)); got != c.want {
					t.Errorf("call %d at +%v: AllowAt() = %v, want %v", i, c.at, got, c.want)
				}
			}
		})
	}
}

func TestBucketZeroValueIsUnlimited(t *testing.T) {
	var b Bucket
	for i := 0; i < 100; i++ {
		if !b.Allow() {
			t.Fatalf("zero-value Bucket blocked call %d; the zero value must not limit", i)
		}
	}
}

func TestLimiterPerKeyIsolation(t *testing.T) {
	l := NewLimiter(1, 2, time.Minute)

	// "a" burns its whole burst.
	for i := 0; i < 2; i++ {
		if !l.AllowAt("a", base) {
			t.Fatalf("a: call %d should be allowed within burst", i)
		}
	}
	if l.AllowAt("a", base) {
		t.Error("a: third call should be blocked")
	}

	// "b" must be completely unaffected: one flooding IP cannot lock out others.
	for i := 0; i < 2; i++ {
		if !l.AllowAt("b", base) {
			t.Fatalf("b: call %d should be allowed — keys must be isolated", i)
		}
	}
	if l.AllowAt("b", base) {
		t.Error("b: third call should be blocked")
	}
	if got := l.Len(); got != 2 {
		t.Errorf("Len() = %d, want 2", got)
	}
}

func TestLimiterCleanup(t *testing.T) {
	const ttl = 5 * time.Minute
	l := NewLimiter(1, 2, ttl)

	l.AllowAt("stale", base)
	l.AllowAt("fresh", base.Add(4*time.Minute))
	if got := l.Len(); got != 2 {
		t.Fatalf("Len() before cleanup = %d, want 2", got)
	}

	// At base+6m, "stale" is 6m idle (> ttl) and "fresh" is 2m idle.
	if removed := l.Cleanup(base.Add(6 * time.Minute)); removed != 1 {
		t.Errorf("Cleanup() removed %d, want 1", removed)
	}
	if got := l.Len(); got != 1 {
		t.Errorf("Len() after cleanup = %d, want 1", got)
	}

	// Nothing is stale yet at the same instant, so a second sweep is a no-op.
	if removed := l.Cleanup(base.Add(6 * time.Minute)); removed != 0 {
		t.Errorf("second Cleanup() removed %d, want 0", removed)
	}
}

func TestLimiterCleanupDoesNotGrantFreeReset(t *testing.T) {
	const ttl = time.Minute
	l := NewLimiter(1, 2, ttl)

	l.AllowAt("ip", base)
	l.AllowAt("ip", base)
	if l.AllowAt("ip", base) {
		t.Fatal("third call should be blocked")
	}

	// Evict, then come back. The bucket is recreated full — but that is exactly
	// what an un-evicted bucket would have refilled to after 2m at rate 1/s, so
	// eviction cannot be used to bypass the limit.
	l.Cleanup(base.Add(2 * time.Minute))
	if got := l.Len(); got != 0 {
		t.Fatalf("Len() after cleanup = %d, want 0", got)
	}
	if !l.AllowAt("ip", base.Add(2*time.Minute)) {
		t.Error("call after a full refill window should be allowed")
	}
}

func TestNilLimiterAllowsEverything(t *testing.T) {
	var l *Limiter
	if !l.Allow("anything") {
		t.Error("nil *Limiter must allow: callers rely on it as 'limiting disabled'")
	}
	if l.Enabled() {
		t.Error("nil *Limiter must report Enabled() == false")
	}
	if l.Len() != 0 {
		t.Error("nil *Limiter Len() must be 0")
	}
	if l.Cleanup(base) != 0 {
		t.Error("nil *Limiter Cleanup() must be a no-op")
	}
	l.Stop()          // must not panic
	l.StartCleanup(0) // must not panic or spawn anything
}

func TestDisabledLimiterAllowsEverything(t *testing.T) {
	l := NewLimiter(0, 1, time.Minute)
	for i := 0; i < 100; i++ {
		if !l.Allow("k") {
			t.Fatalf("rate 0 must disable limiting (call %d blocked)", i)
		}
	}
	if l.Len() != 0 {
		t.Error("a disabled limiter must not allocate buckets")
	}
}

func TestLimiterConcurrentAccess(t *testing.T) {
	// The limiter is shared across the gateway's accept loop and its cleanup
	// goroutine, so concurrent Allow/Cleanup must be safe. Under -race (CI)
	// this is the test that would catch a missing lock.
	l := NewLimiter(1000, 1000, time.Minute)
	var wg sync.WaitGroup
	for i := 0; i < 8; i++ {
		wg.Add(1)
		go func(n int) {
			defer wg.Done()
			for j := 0; j < 200; j++ {
				l.Allow("key")
				l.Allow(string(rune('a' + n)))
			}
		}(i)
	}
	wg.Add(1)
	go func() {
		defer wg.Done()
		for j := 0; j < 50; j++ {
			l.Cleanup(time.Now())
		}
	}()
	wg.Wait()
}

func TestStopIsIdempotent(t *testing.T) {
	l := NewLimiter(1, 1, time.Minute)
	l.StartCleanup(time.Millisecond)
	l.Stop()
	l.Stop() // must not panic on a double close
}

func BenchmarkBucketAllow(b *testing.B) {
	// Guards the "no allocation in the hot path" claim: this runs once per
	// inbound gateway frame.
	bucket := NewBucket(1e9, 1e9)
	now := base
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		bucket.AllowAt(now)
	}
}
