package events

import (
	"context"
	"log/slog"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

// Publisher wraps an EventStream for publishing game events.
type Publisher struct {
	stream storage.EventStream
	logger *slog.Logger
}

// NewPublisher creates an event publisher.
func NewPublisher(stream storage.EventStream, logger *slog.Logger) *Publisher {
	return &Publisher{stream: stream, logger: logger}
}

// Publish sends an event to the given stream.
func (p *Publisher) Publish(ctx context.Context, streamName string, event storage.Event) {
	if err := p.stream.Publish(ctx, streamName, event); err != nil {
		p.logger.Error("publish event failed", "stream", streamName, "type", event.Type, "err", err)
	}
}
