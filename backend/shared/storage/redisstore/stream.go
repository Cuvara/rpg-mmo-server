package redisstore

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/redis/go-redis/v9"
)

// Field names used inside a stream entry.
const (
	streamFieldType    = "type"
	streamFieldPayload = "payload"
)

// defaultStreamBlock is how long a consumer blocks on XREADGROUP before looping
// (bounded so Close() is responsive).
const defaultStreamBlock = 500 * time.Millisecond

// DefaultStreamMaxLen bounds `events:*` at publish time (XADD ... MAXLEN ~ N).
//
// Redis here runs `maxmemory-policy noeviction` on purpose (ADR-4): it is the
// system of record for the server registry, so it must refuse writes rather
// than silently drop a live game server. That policy makes an untrimmed stream
// the one unbounded consumer of the instance's whole memory budget, and the
// Redis pod is capped at 256Mi — so without a bound the kernel OOM-kills Redis
// *whole*, taking sessions and the registry with it, which is the exact outcome
// noeviction was chosen to prevent (#202). The bound belongs at the publisher
// because that is the only place that runs on every write.
//
// The length is chosen from **consumer lag**, not from a memory figure:
//
//	events/s   the dominant event is entity_killed, one per mob death, from
//	           every game server into the one shared events:game stream. Taking
//	           the 200-player-per-server figure that BENCHMARK.md actually
//	           measures, and ASSUMING a kill roughly every 10s per player, that
//	           is ~20 events/s per server; two live servers and rounding up for
//	           the smaller event types (boss_killed, rare_drop,
//	           inventory_changed) gives a planning rate of 50 events/s.
//	lag budget the only consumer group is the gateway relay, and it falls behind
//	           only while it is down. A CD deploy restarts the gateway (ADR-18
//	           calls those outages), and Kubernetes caps CrashLoopBackOff at 5
//	           minutes, so 10 minutes covers a deploy, a backoff cycle and a
//	           manual restart with room to spare.
//
//	50 events/s * 600s = 30_000 entries
//
// Beyond that window entries are dropped rather than delivered, which is the
// deliberate trade: at-least-once delivery is a promise to a consumer that is
// *running*, and a consumer down for ten minutes has already lost the timeliness
// that made these events worth delivering.
//
// Memory cross-check, so the bound cannot itself be the thing that fills the
// instance: an entity_killed entry is a short type string plus a small JSON
// payload, well under 256 bytes including stream node overhead, so the trimmed
// stream tops out around 30_000 * 256B ~= 7.3MiB — about 6% of the 128mb
// maxmemory set in deploy/k8s/data/redis.conf. The stream is bounded by lag
// tolerance and stays far away from the ceiling; that is the intended relation
// between the two numbers.
const DefaultStreamMaxLen int64 = 30_000

// RedisEventStream implements EventStream over Redis Streams using a consumer
// group: XADD to publish, XREADGROUP to consume, XACK after the handler runs
// (at-least-once delivery; a crashed consumer leaves the entry pending for
// another member of the group).
type EventStream struct {
	client   redis.UniversalClient
	group    string
	consumer string
	block    time.Duration
	maxLen   int64
	owned    bool
	logger   *slog.Logger

	// groupLosses counts NOGROUP recoveries. Exported through GroupLosses so
	// the gateway can surface it as a metric and tests can assert the recovery
	// actually happened rather than inferring it from timing.
	groupLosses atomic.Int64

	mu     sync.Mutex
	closed bool
	cancel context.CancelFunc
	ctx    context.Context
	wg     sync.WaitGroup
}

// SetLogger attaches a logger used for consumer-group recovery events. Must be
// called before Subscribe.
func (s *EventStream) SetLogger(l *slog.Logger) { s.logger = l }

// GroupLosses returns how many times the consumer group was found missing and
// re-created since this stream was built.
func (s *EventStream) GroupLosses() int64 { return s.groupLosses.Load() }

func (s *EventStream) recordGroupLoss() { s.groupLosses.Add(1) }

func (s *EventStream) logf(format string, args ...any) {
	if s.logger != nil {
		s.logger.Warn(fmt.Sprintf(format, args...))
	}
}

// NewRedisEventStream connects to Redis at addr. group is the consumer group
// name (one logical subscriber, e.g. "gateway"); consumer identifies this
// process within the group (e.g. the pod name).
// The client gets a read timeout comfortably above defaultStreamBlock: a
// blocking XREADGROUP legitimately holds the socket for the whole block
// duration, so a read timeout at or below it would turn every idle poll into a
// spurious i/o timeout and mask real errors.
func NewEventStream(addr, password, group, consumer string) *EventStream {
	client := NewRedisClientWithOptions(ClientOptions{
		Addr:        addr,
		Password:    password,
		ReadTimeout: defaultStreamBlock + DefaultReadTimeout,
	})
	s := newEventStream(client, group, consumer)
	s.owned = true
	return s
}

// NewRedisEventStreamWithClient wraps an existing client (shared pool, tests).
func NewEventStreamWithClient(client redis.UniversalClient, group, consumer string) *EventStream {
	return newEventStream(client, group, consumer)
}

func newEventStream(client redis.UniversalClient, group, consumer string) *EventStream {
	ctx, cancel := context.WithCancel(context.Background())
	return &EventStream{
		client:   client,
		group:    group,
		consumer: consumer,
		block:    defaultStreamBlock,
		maxLen:   DefaultStreamMaxLen,
		ctx:      ctx,
		cancel:   cancel,
	}
}

// SetBlockTimeout overrides how long a consumer blocks per XREADGROUP call.
// Must be called before Subscribe.
func (s *EventStream) SetBlockTimeout(d time.Duration) {
	if d > 0 {
		s.block = d
	}
}

// SetMaxLen overrides the retained stream length used by Publish. A
// non-positive value is ignored rather than treated as "unbounded": an
// unbounded stream against a noeviction Redis is the failure this bound exists
// to prevent (#202), so there is deliberately no way to switch it off through
// this API. Must be called before Publish.
func (s *EventStream) SetMaxLen(n int64) {
	if n > 0 {
		s.maxLen = n
	}
}

// maxLenOrDefault guards against a zero-valued EventStream built by something
// other than newEventStream.
func (s *EventStream) maxLenOrDefault() int64 {
	if s.maxLen > 0 {
		return s.maxLen
	}
	return DefaultStreamMaxLen
}

// streamKey builds the Redis key for a logical stream name.
func streamKey(stream string) string {
	return constants.EventStreamPrefix + stream
}

// Publish appends an event to the stream (XADD).
func (s *EventStream) Publish(ctx context.Context, stream string, event storage.Event) error {
	s.mu.Lock()
	closed := s.closed
	s.mu.Unlock()
	if closed {
		return fmt.Errorf("event stream closed")
	}

	// MaxLen + Approx is `MAXLEN ~ N`: Redis trims whole radix-tree nodes and
	// stops at the first node it may not drop, so it removes entries in cheap
	// batches and may leave somewhat more than N. Exact trimming (`MAXLEN N`)
	// would make every publish pay for entry-precise deletion to enforce a
	// number that is itself a rounded-off lag budget — real cost for false
	// precision. Approximate is the right form here, and the resulting overshoot
	// is bounded by one node, not by the stream's age.
	err := s.client.XAdd(ctx, &redis.XAddArgs{
		Stream: streamKey(stream),
		MaxLen: s.maxLenOrDefault(),
		Approx: true,
		Values: map[string]any{
			streamFieldType:    event.Type,
			streamFieldPayload: event.Payload,
		},
	}).Err()
	if err != nil {
		return fmt.Errorf("redis xadd %s: %w", stream, err)
	}
	return nil
}

// Subscribe joins the consumer group on the stream and delivers events to the
// handler from a background goroutine. It returns as soon as the group exists;
// delivery stops on Close.
func (s *EventStream) Subscribe(ctx context.Context, stream string, handler func(storage.Event)) error {
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		return fmt.Errorf("event stream closed")
	}
	s.mu.Unlock()

	key := streamKey(stream)
	if err := s.ensureGroup(ctx, key); err != nil {
		return fmt.Errorf("redis xgroup create %s/%s: %w", stream, s.group, err)
	}

	s.wg.Add(1)
	go func() {
		defer s.wg.Done()
		s.consume(key, handler)
	}()
	return nil
}

// isNoGroup reports whether err is Redis' NOGROUP error — the consumer group
// (or the stream itself) no longer exists. This is what a FLUSHALL, a restore
// from a backup taken before the group was created, or an eviction leaves
// behind, and it is NOT transient: retrying XREADGROUP against a missing group
// fails identically forever, so the relay must re-create the group instead.
func isNoGroup(err error) bool {
	return err != nil && strings.Contains(err.Error(), "NOGROUP")
}

// ensureGroup creates the consumer group, tolerating BUSYGROUP (already there).
// MkStream so subscribing before the first publish works.
func (s *EventStream) ensureGroup(ctx context.Context, key string) error {
	err := s.client.XGroupCreateMkStream(ctx, key, s.group, "0").Err()
	if err != nil && !strings.Contains(err.Error(), "BUSYGROUP") {
		return err
	}
	return nil
}

// consume loops XREADGROUP → handler → XACK until the stream is closed.
func (s *EventStream) consume(key string, handler func(storage.Event)) {
	for {
		if s.ctx.Err() != nil {
			return
		}

		res, err := s.client.XReadGroup(s.ctx, &redis.XReadGroupArgs{
			Group:    s.group,
			Consumer: s.consumer,
			Streams:  []string{key, ">"},
			Count:    16,
			Block:    s.block,
		}).Result()
		if err != nil {
			// Nil == block timeout with no entries; anything else during
			// shutdown is expected too.
			if errors.Is(err, redis.Nil) || s.ctx.Err() != nil {
				continue
			}
			// NOGROUP: the group vanished under us (Redis wiped/restored). Left
			// alone this loop would spin at 1/block forever while the process
			// still looked healthy and the relay was permanently dead. Re-create
			// the group and carry on; entries published while the group was
			// missing are unrecoverable (they were never delivered to anyone),
			// which is why this is logged loudly rather than silently healed.
			if isNoGroup(err) {
				s.recordGroupLoss()
				if cerr := s.ensureGroup(s.ctx, key); cerr != nil {
					s.logf("event stream: consumer group %q lost on %q, re-create failed: %v", s.group, key, cerr)
				} else {
					s.logf("event stream: consumer group %q was missing on %q, re-created", s.group, key)
				}
				select {
				case <-s.ctx.Done():
					return
				case <-time.After(s.block):
				}
				continue
			}
			// Transient error (e.g. Redis restart): back off briefly and retry.
			select {
			case <-s.ctx.Done():
				return
			case <-time.After(s.block):
			}
			continue
		}

		for _, stream := range res {
			for _, msg := range stream.Messages {
				handler(eventFromValues(msg.Values))
				// ACK only after the handler returned: at-least-once.
				_ = s.client.XAck(s.ctx, key, s.group, msg.ID).Err()
			}
		}
	}
}

// Close stops all consumers and waits for in-flight handlers to finish.
func (s *EventStream) Close() error {
	s.mu.Lock()
	if s.closed {
		s.mu.Unlock()
		return nil
	}
	s.closed = true
	s.mu.Unlock()

	s.cancel()
	s.wg.Wait()

	if !s.owned {
		return nil
	}
	if err := s.client.Close(); err != nil {
		return fmt.Errorf("close redis client: %w", err)
	}
	return nil
}

func eventFromValues(values map[string]any) storage.Event {
	e := storage.Event{}
	if v, ok := values[streamFieldType].(string); ok {
		e.Type = v
	}
	if v, ok := values[streamFieldPayload].(string); ok {
		e.Payload = []byte(v)
	}
	return e
}
