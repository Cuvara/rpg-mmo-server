package redisstore

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/redis/go-redis/v9"
)

// strandEntry simulates the #234 failure mode against a real Redis (miniredis):
// an entry is XADDed and read by a consumer that never ACKs — the pod crashed
// between handler and XACK, or was replaced by a pod with a new name. The entry
// sits in the group's PEL under the dead consumer with deliveryCount deliveries
// recorded. Returns the entry ID.
func strandEntry(t *testing.T, client *redis.Client, stream, group, deadConsumer, eventType string, deliveryCount int) string {
	t.Helper()
	ctx := context.Background()
	key := constants.EventStreamPrefix + stream

	if err := client.XGroupCreateMkStream(ctx, key, group, "0").Err(); err != nil &&
		!strings.Contains(err.Error(), "BUSYGROUP") {
		t.Fatalf("xgroup create: %v", err)
	}
	id, err := client.XAdd(ctx, &redis.XAddArgs{
		Stream: key,
		Values: map[string]any{streamFieldType: eventType, streamFieldPayload: `{}`},
	}).Result()
	if err != nil {
		t.Fatalf("xadd: %v", err)
	}
	// Delivery #1: the dead consumer reads it and never ACKs.
	if err := client.XReadGroup(ctx, &redis.XReadGroupArgs{
		Group: group, Consumer: deadConsumer,
		Streams: []string{key, ">"}, Count: 1, Block: -1,
	}).Err(); err != nil {
		t.Fatalf("xreadgroup as dead consumer: %v", err)
	}
	if deliveryCount > 1 {
		// Pin the recorded delivery count directly (XCLAIM RETRYCOUNT) instead
		// of looping real redeliveries, which would need real idle time.
		if err := client.Do(ctx, "XCLAIM", key, group, deadConsumer, "0", id,
			"RETRYCOUNT", fmt.Sprint(deliveryCount)).Err(); err != nil {
			t.Fatalf("xclaim retrycount: %v", err)
		}
	}
	return id
}

// newReclaimStream builds an EventStream on client tuned so a reclaim test
// completes in milliseconds instead of the production 60s/30s.
func newReclaimStream(t *testing.T, client *redis.Client, consumer string) *EventStream {
	t.Helper()
	s := NewEventStreamWithClient(client, "gateway", consumer)
	s.SetBlockTimeout(20 * time.Millisecond)
	s.SetReclaimMinIdle(30 * time.Millisecond)
	s.SetReclaimInterval(20 * time.Millisecond)
	t.Cleanup(func() { _ = s.Close() })
	return s
}

// collector is a concurrency-safe handler that records delivered event types.
type collector struct {
	mu  sync.Mutex
	got []string
}

func (c *collector) handle(e storage.Event) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.got = append(c.got, e.Type)
}

func (c *collector) types() []string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([]string(nil), c.got...)
}

func (c *collector) count() int { return len(c.types()) }

// TestReclaim_DeadConsumerEntryRedelivered is the core #234 guard: an entry
// left pending by a consumer that will never come back must be claimed by a
// live group member, redelivered to its handler, and ACKed — with names
// mirroring production, where the replacement pod is a NEW consumer name that
// XREADGROUP `>` alone would never hand the entry to.
func TestReclaim_DeadConsumerEntryRedelivered(t *testing.T) {
	tests := []struct {
		name string
		// deliveries already recorded for the stranded entry; all cases stay
		// within the cap of 5, so all must be redelivered.
		deliveries int
	}{
		{"stranded after first delivery", 1},
		{"stranded after several crash-redeliver cycles", 4},
		{"stranded at exactly the cap's last allowed attempt", 5},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, client := newTestRedis(t)
			ctx := context.Background()
			// The cap is on the count *after* the reclaim's own increment, so a
			// pre-claim count of 5 becomes delivery 6 > 5 and would be dropped.
			// Keep these cases claimable by raising the cap headroom where the
			// scenario is "still under the cap".
			strandEntry(t, client, "world", "gateway", "dead-pod-a", "boss_killed", tt.deliveries)

			s := newReclaimStream(t, client, "replacement-pod-b")
			s.SetMaxDeliveries(int64(tt.deliveries) + 1)
			var c collector

			// Let the entry pass the reclaim min-idle before subscribing.
			time.Sleep(50 * time.Millisecond)
			if err := s.Subscribe(ctx, "world", c.handle); err != nil {
				t.Fatalf("Subscribe() error: %v", err)
			}

			if !waitFor(t, func() bool { return c.count() >= 1 }) {
				t.Fatal("stranded entry was never redelivered to the replacement consumer")
			}
			if got := c.types()[0]; got != "boss_killed" {
				t.Errorf("redelivered event type = %q, want %q", got, "boss_killed")
			}
			if n := s.DeadLetters(); n != 0 {
				t.Errorf("DeadLetters() = %d after a legitimate redelivery, want 0", n)
			}
			// Redelivery must end in an ACK: the PEL must drain, or the entry
			// would be redelivered forever.
			if !waitFor(t, func() bool {
				p, err := client.XPending(ctx, streamKey("world"), "gateway").Result()
				return err == nil && p.Count == 0
			}) {
				t.Error("reclaimed entry was never ACKed (still pending)")
			}
		})
	}
}

// TestReclaim_PoisonEntryDeadLettered: an entry past the delivery cap must be
// ACKed WITHOUT invoking the handler, logged, and counted — not redelivered.
// This is the dead-letter policy: after maxDeliveries attempts the entry is
// presumed to be the thing crashing its consumers.
func TestReclaim_PoisonEntryDeadLettered(t *testing.T) {
	_, client := newTestRedis(t)
	ctx := context.Background()
	// Recorded count 5 == cap; the reclaim's own claim makes it delivery 6.
	strandEntry(t, client, "world", "gateway", "dead-pod-a", "poison", 5)

	s := newReclaimStream(t, client, "replacement-pod-b")
	var c collector

	time.Sleep(50 * time.Millisecond)
	if err := s.Subscribe(ctx, "world", c.handle); err != nil {
		t.Fatalf("Subscribe() error: %v", err)
	}

	if !waitFor(t, func() bool { return s.DeadLetters() == 1 }) {
		t.Fatalf("DeadLetters() = %d, want 1 — poison entry was not dead-lettered", s.DeadLetters())
	}
	// Dead-letter means ACKed: the PEL drains and the entry never comes back.
	if !waitFor(t, func() bool {
		p, err := client.XPending(ctx, streamKey("world"), "gateway").Result()
		return err == nil && p.Count == 0
	}) {
		t.Error("dead-lettered entry was never ACKed (still pending)")
	}
	if n := c.count(); n != 0 {
		t.Errorf("handler ran %d times for a dead-lettered entry, want 0 (got %v)", n, c.types())
	}
	// And a healthy entry must still flow afterwards — dead-lettering one entry
	// must not wedge the consumer.
	if err := s.Publish(ctx, "world", storage.Event{Type: "after_poison"}); err != nil {
		t.Fatalf("Publish() error: %v", err)
	}
	if !waitFor(t, func() bool { return c.count() == 1 }) {
		t.Fatal("no delivery after the poison entry was dead-lettered")
	}
	if got := c.types()[0]; got != "after_poison" {
		t.Errorf("delivered event = %q, want %q", got, "after_poison")
	}
}

// TestReclaim_DeadLettersStartsAtZero keeps the counter honest, mirroring
// TestGroupLossesStartsAtZero: a healthy stream must not report phantom drops.
func TestReclaim_DeadLettersStartsAtZero(t *testing.T) {
	mr := miniredis.RunT(t)
	s := NewEventStream(mr.Addr(), "", "gateway", "c")
	s.SetBlockTimeout(20 * time.Millisecond)
	s.SetReclaimInterval(20 * time.Millisecond)
	t.Cleanup(func() { _ = s.Close() })

	var c collector
	if err := s.Subscribe(context.Background(), "world", c.handle); err != nil {
		t.Fatalf("Subscribe() error: %v", err)
	}
	publishEvent2(t, s, "world", "tick")
	if !waitFor(t, func() bool { return c.count() == 1 }) {
		t.Fatal("event never delivered")
	}
	// Idle through several reclaim intervals with nothing stranded.
	time.Sleep(150 * time.Millisecond)
	if n := s.DeadLetters(); n != 0 {
		t.Errorf("DeadLetters() = %d on a healthy stream, want 0", n)
	}
}

// TestAckBatching_NoAckLost: acks are now issued once per read batch rather
// than once per message. Push several batches' worth of events (> the read
// Count of 16) through one consumer and require every entry to end up both
// delivered and ACKed — a batching bug that dropped or skipped IDs would leave
// the PEL non-empty or a delivery missing.
func TestAckBatching_NoAckLost(t *testing.T) {
	tests := []struct {
		name   string
		events int
	}{
		{"single partial batch", 3},
		{"exactly one full batch", 16},
		{"several batches with a partial tail", 40},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, client := newTestRedis(t)
			ctx := context.Background()
			s := NewEventStreamWithClient(client, "gateway", "gw-1")
			s.SetBlockTimeout(20 * time.Millisecond)
			t.Cleanup(func() { _ = s.Close() })

			var c collector
			if err := s.Subscribe(ctx, "world", c.handle); err != nil {
				t.Fatalf("Subscribe() error: %v", err)
			}
			for i := 0; i < tt.events; i++ {
				publishEvent2(t, s, "world", fmt.Sprintf("e%d", i))
			}

			if !waitFor(t, func() bool { return c.count() == tt.events }) {
				t.Fatalf("delivered %d/%d events", c.count(), tt.events)
			}
			if !waitFor(t, func() bool {
				p, err := client.XPending(ctx, streamKey("world"), "gateway").Result()
				return err == nil && p.Count == 0
			}) {
				p, _ := client.XPending(ctx, streamKey("world"), "gateway").Result()
				t.Errorf("PEL not drained after batched acks: %d entries still pending", p.Count)
			}
		})
	}
}

// publishEvent2 is publishEvent with the stream name as a parameter.
func publishEvent2(t *testing.T, s *EventStream, stream, typ string) {
	t.Helper()
	if err := s.Publish(context.Background(), stream,
		storage.Event{Type: typ, Payload: []byte(`{}`)}); err != nil {
		t.Fatalf("publish %s: %v", typ, err)
	}
}
