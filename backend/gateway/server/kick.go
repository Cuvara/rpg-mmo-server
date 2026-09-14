package server

import (
	"context"
	"encoding/json"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

type SessionSupersededEvent struct {
	UserID     string `json:"user_id"`
	ServerID   string `json:"server_id"`
	JTI        string `json:"jti"`
	OldGateway string `json:"old_gateway,omitempty"`
	NewGateway string `json:"new_gateway,omitempty"`
}

const kickPublishTimeout = 2 * time.Second

func (g *Gateway) publishSupersede(userID string, existing session.SessionData) {
	if existing.ServerID == "" || existing.JoinTokenJTI == "" {
		return
	}
	if g.kickStream == nil {
		g.logger.Warn("no kick stream configured", "user", userID, "server", existing.ServerID)
		return
	}
	payload, err := json.Marshal(SessionSupersededEvent{
		UserID: userID, ServerID: existing.ServerID, JTI: existing.JoinTokenJTI,
		OldGateway: existing.GatewayID, NewGateway: g.sessions.GatewayID(),
	})
	if err != nil {
		g.logger.Error("marshal supersede event", "user", userID, "err", err)
		g.metrics.KickPublishResult(false)
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), kickPublishTimeout)
	defer cancel()
	if err = g.kickStream.Publish(ctx, constants.KickEventStream, storage.Event{
		Type: constants.EventSessionSuperseded, Payload: payload,
	}); err != nil {
		g.logger.Error("publish supersede event", "user", userID, "server", existing.ServerID, "err", err)
		g.metrics.KickPublishResult(false)
		return
	}
	g.metrics.KickPublishResult(true)
	g.logger.Info("published session supersede", "user", userID, "server", existing.ServerID,
		"old_gateway", existing.GatewayID, "new_gateway", g.sessions.GatewayID())
	if existing.GatewayID != "" && existing.GatewayID != g.sessions.GatewayID() {
		g.publishGatewayKick(userID, existing.GatewayID, payload)
	}
}

func (g *Gateway) publishGatewayKick(userID, oldGateway string, payload []byte) {
	if g.kickStream == nil {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), kickPublishTimeout)
	defer cancel()
	if err := g.kickStream.Publish(ctx, constants.GatewayKickStream, storage.Event{
		Type: constants.EventGatewaySuperseded, Payload: payload,
	}); err != nil {
		g.logger.Error("publish gateway kick", "user", userID, "old_gateway", oldGateway, "err", err)
		return
	}
	g.logger.Info("published gateway kick", "user", userID, "old_gateway", oldGateway,
		"new_gateway", g.sessions.GatewayID())
}
