package redisstore

import (
	"context"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/redis/go-redis/v9"
)

// newTrimTestStream is newTestStream plus the client, which the trim tests need
// in order to read XLEN back.
func newTrimTestStream(t *testing.T) (*EventStream, *redis.Client) {
	t.Helper()
	_, client := newTestRedis(t)
	s := NewEventStreamWithClient(client, "gateway", "gw-1")
	t.Cleanup(func() { _ = s.Close() })
	return s, client
}

// TestRedisEventStream_PublishTrimsStream is the #202 guard: Publish must carry
// MAXLEN so `events:*` cannot grow without bound against a noeviction Redis.
// Without the bound the pod is OOM-killed whole and takes sessions and the
// server registry with it, which is the failure ADR-4's noeviction was chosen to
// avoid.
//
// The assertion is an upper bound rather than an equality on purpose. Publish
// uses the approximate form (`MAXLEN ~ N`), so a real Redis may retain somewhat
// more than N — it stops trimming at a radix-tree node boundary. miniredis,
// which backs these tests, implements the trim exactly, so `<=` holds on both
// and does not encode miniredis' stricter behaviour as the contract.
func TestRedisEventStream_PublishTrimsStream(t *testing.T) {
	tests := []struct {
		name string
		// maxLen 0 means "leave the production default in place".
		maxLen     int64
		publish    int
		wantAtMost int64
		wantTrim   bool
	}{
		{
			// Guards the default from the opposite mistake: a bound so tight
			// that ordinary traffic is trimmed before a consumer can read it.
			name:       "default bound retains a small burst untouched",
			publish:    25,
			wantAtMost: 25,
		},
		{
			name:       "bound smaller than the burst trims the excess",
			maxLen:     50,
			publish:    200,
			wantAtMost: 50,
			wantTrim:   true,
		},
		{
			name:       "bound of one keeps only the newest entry",
			maxLen:     1,
			publish:    10,
			wantAtMost: 1,
			wantTrim:   true,
		},
		{
			// SetMaxLen must not be usable as an off switch: a non-positive
			// value keeps the default rather than removing the bound.
			name:       "non-positive bound falls back to the default, not to unbounded",
			maxLen:     -1,
			publish:    25,
			wantAtMost: 25,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			s, client := newTrimTestStream(t)
			ctx := context.Background()
			if tt.maxLen != 0 {
				s.SetMaxLen(tt.maxLen)
			}

			for i := 0; i < tt.publish; i++ {
				if err := s.Publish(ctx, "world", storage.Event{
					Type:    "entity_killed",
					Payload: []byte(`{"entity":"mob_1"}`),
				}); err != nil {
					t.Fatalf("Publish() #%d error: %v", i, err)
				}
			}

			got, err := client.XLen(ctx, constants.EventStreamPrefix+"world").Result()
			if err != nil {
				t.Fatalf("XLen() error: %v", err)
			}
			if got > tt.wantAtMost {
				t.Errorf("XLen() = %d, want at most %d", got, tt.wantAtMost)
			}
			if tt.wantTrim && got >= int64(tt.publish) {
				t.Errorf("XLen() = %d after %d publishes: nothing was trimmed", got, tt.publish)
			}
			if !tt.wantTrim && got != int64(tt.publish) {
				t.Errorf("XLen() = %d, want all %d entries retained", got, tt.publish)
			}
		})
	}
}

// TestRedisEventStream_DefaultMaxLenApplied pins the bound a stream publishes
// with when nobody calls SetMaxLen. Exercising the real 30_000-entry default
// through miniredis would mean 30_000 round trips per assertion, so the
// resolved value is checked directly instead; the trimming behaviour itself is
// covered above.
func TestRedisEventStream_DefaultMaxLenApplied(t *testing.T) {
	tests := []struct {
		name string
		set  int64
		want int64
	}{
		{"unset uses the production default", 0, DefaultStreamMaxLen},
		{"zero is ignored", 0, DefaultStreamMaxLen},
		{"negative is ignored", -100, DefaultStreamMaxLen},
		{"positive overrides", 500, 500},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			s, _ := newTrimTestStream(t)
			if tt.set != 0 {
				s.SetMaxLen(tt.set)
			}
			if got := s.maxLenOrDefault(); got != tt.want {
				t.Errorf("maxLenOrDefault() = %d, want %d", got, tt.want)
			}
		})
	}
}

// TestRedisEventStream_TrimKeepsNewest proves the trim drops the OLDEST
// entries. A bound that discarded the newest would silently invert delivery
// order under load and is worth an explicit guard, since XLEN alone cannot tell
// the two apart.
func TestRedisEventStream_TrimKeepsNewest(t *testing.T) {
	s, client := newTrimTestStream(t)
	ctx := context.Background()
	s.SetMaxLen(3)

	for i, typ := range []string{"e0", "e1", "e2", "e3", "e4"} {
		if err := s.Publish(ctx, "world", storage.Event{Type: typ}); err != nil {
			t.Fatalf("Publish() #%d error: %v", i, err)
		}
	}

	entries, err := client.XRange(ctx, constants.EventStreamPrefix+"world", "-", "+").Result()
	if err != nil {
		t.Fatalf("XRange() error: %v", err)
	}
	if len(entries) > 3 {
		t.Fatalf("XRange() returned %d entries, want at most 3", len(entries))
	}
	// The newest publish must always survive; the oldest must not.
	last := entries[len(entries)-1].Values[streamFieldType]
	if last != "e4" {
		t.Errorf("newest retained entry = %v, want %q", last, "e4")
	}
	for _, e := range entries {
		if e.Values[streamFieldType] == "e0" {
			t.Errorf("oldest entry e0 survived a MAXLEN 3 trim over 5 publishes")
		}
	}
}
