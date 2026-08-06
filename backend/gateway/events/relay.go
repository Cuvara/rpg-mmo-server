package events

import (
	"context"
	"fmt"
	"log/slog"
	"sync"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// DefaultStream is the logical stream name the gateway subscribes to. It must
// match what game servers publish to (constants.GameEventStream); the concrete
// store adds the prefix (constants.EventStreamPrefix for Redis Streams).
const DefaultStream = constants.GameEventStream

// EventRelay consumes cross-server events and hands them to the gateway.
type EventRelay interface {
	// Start begins consuming events from the stream.
	Start(ctx context.Context) error
	// Stop gracefully shuts down the relay.
	Stop() error
}

// Sink receives every event the relay consumes. The gateway implements it and
// decides what to do with each event.
type Sink interface {
	// OnEvent is called once per consumed event, from the consumer goroutine.
	OnEvent(ev storage.Event)
}

// SinkFunc adapts a plain function to the Sink interface.
type SinkFunc func(ev storage.Event)

// OnEvent calls f.
func (f SinkFunc) OnEvent(ev storage.Event) { f(ev) }

// Relay is the real EventRelay: it subscribes to a storage.EventStream
// (MemoryEventStream in dev/tests, redisstore.EventStream with consumer-group
// ACK in production) and dispatches every event to a Sink.
//
// MVP limitation: shared/messages has no client-facing event message type (ids
// stop at MsgDisconnect), so the gateway's sink logs and counts events rather
// than pushing them down client sockets. Adding a MsgEvent to shared/messages
// (owned by agent-shared) is the only missing piece — the plumbing here is
// complete and exercised against both stream implementations.
type Relay struct {
	stream storage.EventStream
	name   string
	sink   Sink
	logger *slog.Logger

	mu      sync.Mutex
	started bool
	stopped bool
}

// NewRelay builds a Relay over the given event stream. streamName defaults to
// DefaultStream when empty.
func NewRelay(stream storage.EventStream, streamName string, sink Sink, logger *slog.Logger) *Relay {
	if streamName == "" {
		streamName = DefaultStream
	}
	return &Relay{stream: stream, name: streamName, sink: sink, logger: logger}
}

// Stream returns the logical stream name the relay consumes.
func (r *Relay) Stream() string { return r.name }

// Start subscribes to the stream. Delivery runs in the background; Start
// returns once the subscription is established.
func (r *Relay) Start(ctx context.Context) error {
	r.mu.Lock()
	if r.started {
		r.mu.Unlock()
		return fmt.Errorf("event relay: already started")
	}
	if r.stopped {
		r.mu.Unlock()
		return fmt.Errorf("event relay: already stopped")
	}
	r.mu.Unlock()

	// `started` is only latched once Subscribe actually succeeds. Setting it
	// before the attempt would make a failed Start permanent: the caller's
	// retry (the gateway retries a relay that could not reach Redis at boot)
	// would hit the already-started guard forever and the relay would never
	// recover, while still reporting itself as started.
	if err := r.stream.Subscribe(ctx, r.name, r.dispatch); err != nil {
		return fmt.Errorf("event relay subscribe %s: %w", r.name, err)
	}

	r.mu.Lock()
	r.started = true
	r.mu.Unlock()
	if r.logger != nil {
		r.logger.Info("event relay started", "stream", r.name)
	}
	return nil
}

func (r *Relay) dispatch(ev storage.Event) {
	if r.sink != nil {
		r.sink.OnEvent(ev)
	}
}

// Stop closes the underlying stream subscription. Safe to call more than once.
func (r *Relay) Stop() error {
	r.mu.Lock()
	if r.stopped {
		r.mu.Unlock()
		return nil
	}
	r.stopped = true
	r.mu.Unlock()

	if err := r.stream.Close(); err != nil {
		return fmt.Errorf("event relay stop: %w", err)
	}
	return nil
}

// StubEventRelay is a no-op relay used when no event stream is configured.
type StubEventRelay struct{}

// Start does nothing and succeeds.
func (s *StubEventRelay) Start(_ context.Context) error { return nil }

// Stop returns nil (nothing to clean up).
func (s *StubEventRelay) Stop() error { return nil }
