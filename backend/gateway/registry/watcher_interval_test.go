package registry

import (
	"testing"

	"github.com/duycuong/rpg-mmo/shared/constants"
)

// TestWatchPollInterval_ShorterThanHeartbeatTTL pins the one relationship that
// makes the watcher worth running. The registry drops a server that stops
// beating after constants.ServerHeartbeatTTL; the watcher's whole job is to
// notice that death sooner and publish server_down. A poll interval at or above
// the TTL would always lose that race, leaving the watcher a no-op that still
// costs a registry read per server per tick.
func TestWatchPollInterval_ShorterThanHeartbeatTTL(t *testing.T) {
	ttl := constants.ServerHeartbeatTTL
	if watchPollInterval >= ttl {
		t.Fatalf("watchPollInterval = %s must be shorter than constants.ServerHeartbeatTTL = %s; "+
			"a watcher slower than the TTL adds nothing, expiry would always win", watchPollInterval, ttl)
	}
	// Not merely shorter: shorter by enough that a death is caught well inside
	// the window in which the gateway would otherwise keep announcing the dead
	// server's address (the split-map fault of #203).
	const wantRatio = 3
	if got := ttl / watchPollInterval; got < wantRatio {
		t.Fatalf("watchPollInterval = %s gives only a %dx margin against ServerHeartbeatTTL = %s; want at least %dx",
			watchPollInterval, got, ttl, wantRatio)
	}
}
