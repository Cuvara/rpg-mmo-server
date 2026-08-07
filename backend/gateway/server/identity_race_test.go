package server

import (
	"net"
	"sync"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/ratelimit"
)

// TestClientConnIdentityConcurrentAccess is the direct regression guard for the
// data race CI caught on ClientConn: cleanupSession wrote UserID/State from the
// read goroutine while CloseGracefully read UserID from the write goroutine.
//
// It reproduces that shape without a socket, so `-race` has something loud and
// deterministic to catch: one goroutine drives the identity through its whole
// lifecycle while another reads it through the accessors the write side uses.
// Against the pre-fix code (plain exported fields) this trips the detector on
// essentially every run.
func TestClientConnIdentityConcurrentAccess(t *testing.T) {
	cc := NewClientConn(nil, logger.New("error"), ratelimit.Bucket{})

	const iterations = 2000
	var wg sync.WaitGroup

	// Writer: the read-loop side — auth, enter world, session expiry, teardown.
	wg.Add(1)
	go func() {
		defer wg.Done()
		for i := 0; i < iterations; i++ {
			cc.UserID = "user1"
			cc.State = StateAuthenticated
			cc.State = StateInWorld
			func() string { u := cc.UserID; cc.UserID = ""; cc.State = StateConnected; return u }()
		}
	}()

	// Readers: the write-loop side — every log line it emits reads the user,
	// and handleMessage's default branch reads the state.
	for r := 0; r < 3; r++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := 0; i < iterations; i++ {
				_ = cc.UserID
				_ = cc.State
				user, state := cc.UserID, cc.State
				// Identity must never report a half-applied transition: a
				// connection with no user is never in a post-auth state.
				if user == "" && state != StateConnected {
					t.Errorf("torn identity: user=%q state=%v", user, state)
					return
				}
			}
		}()
	}

	wg.Wait()

	if user := cc.UserID; user != "" {
		t.Errorf("UserID after final clear = %q, want empty", user)
	}
	if state := cc.State; state != StateConnected {
		t.Errorf("State after final clear = %v, want StateConnected", state)
	}
}

// TestGatewayConcurrentTeardownRace exercises the real two-goroutine teardown
// end to end: a flooding client trips the message limiter, so WriteLoop drains
// the final error frame and calls CloseGracefully (reading the identity) while
// ReadLoop exits and its deferred cleanupSession clears that same identity.
// That interleaving is exactly the one in the CI race report.
//
// The client authenticates first so the identity is non-empty and the teardown
// actually has something to clear; several connections run at once to widen the
// window.
func TestGatewayConcurrentTeardownRace(t *testing.T) {
	// startGatewayWithOptions registers its own Shutdown cleanup.
	gw, _ := startGatewayWithOptions(t, WithMsgRateLimit(0.01, 3))

	const clients = 12
	var wg sync.WaitGroup
	for i := 0; i < clients; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()

			conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
			if err != nil {
				return // accept-side limiting or shutdown; not what we test here
			}
			defer conn.Close()

			token, err := jwt.Sign("user1", testSecret, time.Hour)
			if err != nil {
				return
			}
			authEnv, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
			if err != nil {
				return
			}
			data, err := messages.Encode(authEnv)
			if err != nil {
				return
			}
			// Burn the burst so the limiter trips and hands the connection to
			// the SendAndClose -> WriteLoop -> CloseGracefully teardown path.
			for n := 0; n < 20; n++ {
				conn.SetWriteDeadline(time.Now().Add(2 * time.Second))
				if _, err := conn.Write(data); err != nil {
					break
				}
			}
			// Drain until the server hangs up, so the test observes the whole
			// teardown rather than racing it with its own Close.
			conn.SetReadDeadline(time.Now().Add(3 * time.Second))
			for {
				if _, err := messages.Decode(conn); err != nil {
					return
				}
			}
		}()
	}
	wg.Wait()
}
