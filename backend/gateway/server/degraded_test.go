package server

import (
	"context"
	"errors"
	"fmt"
	"net"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/events"
	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	gameerrors "github.com/duycuong/rpg-mmo/shared/errors"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// --- G3: the gateway must start degraded when the relay cannot reach Redis ---

// flakyStream fails Subscribe the first failCount times, then succeeds. It
// stands in for a Redis that is down at boot and comes back later.
type flakyStream struct {
	mu        sync.Mutex
	failCount int
	attempts  int
	subbed    bool
}

func (f *flakyStream) Subscribe(_ context.Context, _ string, _ func(storage.Event)) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.attempts++
	if f.attempts <= f.failCount {
		return errors.New("dial tcp 127.0.0.1:6379: connect: connection refused")
	}
	f.subbed = true
	return nil
}

func (f *flakyStream) Publish(_ context.Context, _ string, _ storage.Event) error { return nil }
func (f *flakyStream) Close() error                                               { return nil }

func (f *flakyStream) Attempts() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.attempts
}

func (f *flakyStream) Subscribed() bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.subbed
}

// TestRelayStartFailureDoesNotKillGateway is the G3 regression guard. Before
// this change relay.Start's error propagated out of Run, main exited 1 and the
// pod crash-looped — so a Redis outage took down auth and map assignment, which
// do not need the relay at all.
func TestRelayStartFailureDoesNotKillGateway(t *testing.T) {
	stream := &flakyStream{failCount: 1000} // never recovers during this test
	gw := startGatewayWithStream(t, stream)

	// The gateway must be serving despite the relay being down.
	if gw.Addr() == "" {
		t.Fatal("gateway did not bind a listener when the relay failed to start")
	}
	if gw.RelayUp() {
		t.Error("RelayUp() = true, want false while the relay cannot subscribe")
	}

	// And it must actually answer traffic: a full auth handshake over the wire.
	conn, err := net.Dial("tcp", gw.Addr())
	if err != nil {
		t.Fatalf("dial degraded gateway: %v", err)
	}
	defer conn.Close()

	if resp := authenticate(t, conn, "user1"); !resp.OK {
		t.Errorf("auth against a relay-degraded gateway failed: %q", resp.Error)
	}
}

// TestRelayRecoversAfterRedisReturns proves the retry loop is real: once the
// stream stops failing, the relay subscribes without a restart. This is the
// half that a "just log and give up" fix would fail.
func TestRelayRecoversAfterRedisReturns(t *testing.T) {
	stream := &flakyStream{failCount: 1}
	gw := startGatewayWithStream(t, stream)

	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		if gw.RelayUp() {
			break
		}
		time.Sleep(20 * time.Millisecond)
	}

	if !gw.RelayUp() {
		t.Fatalf("relay never recovered after %d attempts", stream.Attempts())
	}
	if !stream.Subscribed() {
		t.Error("relay reports up but the stream was never subscribed")
	}
}

// startGatewayWithStream starts a gateway whose event relay is backed by the
// given stream, and waits for the listener to bind. It must NOT wait for the
// relay: the whole point is that the gateway serves without it.
func startGatewayWithStream(t *testing.T, stream storage.EventStream) *Gateway {
	t.Helper()

	sessions := session.NewSessionManager(storage.NewMemorySessionStore())
	reg := registry.NewRegistryService(storage.NewMemoryServerRegistry())

	var gw *Gateway
	relay := events.NewRelay(stream, events.DefaultStream,
		events.SinkFunc(func(ev storage.Event) { gw.OnEvent(ev) }), logger.New("error"))
	gw = New(sessions, reg, testSecret, logger.New("error"), WithEventRelay(relay))

	go func() {
		if err := gw.Run("127.0.0.1:0"); err != nil {
			select {
			case <-gw.done:
			default:
				t.Logf("gateway run error: %v", err)
			}
		}
	}()
	for i := 0; i < 200; i++ {
		if gw.Addr() != "" {
			t.Cleanup(gw.Shutdown)
			return gw
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("gateway did not bind a listener; a failing relay must not block startup")
	return nil
}

// --- G7: internal errors must not reach the client verbatim ---

// TestClientSafeAssignError is the G7 guard. The raw chain embeds internal
// addresses (`dial tcp 10.0.1.7:6379`), so anything not explicitly classified
// must collapse to a generic message.
func TestClientSafeAssignError(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want string
	}{
		{
			name: "no server available stays specific",
			err:  fmt.Errorf("assign map: %w map_01", registry.ErrNoServerAvailable),
			want: msgNoServerAvailable,
		},
		{
			// "your server is booting" (retry shortly) must not collapse into
			// "this map is full/unavailable" (do not retry).
			name: "allocated server still starting is its own message",
			err:  fmt.Errorf("assign map: allocated server for map map_01: %w: gs-1 did not register within 20s", registry.ErrServerStarting),
			want: msgServerStarting,
		},
		{
			name: "not implemented stays specific",
			err:  fmt.Errorf("assign map: %w", gameerrors.New(gameerrors.ErrNotImplemented, "dungeon transfer")),
			want: msgNotImplemented,
		},
		{
			name: "redis dial error is masked",
			err:  errors.New("assign map: find servers: dial tcp 10.0.1.7:6379: connect: connection refused"),
			want: msgInternalError,
		},
		{
			name: "allocator error with cluster host is masked",
			err:  errors.New("assign map: allocate: Post \"https://agones-allocator.agones-system.svc:443\": no route to host"),
			want: msgInternalError,
		},
		{
			name: "token signing failure is masked",
			err:  errors.New("assign map: generate join token: keyring is empty"),
			want: msgInternalError,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := clientSafeAssignError(tt.err)
			if got != tt.want {
				t.Errorf("clientSafeAssignError() = %q, want %q", got, tt.want)
			}
			// Whatever the classification, the message must never carry
			// anything resembling an internal address.
			for _, leak := range []string{"10.0.1.7", "6379", "svc:443", "dial tcp", "connection refused"} {
				if strings.Contains(got, leak) {
					t.Errorf("client message %q leaks %q", got, leak)
				}
			}
		})
	}
}

// TestNoServerAvailableIsMatchable guards the sentinel itself: FindServer wraps
// it, so string-matching would silently regress to the generic message.
func TestNoServerAvailableIsMatchable(t *testing.T) {
	err := fmt.Errorf("assign map: %w map_01", registry.ErrNoServerAvailable)
	if !errors.Is(err, registry.ErrNoServerAvailable) {
		t.Fatal("wrapped ErrNoServerAvailable is not matchable with errors.Is")
	}

	// The two conditions must never be confused for each other: one says
	// "do not retry", the other says "retry shortly".
	starting := fmt.Errorf("assign map: %w: gs-1", registry.ErrServerStarting)
	if !errors.Is(starting, registry.ErrServerStarting) {
		t.Fatal("wrapped ErrServerStarting is not matchable with errors.Is")
	}
	if errors.Is(starting, registry.ErrNoServerAvailable) || errors.Is(err, registry.ErrServerStarting) {
		t.Fatal("ErrServerStarting and ErrNoServerAvailable must be distinct sentinels")
	}
}

// TestGameErrorIsUnwraps covers the shared/errors fix this depended on: a
// wrapped GameError must still classify, or every wrapped GameError falls
// through to the default (generic) branch.
func TestGameErrorIsUnwraps(t *testing.T) {
	base := gameerrors.New(gameerrors.ErrNotImplemented, "dungeon transfer")
	wrapped := fmt.Errorf("assign map: %w", base)

	if !gameerrors.Is(wrapped, gameerrors.ErrNotImplemented) {
		t.Error("gameerrors.Is() = false for a wrapped GameError, want true")
	}
	if gameerrors.Is(wrapped, gameerrors.ErrServerFull) {
		t.Error("gameerrors.Is() matched the wrong code")
	}
}
