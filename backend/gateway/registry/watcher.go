package registry

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

const (
	// watchPollInterval is how often the watcher checks known servers.
	//
	// It MUST stay meaningfully shorter than constants.ServerHeartbeatTTL (15s),
	// the TTL after which the registry drops a server that stopped beating. The
	// watcher exists to notice a dead server BEFORE that expiry propagates on its
	// own; a poll slower than the TTL would always lose the race and the watcher
	// would add nothing. 5s against a 15s TTL is a 3x margin, so a server that
	// dies is published as server_down within at most one third of the window in
	// which the gateway would otherwise keep handing clients its address (the
	// split-map fault of #203).
	//
	// TestWatchPollInterval_ShorterThanHeartbeatTTL pins this relationship so it
	// cannot silently invert.
	watchPollInterval = 5 * time.Second

	// ServerDownChannel is the Redis Pub/Sub channel for server-down events.
	ServerDownChannel = "gateway:server_down"
)

// ServerDownEvent is published when a server's heartbeat expires.
type ServerDownEvent struct {
	ServerID string `json:"server_id"`
	MapID    string `json:"map_id"`
}

// Publisher publishes messages to a channel. In production this is backed by
// Redis Pub/Sub; tests use an in-memory implementation.
type Publisher interface {
	Publish(ctx context.Context, channel string, message []byte) error
}

// Subscriber subscribes to a channel and delivers messages via a callback.
type Subscriber interface {
	Subscribe(ctx context.Context, channel string, handler func([]byte)) error
	Close() error
}

// RegistryWatcher periodically polls known servers and detects when one
// disappears (heartbeat TTL expired). On detection it publishes a
// ServerDownEvent to the gateway:server_down channel and clears cached state.
type RegistryWatcher struct {
	reg       storage.ServerRegistry
	publisher Publisher
	log       logger

	mu           sync.Mutex
	knownServers map[string]string // serverID -> mapID
	stopped      chan struct{}
}

// NewRegistryWatcher creates a watcher that monitors registry health.
func NewRegistryWatcher(reg storage.ServerRegistry, pub Publisher, log logger) *RegistryWatcher {
	return &RegistryWatcher{
		reg:          reg,
		publisher:    pub,
		log:          log,
		knownServers: make(map[string]string),
		stopped:      make(chan struct{}),
	}
}

// TrackServer adds a server to the set of known servers to monitor.
func (w *RegistryWatcher) TrackServer(serverID, mapID string) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.knownServers[serverID] = mapID
}

// UntrackServer removes a server from monitoring (e.g. on graceful deregister).
func (w *RegistryWatcher) UntrackServer(serverID string) {
	w.mu.Lock()
	defer w.mu.Unlock()
	delete(w.knownServers, serverID)
}

// KnownCount returns the number of servers being tracked.
func (w *RegistryWatcher) KnownCount() int {
	w.mu.Lock()
	defer w.mu.Unlock()
	return len(w.knownServers)
}

// Start begins the polling loop. It returns immediately; polling runs in a
// background goroutine until ctx is cancelled or Stop is called.
func (w *RegistryWatcher) Start(ctx context.Context) {
	go w.pollLoop(ctx)
}

// Stop signals the watcher to stop. Blocks until the poll loop exits.
func (w *RegistryWatcher) Stop() {
	<-w.stopped
}

func (w *RegistryWatcher) pollLoop(ctx context.Context) {
	defer close(w.stopped)
	ticker := time.NewTicker(watchPollInterval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			w.checkServers(ctx)
		}
	}
}

func (w *RegistryWatcher) checkServers(ctx context.Context) {
	w.mu.Lock()
	// Snapshot the known set so we don't hold the lock during Redis calls.
	snapshot := make(map[string]string, len(w.knownServers))
	for id, mapID := range w.knownServers {
		snapshot[id] = mapID
	}
	w.mu.Unlock()

	for serverID, mapID := range snapshot {
		_, err := w.reg.GetServer(ctx, serverID)
		if err == nil {
			continue
		}

		// Only a definitive "the entry does not exist" (storage.ErrNotFound —
		// heartbeat TTL expired or the server deregistered) is server death.
		// Anything else is a store fault — a Redis blip, a network timeout — and
		// says nothing about the server, so the watcher keeps tracking it and
		// publishes nothing: a false server_down here would be consumed as an
		// eviction and take a healthy server out of assignment for every tracked
		// server at once, turning one transient store error into an outage.
		if !errors.Is(err, storage.ErrNotFound) {
			w.log.Warn("registry check failed with a transient error; keeping server tracked",
				"server_id", serverID, "map_id", mapID, "error", err)
			continue
		}

		// Server is gone (heartbeat expired or deregistered).
		w.log.Warn("server heartbeat expired, publishing server_down",
			"server_id", serverID, "map_id", mapID)

		w.mu.Lock()
		delete(w.knownServers, serverID)
		w.mu.Unlock()

		event := ServerDownEvent{ServerID: serverID, MapID: mapID}
		payload, _ := json.Marshal(event)

		if w.publisher != nil {
			if perr := w.publisher.Publish(ctx, ServerDownChannel, payload); perr != nil {
				w.log.Warn("failed to publish server_down event",
					"server_id", serverID, "error", perr)
			}
		}
	}
}

// SubscribeServerDown listens for server_down events on the given subscriber
// and calls onDown for each event. Runs until ctx is cancelled.
func SubscribeServerDown(ctx context.Context, sub Subscriber, onDown func(ServerDownEvent), log logger) error {
	return sub.Subscribe(ctx, ServerDownChannel, func(data []byte) {
		var event ServerDownEvent
		if err := json.Unmarshal(data, &event); err != nil {
			log.Warn("failed to unmarshal server_down event", "error", err)
			return
		}
		onDown(event)
	})
}

// MemoryPubSub is an in-memory Publisher+Subscriber for tests.
type MemoryPubSub struct {
	mu       sync.Mutex
	handlers map[string][]func([]byte)
	messages [][]byte
}

// NewMemoryPubSub creates a test-only pub/sub.
func NewMemoryPubSub() *MemoryPubSub {
	return &MemoryPubSub{handlers: make(map[string][]func([]byte))}
}

// Publish sends a message to all subscribers of the channel.
func (m *MemoryPubSub) Publish(_ context.Context, channel string, message []byte) error {
	m.mu.Lock()
	m.messages = append(m.messages, message)
	handlers := append([]func([]byte){}, m.handlers[channel]...)
	m.mu.Unlock()

	for _, h := range handlers {
		h(message)
	}
	return nil
}

// Subscribe registers a handler for a channel.
func (m *MemoryPubSub) Subscribe(_ context.Context, channel string, handler func([]byte)) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.handlers[channel] = append(m.handlers[channel], handler)
	return nil
}

// Close is a no-op for the in-memory implementation.
func (m *MemoryPubSub) Close() error { return nil }

// MessageCount returns the number of published messages.
func (m *MemoryPubSub) MessageCount() int {
	m.mu.Lock()
	defer m.mu.Unlock()
	return len(m.messages)
}

// String implements fmt.Stringer for debug/test output.
func (e ServerDownEvent) String() string {
	return fmt.Sprintf("server_down{server_id=%s, map_id=%s}", e.ServerID, e.MapID)
}
