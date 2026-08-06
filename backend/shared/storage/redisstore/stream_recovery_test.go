package redisstore

import (
	"context"
	"sync"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// TestConsumerGroupRecoveredAfterWipe is the G4 regression guard, run against a
// real Redis (miniredis) rather than a mock.
//
// Before this change, a Redis wipe left XREADGROUP returning NOGROUP forever.
// The consumer looped at 1/block silently, the process reported itself healthy,
// and the relay was permanently dead — the worst failure mode available, since
// nothing looked wrong.
func TestConsumerGroupRecoveredAfterWipe(t *testing.T) {
	mr := miniredis.RunT(t)

	s := NewEventStream(mr.Addr(), "", "gateway", "consumer-1")
	s.SetBlockTimeout(20 * time.Millisecond)
	t.Cleanup(func() { _ = s.Close() })

	var (
		mu   sync.Mutex
		got  []string
		seen = func() int { mu.Lock(); defer mu.Unlock(); return len(got) }
	)
	handler := func(ev storage.Event) {
		mu.Lock()
		got = append(got, ev.Type)
		mu.Unlock()
	}

	ctx := context.Background()
	if err := s.Subscribe(ctx, "game_events", handler); err != nil {
		t.Fatalf("subscribe: %v", err)
	}

	// Baseline: delivery works before the wipe.
	publishEvent(t, s, "before_wipe")
	waitForCond(t, func() bool { return seen() >= 1 }, "first event was never delivered")

	// Simulate the disaster-recovery case: Redis is wiped (FLUSHALL, or restored
	// from a backup taken before the group existed). The stream and its consumer
	// group are gone; the consumer goroutine is mid-XREADGROUP.
	mr.FlushAll()

	// Recovery must be automatic: no restart, no manual XGROUP CREATE.
	waitForCond(t, func() bool { return s.GroupLosses() > 0 },
		"NOGROUP was never detected — the relay would spin silently forever")

	// And the relay must actually work again afterwards, which is the part a
	// "detect and log" fix would fail.
	publishEvent(t, s, "after_wipe")
	waitForCond(t, func() bool { return seen() >= 2 },
		"no event delivered after the group was re-created — relay is still dead")

	mu.Lock()
	defer mu.Unlock()
	if got[len(got)-1] != "after_wipe" {
		t.Errorf("last delivered event = %q, want %q", got[len(got)-1], "after_wipe")
	}
}

// TestSubscribeTolerantOfExistingGroup covers the BUSYGROUP path: a second
// subscriber (another gateway pod) must join an existing group, not fail.
func TestSubscribeTolerantOfExistingGroup(t *testing.T) {
	mr := miniredis.RunT(t)

	s1 := NewEventStream(mr.Addr(), "", "gateway", "pod-a")
	s1.SetBlockTimeout(20 * time.Millisecond)
	t.Cleanup(func() { _ = s1.Close() })
	if err := s1.Subscribe(context.Background(), "game_events", func(storage.Event) {}); err != nil {
		t.Fatalf("first subscribe: %v", err)
	}

	s2 := NewEventStream(mr.Addr(), "", "gateway", "pod-b")
	s2.SetBlockTimeout(20 * time.Millisecond)
	t.Cleanup(func() { _ = s2.Close() })
	if err := s2.Subscribe(context.Background(), "game_events", func(storage.Event) {}); err != nil {
		t.Fatalf("second subscribe into an existing group: %v", err)
	}
}

// TestGroupLossesStartsAtZero keeps the counter honest: a healthy stream must
// not report phantom recoveries (which would fire a false alert).
func TestGroupLossesStartsAtZero(t *testing.T) {
	mr := miniredis.RunT(t)
	s := NewEventStream(mr.Addr(), "", "gateway", "c")
	s.SetBlockTimeout(20 * time.Millisecond)
	t.Cleanup(func() { _ = s.Close() })

	if err := s.Subscribe(context.Background(), "game_events", func(storage.Event) {}); err != nil {
		t.Fatalf("subscribe: %v", err)
	}
	// Let the consumer loop idle through several block timeouts.
	time.Sleep(200 * time.Millisecond)

	if n := s.GroupLosses(); n != 0 {
		t.Errorf("GroupLosses() = %d on a healthy stream, want 0", n)
	}
}

func publishEvent(t *testing.T, s *EventStream, typ string) {
	t.Helper()
	if err := s.Publish(context.Background(), "game_events",
		storage.Event{Type: typ, Payload: []byte(`{}`)}); err != nil {
		t.Fatalf("publish %s: %v", typ, err)
	}
}

func waitForCond(t *testing.T, cond func() bool, msg string) {
	t.Helper()
	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal(msg)
}
