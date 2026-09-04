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

	// KickEventStream is the LOGICAL name of the gateway -> game-server
	// control stream (concrete Redis key "events:kick"). The gateway XADDs a
	// SessionSupersededEvent here on duplicate login; every game server
	// consumes it through its OWN consumer group (one group per server id, so
	// the stream is a broadcast) and acts only on events whose server_id names
	// it. One shared stream rather than a key per server: server ids churn
	// (dungeon instances, pod restarts) and this Redis runs noeviction
	// (ADR-4), so per-server keys would accumulate without bound while a
	// single stream stays trimmed by the publisher's MAXLEN. Streams with
	// consumer-group ACK per ADR-5 — never Pub/Sub (see #211 for the shape
	// that was deleted for describing exactly that wrong transport).
	KickEventStream = "kick"

	// EventSessionSuperseded is the event type carried on KickEventStream: a
	// newer login for user_id has superseded the session that joined server_id
	// with join-token jti. The C# consumer mirrors this literal
	// (GameServer/Events/KickEvents.cs); the payload contract is documented in
	// gameserver-dotnet/docs/API.md.
	EventSessionSuperseded = "session_superseded"
)
