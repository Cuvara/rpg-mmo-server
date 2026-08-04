package events

import (
	"context"
	"sync"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"
)

// collector is a Sink that records everything it receives.
type collector struct {
	mu     sync.Mutex
	events []storage.Event
}

func (c *collector) OnEvent(ev storage.Event) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.events = append(c.events, ev)
}

func (c *collector) len() int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return len(c.events)
}

func (c *collector) types() []string {
	c.mu.Lock()
	defer c.mu.Unlock()
	out := make([]string, len(c.events))
	for i, e := range c.events {
		out[i] = e.Type
	}
	return out
}

func TestRelay_ForwardsEvents(t *testing.T) {
	tests := []struct {
		name   string
		stream func(t *testing.T) storage.EventStream
	}{
		{
			name:   "memory",
			stream: func(t *testing.T) storage.EventStream { return storage.NewMemoryEventStream() },
		},
		{
			name: "redis",
			stream: func(t *testing.T) storage.EventStream {
				mr := miniredis.RunT(t)
				s := redisstore.NewEventStream(mr.Addr(), "", "gateway-test", "consumer-1")
				s.SetBlockTimeout(20 * time.Millisecond)
				return s
			},
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			stream := tt.stream(t)
			sink := &collector{}
			relay := NewRelay(stream, "", sink, logger.New("error"))

			ctx := context.Background()
			if err := relay.Start(ctx); err != nil {
				t.Fatalf("Start() error: %v", err)
			}
			defer relay.Stop()

			if relay.Stream() != DefaultStream {
				t.Errorf("Stream() = %q, want %q", relay.Stream(), DefaultStream)
			}

			want := []string{"boss_killed", "rare_drop"}
			for _, typ := range want {
				if err := stream.Publish(ctx, DefaultStream, storage.Event{
					Type:    typ,
					Payload: []byte(`{"map_id":"map_forest"}`),
				}); err != nil {
					t.Fatalf("Publish(%s) error: %v", typ, err)
				}
			}

			deadline := time.Now().Add(3 * time.Second)
			for sink.len() < len(want) && time.Now().Before(deadline) {
				time.Sleep(10 * time.Millisecond)
			}
			// Ordering is not guaranteed (MemoryEventStream fans out
			// concurrently), so compare as a set.
			got := map[string]bool{}
			for _, typ := range sink.types() {
				got[typ] = true
			}
			if len(got) != len(want) {
				t.Fatalf("received %v, want %v", sink.types(), want)
			}
			for _, typ := range want {
				if !got[typ] {
					t.Errorf("missing event %q (got %v)", typ, sink.types())
				}
			}
		})
	}
}

func TestRelay_StartTwiceFails(t *testing.T) {
	relay := NewRelay(storage.NewMemoryEventStream(), DefaultStream, SinkFunc(func(storage.Event) {}), logger.New("error"))
	if err := relay.Start(context.Background()); err != nil {
		t.Fatalf("first Start() error: %v", err)
	}
	defer relay.Stop()

	if err := relay.Start(context.Background()); err == nil {
		t.Error("second Start() should fail")
	}
}

func TestRelay_StopIsIdempotent(t *testing.T) {
	relay := NewRelay(storage.NewMemoryEventStream(), DefaultStream, &collector{}, logger.New("error"))
	if err := relay.Start(context.Background()); err != nil {
		t.Fatalf("Start() error: %v", err)
	}
	if err := relay.Stop(); err != nil {
		t.Fatalf("first Stop() error: %v", err)
	}
	if err := relay.Stop(); err != nil {
		t.Errorf("second Stop() error: %v", err)
	}
}

func TestStubEventRelay(t *testing.T) {
	var s StubEventRelay
	if err := s.Start(context.Background()); err != nil {
		t.Errorf("Start() error: %v", err)
	}
	if err := s.Stop(); err != nil {
		t.Errorf("Stop() error: %v", err)
	}
}
