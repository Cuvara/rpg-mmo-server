package server

import (
	"context"
	"encoding/json"
	"sync"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// TestKickConsumer_TableDriven pins the dispatch logic of the gateway kick
// consumer: close on match, ignore on mismatch, count malformed.
func TestKickConsumer_TableDriven(t *testing.T) {
	tests := []struct {
		name        string
		gatewayID   string
		event       storage.Event
		wantClosed  string // userID of expected close, "" if none
		wantHandled int64
	}{
		{
			name:      "closes_connection_when_old_gateway_matches",
			gatewayID: "gw-old",
			event: makeGatewayKickEvent(t, SessionSupersededEvent{
				UserID:     "user-1",
				ServerID:   "srv1",
				JTI:        "jti-abc",
				OldGateway: "gw-old",
				NewGateway: "gw-new",
			}),
			wantClosed:  "user-1",
			wantHandled: 1,
		},
		{
			name:      "ignores_event_for_different_gateway",
			gatewayID: "gw-other",
			event: makeGatewayKickEvent(t, SessionSupersededEvent{
				UserID:     "user-2",
				ServerID:   "srv1",
				JTI:        "jti-def",
				OldGateway: "gw-old",
				NewGateway: "gw-new",
			}),
			wantClosed:  "",
			wantHandled: 0,
		},
		{
			name:      "ignores_wrong_event_type",
			gatewayID: "gw-old",
			event: storage.Event{
				Type:    constants.EventSessionSuperseded, // game-server event, not gateway
				Payload: []byte(`{"user_id":"user-3"}`),
			},
			wantClosed:  "",
			wantHandled: 0,
		},
		{
			name:      "counts_malformed_payload",
			gatewayID: "gw-old",
			event: storage.Event{
				Type:    constants.EventGatewaySuperseded,
				Payload: []byte(`{invalid json`),
			},
			wantClosed:  "",
			wantHandled: 0,
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var mu sync.Mutex
			var closedUser string
			closer := func(userID string) {
				mu.Lock()
				closedUser = userID
				mu.Unlock()
			}

			stream := storage.NewMemoryEventStream()
			t.Cleanup(func() { _ = stream.Close() })

			kc := NewKickConsumer(stream, tc.gatewayID, closer, logger.New("error"))
			if err := kc.Start(context.Background()); err != nil {
				t.Fatalf("Start: %v", err)
			}
			t.Cleanup(func() { _ = kc.Stop() })

			// Publish the event.
			if err := stream.Publish(context.Background(), constants.GatewayKickStream, tc.event); err != nil {
				t.Fatalf("Publish: %v", err)
			}

			// Give the in-memory stream a moment to dispatch synchronously.
			time.Sleep(50 * time.Millisecond)

			mu.Lock()
			got := closedUser
			mu.Unlock()

			if got != tc.wantClosed {
				t.Errorf("closed user = %q, want %q", got, tc.wantClosed)
			}
			if kc.Handled() != tc.wantHandled {
				t.Errorf("handled = %d, want %d", kc.Handled(), tc.wantHandled)
			}
		})
	}
}

// TestKickConsumer_MalformedCount verifies the malformed counter increments.
func TestKickConsumer_MalformedCount(t *testing.T) {
	stream := storage.NewMemoryEventStream()
	t.Cleanup(func() { _ = stream.Close() })

	kc := NewKickConsumer(stream, "gw-x", func(string) {}, logger.New("error"))
	if err := kc.Start(context.Background()); err != nil {
		t.Fatalf("Start: %v", err)
	}
	t.Cleanup(func() { _ = kc.Stop() })

	// Publish a malformed event.
	if err := stream.Publish(context.Background(), constants.GatewayKickStream, storage.Event{
		Type:    constants.EventGatewaySuperseded,
		Payload: []byte("not json"),
	}); err != nil {
		t.Fatalf("Publish: %v", err)
	}

	time.Sleep(50 * time.Millisecond)

	if kc.Malformed() != 1 {
		t.Errorf("malformed = %d, want 1", kc.Malformed())
	}
	if kc.Handled() != 0 {
		t.Errorf("handled = %d, want 0", kc.Handled())
	}
}

// TestKickConsumer_StopIdempotent verifies Stop can be called multiple times.
func TestKickConsumer_StopIdempotent(t *testing.T) {
	stream := storage.NewMemoryEventStream()
	t.Cleanup(func() { _ = stream.Close() })

	kc := NewKickConsumer(stream, "gw-y", func(string) {}, logger.New("error"))
	if err := kc.Start(context.Background()); err != nil {
		t.Fatalf("Start: %v", err)
	}
	if err := kc.Stop(); err != nil {
		t.Errorf("first Stop: %v", err)
	}
	if err := kc.Stop(); err != nil {
		t.Errorf("second Stop: %v", err)
	}
}

func makeGatewayKickEvent(t *testing.T, ev SessionSupersededEvent) storage.Event {
	t.Helper()
	payload, err := json.Marshal(ev)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	return storage.Event{
		Type:    constants.EventGatewaySuperseded,
		Payload: payload,
	}
}
