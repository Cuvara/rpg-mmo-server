package constants

// Redis key patterns. Use fmt.Sprintf to fill placeholders.
const (
	SessionKeyPrefix  = "session:" // session:{user_id}
	ServerRegistryKey = "servers:" // servers:{map_id}
	EventStreamPrefix = "events:"  // events:{stream_name}

	// GameEventStream is the LOGICAL name of the cross-server gameplay event
	// stream.
	GameEventStream = "game"

	// KickEventStream is the LOGICAL name of the gateway -> game-server
	// control stream (concrete Redis key "events:kick").
	KickEventStream = "kick"

	// EventSessionSuperseded is the event type carried on KickEventStream.
	EventSessionSuperseded = "session_superseded"

	// GatewayKickStream is the LOGICAL name of the gateway -> gateway control
	// stream (concrete Redis key "events:gateway_kick").
	GatewayKickStream = "gateway_kick"

	// EventGatewaySuperseded is the event type carried on GatewayKickStream.
	EventGatewaySuperseded = "gateway_superseded"
)
