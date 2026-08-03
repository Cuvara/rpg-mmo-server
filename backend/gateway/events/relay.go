package events

import (
	"context"

	gameerrors "github.com/duycuong/rpg-mmo/shared/errors"
)

// EventRelay forwards cross-server events from Redis Streams to clients.
type EventRelay interface {
	// Start begins consuming events from the stream.
	Start(ctx context.Context) error
	// Stop gracefully shuts down the relay.
	Stop() error
}

// StubEventRelay is a placeholder that always returns ErrNotImplemented.
type StubEventRelay struct{}

// Start returns ErrNotImplemented.
func (s *StubEventRelay) Start(_ context.Context) error {
	return gameerrors.New(gameerrors.ErrNotImplemented, "event relay not implemented")
}

// Stop returns nil (nothing to clean up).
func (s *StubEventRelay) Stop() error {
	return nil
}
