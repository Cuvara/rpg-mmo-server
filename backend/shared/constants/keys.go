package constants

// Redis key patterns. Use fmt.Sprintf to fill placeholders.
const (
	SessionKeyPrefix  = "session:" // session:{user_id}
	ServerRegistryKey = "servers:" // servers:{map_id}
	EventStreamPrefix = "events:"  // events:{stream_name}

	// GameEventStream is the LOGICAL name of the cross-server gameplay event
	// stream. Publishers (gameserver) and subscribers (gateway relay) must
	// both use this constant; the storage layer adds EventStreamPrefix, so
	// the concrete Redis key is "events:game". Never pre-prefix this value.
	GameEventStream = "game"

	// GatewayKickChannel is the Redis Pub/Sub channel used by gateways to
	// coordinate duplicate-login kicks across instances. A gateway that detects
	// a session owned by a different instance publishes a kick request here;
	// every gateway subscribes and closes the local connection if it owns the
	// user. Message loss is acceptable (the old session expires via TTL), so
	// Pub/Sub rather than Streams is the right transport.
	GatewayKickChannel = "gateway:kick"
)
