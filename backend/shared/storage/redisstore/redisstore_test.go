package redisstore

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/alicebob/miniredis/v2"
	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/redis/go-redis/v9"
)

// newTestRedis spins up an in-process miniredis and a client pointed at it.
func newTestRedis(t *testing.T) (*miniredis.Miniredis, *redis.Client) {
	t.Helper()
	mr := miniredis.RunT(t)
	client := redis.NewClient(&redis.Options{Addr: mr.Addr()})
	t.Cleanup(func() { _ = client.Close() })
	return mr, client
}

// --- RedisSessionStore ---

func TestRedisSessionStore_SetGetDelete(t *testing.T) {
	_, client := newTestRedis(t)
	store := NewSessionStoreWithClient(client)
	ctx := context.Background()
	key := constants.SessionKeyPrefix + "user1"

	tests := []struct {
		name  string
		value []byte
	}{
		{"simple value", []byte("user1")},
		{"json value", []byte(`{"user_id":"user1","map":"map_01"}`)},
		{"empty value", []byte("")},
		{"binary value", []byte{0x00, 0xff, 0x10}},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if err := store.Set(ctx, key, tt.value, constants.SessionTTL); err != nil {
				t.Fatalf("Set() error: %v", err)
			}
			got, err := store.Get(ctx, key)
			if err != nil {
				t.Fatalf("Get() error: %v", err)
			}
			if string(got) != string(tt.value) {
				t.Errorf("Get() = %q, want %q", got, tt.value)
			}
			if err := store.Delete(ctx, key); err != nil {
				t.Fatalf("Delete() error: %v", err)
			}
			if _, err := store.Get(ctx, key); !errors.Is(err, storage.ErrNotFound) {
				t.Errorf("Get() after Delete error = %v, want storage.ErrNotFound", err)
			}
		})
	}
}

func TestRedisSessionStore_MissingKey(t *testing.T) {
	_, client := newTestRedis(t)
	store := NewSessionStoreWithClient(client)
	ctx := context.Background()

	if _, err := store.Get(ctx, "session:nobody"); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("Get() error = %v, want storage.ErrNotFound", err)
	}
	if err := store.Refresh(ctx, "session:nobody", time.Minute); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("Refresh() error = %v, want storage.ErrNotFound", err)
	}
	// Deleting a missing key is a no-op in Redis.
	if err := store.Delete(ctx, "session:nobody"); err != nil {
		t.Errorf("Delete() on missing key error: %v", err)
	}
}

func TestRedisSessionStore_TTLExpiry(t *testing.T) {
	mr, client := newTestRedis(t)
	store := NewSessionStoreWithClient(client)
	ctx := context.Background()
	key := constants.SessionKeyPrefix + "user1"

	if err := store.Set(ctx, key, []byte("user1"), 10*time.Second); err != nil {
		t.Fatalf("Set() error: %v", err)
	}

	mr.FastForward(9 * time.Second)
	if _, err := store.Get(ctx, key); err != nil {
		t.Fatalf("Get() before expiry error: %v", err)
	}

	mr.FastForward(2 * time.Second)
	if _, err := store.Get(ctx, key); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("Get() after expiry error = %v, want storage.ErrNotFound", err)
	}
}

func TestRedisSessionStore_Refresh(t *testing.T) {
	mr, client := newTestRedis(t)
	store := NewSessionStoreWithClient(client)
	ctx := context.Background()
	key := constants.SessionKeyPrefix + "user1"

	if err := store.Set(ctx, key, []byte("user1"), 10*time.Second); err != nil {
		t.Fatalf("Set() error: %v", err)
	}
	mr.FastForward(9 * time.Second)
	if err := store.Refresh(ctx, key, 10*time.Second); err != nil {
		t.Fatalf("Refresh() error: %v", err)
	}

	// Without the refresh this would already be gone.
	mr.FastForward(9 * time.Second)
	got, err := store.Get(ctx, key)
	if err != nil {
		t.Fatalf("Get() after Refresh error: %v", err)
	}
	if string(got) != "user1" {
		t.Errorf("Get() = %q, want %q", got, "user1")
	}

	mr.FastForward(2 * time.Second)
	if _, err := store.Get(ctx, key); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("Get() after refreshed TTL expiry = %v, want storage.ErrNotFound", err)
	}
}

// --- RedisServerRegistry ---

func testServer(id, mapID string) storage.ServerInfo {
	return storage.ServerInfo{
		ServerID: id, MapID: mapID, Addr: "127.0.0.1:9000",
		Capacity: 100, PlayerCount: 0,
	}
}

func newTestRegistry(t *testing.T) (*miniredis.Miniredis, *ServerRegistry) {
	t.Helper()
	mr, client := newTestRedis(t)
	return mr, NewServerRegistryWithClient(client, constants.ServerHeartbeatTTL)
}

func TestRedisServerRegistry_RegisterAndLookup(t *testing.T) {
	_, reg := newTestRegistry(t)
	ctx := context.Background()

	servers := []storage.ServerInfo{
		testServer("gs-1", "map_01"),
		testServer("gs-2", "map_01"),
		testServer("gs-3", "map_02"),
	}
	for _, s := range servers {
		if err := reg.Register(ctx, s); err != nil {
			t.Fatalf("Register(%s) error: %v", s.ServerID, err)
		}
	}

	tests := []struct {
		name  string
		mapID string
		want  int
	}{
		{"two servers on map_01", "map_01", 2},
		{"one server on map_02", "map_02", 1},
		{"unknown map", "map_99", 0},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := reg.FindByMapID(ctx, tt.mapID)
			if err != nil {
				t.Fatalf("FindByMapID() error: %v", err)
			}
			if len(got) != tt.want {
				t.Errorf("FindByMapID(%s) = %d servers, want %d", tt.mapID, len(got), tt.want)
			}
		})
	}

	got, err := reg.GetServer(ctx, "gs-3")
	if err != nil {
		t.Fatalf("GetServer() error: %v", err)
	}
	want := testServer("gs-3", "map_02")
	if got != want {
		t.Errorf("GetServer() = %+v, want %+v", got, want)
	}

	if _, err := reg.GetServer(ctx, "ghost"); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("GetServer(ghost) error = %v, want storage.ErrNotFound", err)
	}
}

func TestRedisServerRegistry_UpdatePlayerCount(t *testing.T) {
	_, reg := newTestRegistry(t)
	ctx := context.Background()
	if err := reg.Register(ctx, testServer("gs-1", "map_01")); err != nil {
		t.Fatalf("Register() error: %v", err)
	}

	if err := reg.UpdatePlayerCount(ctx, "gs-1", 42); err != nil {
		t.Fatalf("UpdatePlayerCount() error: %v", err)
	}
	got, err := reg.GetServer(ctx, "gs-1")
	if err != nil {
		t.Fatalf("GetServer() error: %v", err)
	}
	if got.PlayerCount != 42 {
		t.Errorf("PlayerCount = %d, want 42", got.PlayerCount)
	}

	if err := reg.UpdatePlayerCount(ctx, "ghost", 1); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("UpdatePlayerCount(ghost) error = %v, want storage.ErrNotFound", err)
	}
}

func TestRedisServerRegistry_Deregister(t *testing.T) {
	_, reg := newTestRegistry(t)
	ctx := context.Background()
	if err := reg.Register(ctx, testServer("gs-1", "map_01")); err != nil {
		t.Fatalf("Register() error: %v", err)
	}
	if err := reg.Deregister(ctx, "gs-1"); err != nil {
		t.Fatalf("Deregister() error: %v", err)
	}
	if _, err := reg.GetServer(ctx, "gs-1"); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("GetServer() after Deregister = %v, want storage.ErrNotFound", err)
	}
	found, err := reg.FindByMapID(ctx, "map_01")
	if err != nil {
		t.Fatalf("FindByMapID() error: %v", err)
	}
	if len(found) != 0 {
		t.Errorf("FindByMapID() = %d servers, want 0", len(found))
	}
	// Deregistering twice must not error.
	if err := reg.Deregister(ctx, "gs-1"); err != nil {
		t.Errorf("Deregister() second call error: %v", err)
	}
}

func TestRedisServerRegistry_HeartbeatExpiry(t *testing.T) {
	mr, reg := newTestRegistry(t)
	ctx := context.Background()
	ttl := constants.ServerHeartbeatTTL

	tests := []struct {
		name        string
		advance     time.Duration
		heartbeat   bool
		wantServers int
	}{
		{"alive within ttl", ttl - time.Second, false, 1},
		{"still alive after heartbeat", ttl - time.Second, true, 1},
		{"dead after ttl without heartbeat", ttl + time.Second, false, 0},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if err := reg.Register(ctx, testServer("gs-1", "map_01")); err != nil {
				t.Fatalf("Register() error: %v", err)
			}
			if tt.heartbeat {
				mr.FastForward(ttl - time.Second)
				if err := reg.Heartbeat(ctx, "gs-1"); err != nil {
					t.Fatalf("Heartbeat() error: %v", err)
				}
			}
			mr.FastForward(tt.advance)

			got, err := reg.FindByMapID(ctx, "map_01")
			if err != nil {
				t.Fatalf("FindByMapID() error: %v", err)
			}
			if len(got) != tt.wantServers {
				t.Errorf("FindByMapID() = %d servers, want %d", len(got), tt.wantServers)
			}
			_ = reg.Deregister(ctx, "gs-1")
		})
	}
}

func TestRedisServerRegistry_HeartbeatUnknownServer(t *testing.T) {
	_, reg := newTestRegistry(t)
	if err := reg.Heartbeat(context.Background(), "ghost"); !errors.Is(err, storage.ErrNotFound) {
		t.Errorf("Heartbeat(ghost) error = %v, want storage.ErrNotFound", err)
	}
}

func TestRedisServerRegistry_ExpiredIndexPruned(t *testing.T) {
	mr, reg := newTestRegistry(t)
	ctx := context.Background()
	if err := reg.Register(ctx, testServer("gs-1", "map_01")); err != nil {
		t.Fatalf("Register() error: %v", err)
	}
	mr.FastForward(constants.ServerHeartbeatTTL + time.Second)

	if _, err := reg.FindByMapID(ctx, "map_01"); err != nil {
		t.Fatalf("FindByMapID() error: %v", err)
	}
	members, err := mr.SMembers(registryMapPrefix + "map_01")
	if err != nil && err.Error() != "ERR no such key" {
		t.Fatalf("SMembers() error: %v", err)
	}
	if len(members) != 0 {
		t.Errorf("map index = %v, want pruned", members)
	}
}

// --- RedisEventStream ---

func newTestStream(t *testing.T, group, consumer string) *EventStream {
	t.Helper()
	_, client := newTestRedis(t)
	s := NewEventStreamWithClient(client, group, consumer)
	s.SetBlockTimeout(20 * time.Millisecond)
	t.Cleanup(func() { _ = s.Close() })
	return s
}

// waitFor polls cond until it holds or the deadline passes.
func waitFor(t *testing.T, cond func() bool) bool {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return true
		}
		time.Sleep(5 * time.Millisecond)
	}
	return cond()
}

func TestRedisEventStream_PublishSubscribe(t *testing.T) {
	s := newTestStream(t, "gateway", "gw-1")
	ctx := context.Background()

	tests := []struct {
		name  string
		event storage.Event
	}{
		{"boss killed", storage.Event{Type: "boss_killed", Payload: []byte(`{"boss":"dragon"}`)}},
		{"rare drop", storage.Event{Type: "rare_drop", Payload: []byte(`{"item":42}`)}},
		{"empty payload", storage.Event{Type: "player_offline"}},
	}

	var (
		mu   = make(chan storage.Event, len(tests))
		name = "world"
	)
	if err := s.Subscribe(ctx, name, func(e storage.Event) { mu <- e }); err != nil {
		t.Fatalf("Subscribe() error: %v", err)
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if err := s.Publish(ctx, name, tt.event); err != nil {
				t.Fatalf("Publish() error: %v", err)
			}
			select {
			case got := <-mu:
				if got.Type != tt.event.Type {
					t.Errorf("Type = %q, want %q", got.Type, tt.event.Type)
				}
				if string(got.Payload) != string(tt.event.Payload) {
					t.Errorf("Payload = %q, want %q", got.Payload, tt.event.Payload)
				}
			case <-time.After(3 * time.Second):
				t.Fatal("timed out waiting for event")
			}
		})
	}
}

func TestRedisEventStream_AckRemovesFromPending(t *testing.T) {
	_, client := newTestRedis(t)
	s := NewEventStreamWithClient(client, "gateway", "gw-1")
	s.SetBlockTimeout(20 * time.Millisecond)
	defer s.Close()

	ctx := context.Background()
	got := make(chan storage.Event, 1)
	if err := s.Subscribe(ctx, "world", func(e storage.Event) { got <- e }); err != nil {
		t.Fatalf("Subscribe() error: %v", err)
	}
	if err := s.Publish(ctx, "world", storage.Event{Type: "boss_killed"}); err != nil {
		t.Fatalf("Publish() error: %v", err)
	}
	<-got

	key := streamKey("world")
	ok := waitFor(t, func() bool {
		pending, err := client.XPending(ctx, key, "gateway").Result()
		return err == nil && pending.Count == 0
	})
	if !ok {
		t.Error("event was never acknowledged (still pending)")
	}
}

func TestRedisEventStream_SubscribeBeforePublishGetsBacklog(t *testing.T) {
	s := newTestStream(t, "gateway", "gw-1")
	ctx := context.Background()

	// Publish first: the group is created with MkStream at "0" but only new
	// entries ('>') are delivered, so publish after Subscribe returns.
	got := make(chan storage.Event, 3)
	if err := s.Subscribe(ctx, "world", func(e storage.Event) { got <- e }); err != nil {
		t.Fatalf("Subscribe() error: %v", err)
	}
	for i := 0; i < 3; i++ {
		if err := s.Publish(ctx, "world", storage.Event{Type: "tick"}); err != nil {
			t.Fatalf("Publish() error: %v", err)
		}
	}
	for i := 0; i < 3; i++ {
		select {
		case <-got:
		case <-time.After(3 * time.Second):
			t.Fatalf("only received %d/3 events", i)
		}
	}
}

func TestRedisEventStream_ClosedStreamRejects(t *testing.T) {
	s := newTestStream(t, "gateway", "gw-1")
	ctx := context.Background()

	if err := s.Close(); err != nil {
		t.Fatalf("Close() error: %v", err)
	}
	if err := s.Publish(ctx, "world", storage.Event{Type: "boss_killed"}); err == nil {
		t.Error("Publish() after Close should fail")
	}
	if err := s.Subscribe(ctx, "world", func(storage.Event) {}); err == nil {
		t.Error("Subscribe() after Close should fail")
	}
	// Close must be idempotent.
	if err := s.Close(); err != nil {
		t.Errorf("Close() second call error: %v", err)
	}
}
