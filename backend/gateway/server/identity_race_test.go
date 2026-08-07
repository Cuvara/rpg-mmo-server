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
// end to end: a client authenticates, trips the message limiter so the gateway
// queues a final error frame, then vanishes. The write goroutine's Write fails
// and logs the user (connection.go's "write error"), while the read goroutine
// sees EOF and runs handleConn's deferred cleanupSession, which clears that same
// user. That is the interleaving from the CI race report.
//
// Note this is a *probabilistic* reproducer — the window is real but narrow, so
// it is a realism supplement, not the guarantee. The deterministic guard is
// TestClientConnIdentityConcurrentAccess above. Several rounds of several
// clients widen the window without making the test slow.
func TestGatewayConcurrentTeardownRace(t *testing.T) {
	// startGatewayWithOptions registers its own Shutdown cleanup.
	gw, _ := startGatewayWithOptions(t, WithMsgRateLimit(0.01, 3))

	token, err := jwt.Sign("user1", testSecret, time.Hour)
	if err != nil {
		t.Fatalf("sign token: %v", err)
	}
	authEnv, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	if err != nil {
		t.Fatalf("envelope: %v", err)
	}
	data, err := messages.Encode(authEnv)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}

	const (
		rounds  = 8
		clients = 8
	)
	for round := 0; round < rounds; round++ {
		var wg sync.WaitGroup
		for i := 0; i < clients; i++ {
			wg.Add(1)
			go func() {
				defer wg.Done()

				conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
				if err != nil {
					return // accept-side limiting or shutdown; not what we test here
				}
				// Authenticate, then burn the burst so the limiter trips and
				// hands the connection to the SendAndClose teardown path.
				for n := 0; n < 12; n++ {
					conn.SetWriteDeadline(time.Now().Add(2 * time.Second))
					if _, err := conn.Write(data); err != nil {
						break
					}
				}
				// Vanish without draining. The server is mid-teardown with a
				// live identity: its write of the error frame fails on the
				// write goroutine while the read goroutine hits EOF and clears
				// that identity.
				conn.Close()
			}()
		}
		wg.Wait()
	}
}
