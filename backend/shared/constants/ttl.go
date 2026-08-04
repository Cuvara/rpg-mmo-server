package constants

import "time"

const (
	SessionTTL        = 3600 * time.Second // 1 hour
	EntityHoldTTL     = 30 * time.Second   // hold entity after disconnect (map)
	DungeonHoldTTL    = 60 * time.Second   // hold entity after disconnect (dungeon)
	ServerHeartbeatTTL = 15 * time.Second  // server must heartbeat within this
	JoinTokenTTL      = 30 * time.Second   // join token validity
)
