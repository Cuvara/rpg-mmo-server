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
)
