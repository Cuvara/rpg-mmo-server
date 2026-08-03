package constants

// Redis key patterns. Use fmt.Sprintf to fill placeholders.
const (
	SessionKeyPrefix   = "session:"          // session:{user_id}
	ServerRegistryKey  = "servers:"          // servers:{map_id}
	PlayerLocationKey  = "player:location:"  // player:location:{user_id}
	EventStreamPrefix  = "events:"           // events:{stream_name}
)
