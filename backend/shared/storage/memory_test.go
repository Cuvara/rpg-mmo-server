package storage

import (
	"context"
	"testing"
	"time"
)

func TestMemoryPlayerStore(t *testing.T) {
	ctx := context.Background()
	store := NewMemoryPlayerStore()

	state := &PlayerState{UserID: "u1", X: 10, Y: 20, HP: 100, MaxHP: 100, MapID: "map_01"}

	// Save
	if err := store.SavePlayer(ctx, state); err != nil {
		t.Fatalf("SavePlayer() error: %v", err)
	}

	// Load
	loaded, err := store.LoadPlayer(ctx, "u1")
	if err != nil {
		t.Fatalf("LoadPlayer() error: %v", err)
	}
	if loaded.X != 10 || loaded.HP != 100 {
		t.Errorf("loaded = %+v, want X=10, HP=100", loaded)
	}

	// Delete
	if err := store.DeletePlayer(ctx, "u1"); err != nil {
		t.Fatalf("DeletePlayer() error: %v", err)
	}
	_, err = store.LoadPlayer(ctx, "u1")
	if err == nil {
		t.Error("LoadPlayer() should fail after delete")
	}
}

func TestMemorySessionStore(t *testing.T) {
	ctx := context.Background()
	store := NewMemorySessionStore()

	// Set and Get
	if err := store.Set(ctx, "s1", []byte("data"), time.Hour); err != nil {
		t.Fatalf("Set() error: %v", err)
	}
	val, err := store.Get(ctx, "s1")
	if err != nil {
		t.Fatalf("Get() error: %v", err)
	}
	if string(val) != "data" {
		t.Errorf("Get() = %q, want %q", string(val), "data")
	}

	// Expired
	if err := store.Set(ctx, "s2", []byte("old"), -time.Second); err != nil {
		t.Fatalf("Set() error: %v", err)
	}
	_, err = store.Get(ctx, "s2")
	if err == nil {
		t.Error("Get() should fail for expired session")
	}

	// Delete
	if err := store.Delete(ctx, "s1"); err != nil {
		t.Fatalf("Delete() error: %v", err)
	}
	_, err = store.Get(ctx, "s1")
	if err == nil {
		t.Error("Get() should fail after delete")
	}
}

func TestMemoryServerRegistry(t *testing.T) {
	ctx := context.Background()
	reg := NewMemoryServerRegistry()

	info := ServerInfo{
		ServerID:    "srv1",
		MapID:       "map_01",
		Addr:        "127.0.0.1:9000",
		Capacity:    100,
		PlayerCount: 0,
	}

	// Register
	if err := reg.Register(ctx, info); err != nil {
		t.Fatalf("Register() error: %v", err)
	}

	// FindByMapID
	servers, err := reg.FindByMapID(ctx, "map_01")
	if err != nil {
		t.Fatalf("FindByMapID() error: %v", err)
	}
	if len(servers) != 1 || servers[0].ServerID != "srv1" {
		t.Errorf("FindByMapID() = %+v, want 1 server with ID srv1", servers)
	}

	// FindByMapID — wrong map
	servers, err = reg.FindByMapID(ctx, "map_99")
	if err != nil {
		t.Fatalf("FindByMapID() error: %v", err)
	}
	if len(servers) != 0 {
		t.Errorf("FindByMapID(map_99) = %d servers, want 0", len(servers))
	}

	// UpdatePlayerCount
	if err := reg.UpdatePlayerCount(ctx, "srv1", 50); err != nil {
		t.Fatalf("UpdatePlayerCount() error: %v", err)
	}
	servers, _ = reg.FindByMapID(ctx, "map_01")
	if servers[0].PlayerCount != 50 {
		t.Errorf("PlayerCount = %d, want 50", servers[0].PlayerCount)
	}

	// Deregister
	if err := reg.Deregister(ctx, "srv1"); err != nil {
		t.Fatalf("Deregister() error: %v", err)
	}
	servers, _ = reg.FindByMapID(ctx, "map_01")
	if len(servers) != 0 {
		t.Errorf("after Deregister, found %d servers", len(servers))
	}
}

func TestMemoryEventStream(t *testing.T) {
	ctx := context.Background()
	stream := NewMemoryEventStream()

	received := make(chan Event, 1)
	if err := stream.Subscribe(ctx, "test_stream", func(e Event) {
		received <- e
	}); err != nil {
		t.Fatalf("Subscribe() error: %v", err)
	}

	event := Event{Type: "boss_killed", Payload: []byte(`{"boss":"dragon"}`)}
	if err := stream.Publish(ctx, "test_stream", event); err != nil {
		t.Fatalf("Publish() error: %v", err)
	}

	select {
	case e := <-received:
		if e.Type != "boss_killed" {
			t.Errorf("event type = %q, want %q", e.Type, "boss_killed")
		}
	case <-time.After(time.Second):
		t.Fatal("timeout waiting for event")
	}

	// Close
	if err := stream.Close(); err != nil {
		t.Fatalf("Close() error: %v", err)
	}
	if err := stream.Publish(ctx, "test_stream", event); err == nil {
		t.Error("Publish() should fail after Close()")
	}
}
