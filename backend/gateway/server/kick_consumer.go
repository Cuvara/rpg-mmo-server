package server

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"sync"
	"sync/atomic"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// KickConsumer consumes gateway_superseded events from the events:gateway_kick
// stream and closes the local socket for the superseded user when the event's
// old_gateway matches this gateway instance.
//
// Pattern: one shared stream, one consumer group per gateway instance
// ("gw:{gateway_id}"). Every gateway needs to see every kick event (broadcast);
// each keeps only the ones whose old_gateway names it. The group starts at $
// and is destroyed on graceful stop -- kick events target live in-process
// connections and mean nothing across a restart.
//
// This is the Go mirror of the C# RedisKickConsumer: same stream shape, same
// consumer-group lifecycle, same ACK-after-handle, same idempotency through
// the jti guard.
type KickConsumer struct {
	stream    storage.EventStream
	gatewayID string
	closer    ConnectionCloser
	logger    *slog.Logger

	mu      sync.Mutex
	started bool
	stopped bool

	consumed  atomic.Int64
	handled   atomic.Int64
	malformed atomic.Int64
}

// ConnectionCloser is the callback the KickConsumer uses to close a user's
// local gateway connection. The Gateway implements it via FindAndCloseConnection.
type ConnectionCloser func(userID string)

// NewKickConsumer builds a consumer but does not start it. Call Start to begin
// consuming.
func NewKickConsumer(
	stream storage.EventStream,
	gatewayID string,
	closer ConnectionCloser,
	logger *slog.Logger,
) *KickConsumer {
	return &KickConsumer{
		stream:    stream,
		gatewayID: gatewayID,
		closer:    closer,
		logger:    logger,
	}
}

// Start subscribes to the gateway_kick stream. Delivery runs in the background;
// Start returns once the subscription is established.
func (kc *KickConsumer) Start(ctx context.Context) error {
	kc.mu.Lock()
	if kc.started {
		kc.mu.Unlock()
		return fmt.Errorf("kick consumer: already started")
	}
	if kc.stopped {
		kc.mu.Unlock()
		return fmt.Errorf("kick consumer: already stopped")
	}
	kc.mu.Unlock()

	if err := kc.stream.Subscribe(ctx, constants.GatewayKickStream, kc.dispatch); err != nil {
		return fmt.Errorf("kick consumer subscribe %s: %w", constants.GatewayKickStream, err)
	}

	kc.mu.Lock()
	kc.started = true
	kc.mu.Unlock()
	if kc.logger != nil {
		kc.logger.Info("kick consumer started",
			"stream", constants.GatewayKickStream, "gateway", kc.gatewayID)
	}
	return nil
}

// Stop marks the consumer as stopped. It does NOT close the underlying
// stream -- the stream lifecycle is owned by whoever constructed this consumer
// (typically main.go), and the same stream may be shared with the event relay
// and the kick publisher. Safe to call more than once.
func (kc *KickConsumer) Stop() error {
	kc.mu.Lock()
	if kc.stopped {
		kc.mu.Unlock()
		return nil
	}
	kc.stopped = true
	kc.mu.Unlock()
	if kc.logger != nil {
		kc.logger.Info("kick consumer stopped", "gateway", kc.gatewayID,
			"consumed", kc.consumed.Load(), "handled", kc.handled.Load())
	}
	return nil
}

func (kc *KickConsumer) dispatch(ev storage.Event) {
	kc.consumed.Add(1)

	if ev.Type != constants.EventGatewaySuperseded {
		// Not ours -- ACKed by the stream layer automatically.
		return
	}

	var payload SessionSupersededEvent
	if err := json.Unmarshal(ev.Payload, &payload); err != nil {
		kc.malformed.Add(1)
		if kc.logger != nil {
			kc.logger.Warn("kick consumer: malformed gateway_superseded event",
				"err", err, "raw", string(ev.Payload))
		}
		return
	}

	// Only act on events addressed to THIS gateway instance.
	if payload.OldGateway != kc.gatewayID {
		return
	}

	kc.handled.Add(1)
	if kc.logger != nil {
		kc.logger.Info("kick consumer: closing superseded connection",
			"user", payload.UserID, "old_gateway", payload.OldGateway,
			"new_gateway", payload.NewGateway)
	}
	kc.closer(payload.UserID)
}

// Consumed returns the total number of events received from the stream.
func (kc *KickConsumer) Consumed() int64 { return kc.consumed.Load() }

// Handled returns the number of events that matched this gateway and triggered
// a connection close.
func (kc *KickConsumer) Handled() int64 { return kc.handled.Load() }

// Malformed returns the number of events that could not be parsed.
func (kc *KickConsumer) Malformed() int64 { return kc.malformed.Load() }
