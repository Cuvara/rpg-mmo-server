package server

import (
	"context"
	"encoding/json"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// SessionSupersededEvent is the payload of a constants.EventSessionSuperseded
// event on the events:kick stream: a newer login for UserID has superseded the
// session whose map assignment minted join token JTI for ServerID.
//
// The C# game server is the consumer (GameServer/Events/KickEvents.cs mirrors
// this shape); the normative contract lives in gameserver-dotnet/docs/API.md.
// The JTI is the whole race story: the game server kicks only the connection
// that joined with exactly this jti, so an event that is delivered late —
// after the NEW login already joined with a different jti — is a no-op rather
// than a kick of the wrong (newest) login. That also makes redelivery
// (at-least-once, consumer-group ACK) idempotent for free.
type SessionSupersededEvent struct {
	UserID   string `json:"user_id"`
	ServerID string `json:"server_id"`
	JTI      string `json:"jti"`
	// OldGateway/NewGateway mirror the "duplicate login detected" log fields;
	// they are diagnostic only and no consumer keys behaviour on them.
	OldGateway string `json:"old_gateway,omitempty"`
	NewGateway string `json:"new_gateway,omitempty"`
}

// kickPublishTimeout bounds the XADD on the auth path. Publishing rides the
// connection's read loop (like every other store call in handleAuth), so it
// must not be able to park the loop on a wedged Redis for longer than a client
// would wait for its auth response.
const kickPublishTimeout = 2 * time.Second

// publishSupersede tells the game server the superseded session joined that a
// newer login owns the user now. Called from handleAuth on duplicate login,
// for sessions owned by ANY gateway instance — unlike the local socket kick,
// this path does not need to reach the old connection, only the stream.
//
// It deliberately publishes nothing when the old session never completed a map
// assignment (no ServerID / no JoinTokenJTI): there is no game-server
// connection to kick, and an event without a jti could never be matched to one
// safely. Failures are logged and counted, never surfaced to the client — the
// NEW login must succeed regardless; the cost of a lost event is the old
// connection lingering until its own heartbeat or the player notices, not a
// correctness violation on the new session.
func (g *Gateway) publishSupersede(userID string, existing session.SessionData) {
	if existing.ServerID == "" || existing.JoinTokenJTI == "" {
		return
	}
	if g.kickStream == nil {
		// Construction gap, not a runtime condition: main.go always passes
		// WithKickStream. Loud, so a future caller that forgets the option
		// re-creates #211's defect visibly instead of silently.
		g.logger.Warn("duplicate login superseded a game-server session but no kick stream is configured; old connection will NOT be kicked",
			"user", userID, "server", existing.ServerID)
		return
	}

	payload, err := json.Marshal(SessionSupersededEvent{
		UserID:     userID,
		ServerID:   existing.ServerID,
		JTI:        existing.JoinTokenJTI,
		OldGateway: existing.GatewayID,
		NewGateway: g.sessions.GatewayID(),
	})
	if err != nil {
		// Marshalling a struct of strings cannot fail; guard anyway.
		g.logger.Error("marshal supersede event", "user", userID, "err", err)
		g.metrics.KickPublishResult(false)
		return
	}

	ctx, cancel := context.WithTimeout(context.Background(), kickPublishTimeout)
	defer cancel()
	err = g.kickStream.Publish(ctx, constants.KickEventStream, storage.Event{
		Type:    constants.EventSessionSuperseded,
		Payload: payload,
	})
	if err != nil {
		g.logger.Error("publish supersede event",
			"user", userID, "server", existing.ServerID, "err", err)
		g.metrics.KickPublishResult(false)
		return
	}
	g.metrics.KickPublishResult(true)
	g.logger.Info("published session supersede",
		"user", userID, "server", existing.ServerID,
		"old_gateway", existing.GatewayID, "new_gateway", g.sessions.GatewayID())
}
