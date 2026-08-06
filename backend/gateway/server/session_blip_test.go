package server

import (
	"context"
	"errors"
	"net"
	"sync/atomic"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/storage/redisstore"
)

// blipStore wraps a SessionStore and can be switched into "infrastructure
// down" mode, where every operation returns a transport error rather than
// ErrNotFound. That distinction is the whole of G6.
type blipStore struct {
	inner storage.SessionStore
	down  atomic.Bool
}

var errRedisDown = errors.New("dial tcp 10.0.1.7:6379: connect: connection refused")

func (b *blipStore) Set(ctx context.Context, key string, value []byte, ttl time.Duration) error {
	if b.down.Load() {
		return errRedisDown
	}
	return b.inner.Set(ctx, key, value, ttl)
}

func (b *blipStore) Get(ctx context.Context, key string) ([]byte, error) {
	if b.down.Load() {
		return nil, errRedisDown
	}
	return b.inner.Get(ctx, key)
}

func (b *blipStore) Delete(ctx context.Context, key string) error {
	if b.down.Load() {
		return errRedisDown
	}
	return b.inner.Delete(ctx, key)
}

func (b *blipStore) Refresh(ctx context.Context, key string, ttl time.Duration) error {
	if b.down.Load() {
		return errRedisDown
	}
	return b.inner.Refresh(ctx, key, ttl)
}

func (b *blipStore) Close() error { return nil }

// TestRedisBlipDoesNotDeauthPlayer is the G6 regression guard.
//
// Before this change checkSession treated `err != nil` and "key missing" as the
// same thing, so a Redis hiccup told a live, correctly authenticated player
// "session expired" and dropped them to StateConnected — a forced re-login for
// every online player, caused by an outage in a dependency that gameplay does
// not even use.
func TestRedisBlipDoesNotDeauthPlayer(t *testing.T) {
	store := &blipStore{inner: storage.NewMemorySessionStore()}
	gw := startGatewayWithSessionStore(t, store)

	conn, err := net.Dial("tcp", gw.Addr())
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	if resp := authenticate(t, conn, "user1"); !resp.OK {
		t.Fatalf("auth failed: %q", resp.Error)
	}

	// Redis falls over *after* the player is authenticated.
	store.down.Store(true)

	resp := enterWorld(t, conn, "map_forest")

	// The player must NOT be told their session expired. Whether the request
	// succeeds depends on the registry (also memory-backed here); what matters
	// is that the failure mode is never a spurious de-auth.
	if resp.Error == "session expired" {
		t.Error("a Redis outage reported 'session expired' to a live player (G6 regression)")
	}
	if resp.Error == "not authenticated" {
		t.Error("a Redis outage de-authenticated a live player (G6 regression)")
	}
}

// TestExpiredSessionStillRejected is the other half of G6: failing open on
// infrastructure errors must not weaken the real expiry path. A session the
// store affirmatively reports as gone must still be rejected.
func TestExpiredSessionStillRejected(t *testing.T) {
	store := &blipStore{inner: storage.NewMemorySessionStore()}
	gw := startGatewayWithSessionStore(t, store)

	conn, err := net.Dial("tcp", gw.Addr())
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	if resp := authenticate(t, conn, "user1"); !resp.OK {
		t.Fatalf("auth failed: %q", resp.Error)
	}

	// Store is healthy; the session is genuinely destroyed.
	if derr := store.Delete(context.Background(), session.SessionKey("user1")); derr != nil {
		t.Fatalf("delete session: %v", derr)
	}

	if resp := enterWorld(t, conn, "map_forest"); resp.Error != "session expired" {
		t.Errorf("Error = %q, want %q — a genuinely gone session must still be rejected",
			resp.Error, "session expired")
	}
}

// TestMemoryStoreReportsErrNotFound pins the interface contract the G6 fix
// depends on. redisstore already returned storage.ErrNotFound; the memory store
// returned a bare fmt.Errorf, so the two backends disagreed on what "missing"
// looks like and any errors.Is-based classification silently misread the memory
// store as an infrastructure failure.
func TestMemoryStoreReportsErrNotFound(t *testing.T) {
	mem := storage.NewMemorySessionStore()
	if _, err := mem.Get(context.Background(), "session:absent"); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("memory Get(missing) = %v, want wrapped storage.ErrNotFound", err)
	}

	mr := miniredis.RunT(t)
	rs := redisstore.NewSessionStore(mr.Addr(), "")
	t.Cleanup(func() { rs.Close() })
	if _, err := rs.Get(context.Background(), "session:absent"); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("redis Get(missing) = %v, want wrapped storage.ErrNotFound", err)
	}
}

// startGatewayWithSessionStore starts a gateway over a caller-supplied session
// store so a test can fault-inject the store underneath a live connection.
func startGatewayWithSessionStore(t *testing.T, store storage.SessionStore) *Gateway {
	t.Helper()

	reg := storage.NewMemoryServerRegistry()
	if err := reg.Register(context.Background(), storage.ServerInfo{
		ServerID: "srv1", MapID: "map_forest", Addr: "10.0.0.1:9000",
		Capacity: 100, PlayerCount: 10,
	}); err != nil {
		t.Fatalf("register server: %v", err)
	}

	gw := New(session.NewSessionManager(store), registry.NewRegistryService(reg),
		testSecret, logger.New("error"))

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
	t.Fatal("gateway did not start in time")
	return nil
}
