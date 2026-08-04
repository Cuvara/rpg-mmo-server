package events

import (
	"context"
	"encoding/json"
	"log/slog"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// GameStream is the cross-server stream game servers publish gameplay events
// to. Logical name only — the storage layer adds constants.EventStreamPrefix.
const GameStream = constants.GameEventStream

// Event type identifiers carried in storage.Event.Type.
const (
	TypePlayerDeath = "player_death"
	TypeBossKilled  = "boss_killed"
)

// DeathPayload is the JSON payload of a player_death / boss_killed event.
type DeathPayload struct {
	VictimID   string `json:"victim_id"`
	VictimType string `json:"victim_type"`
	KillerID   string `json:"killer_id,omitempty"`
	MapID      string `json:"map_id"`
	ServerID   string `json:"server_id"`
}

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

// PublishDeath emits a player_death (or boss_killed) event on GameStream.
// eventType selects the event id; payload is marshaled to JSON.
func (p *Publisher) PublishDeath(ctx context.Context, eventType string, payload DeathPayload) {
	data, err := json.Marshal(payload)
	if err != nil {
		p.logger.Error("marshal death event failed", "type", eventType, "err", err)
		return
	}
	p.Publish(ctx, GameStream, storage.Event{Type: eventType, Payload: data})
}
