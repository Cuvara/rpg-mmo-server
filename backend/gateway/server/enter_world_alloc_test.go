package server

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/gateway/transfer"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// countingAllocator stands in for the Agones allocator: it records calls and
// returns a canned GameServer (name == pod name == server id).
type countingAllocator struct {
	info storage.ServerInfo
	err  error
	hits atomic.Int32
}

func (c *countingAllocator) AllocateServer(_ context.Context, mapID string) (storage.ServerInfo, error) {
	c.hits.Add(1)
	if c.err != nil {
		return storage.ServerInfo{}, c.err
	}
	info := c.info
	info.MapID = mapID
	return info, nil
}

// startGatewayWithAllocator starts a gateway whose registry knows about
// map_forest only, so any other map has to go through the allocator.
func startGatewayWithAllocator(t *testing.T, alloc registry.Allocator) *Gateway {
	t.Helper()

	serverRegistry := storage.NewMemoryServerRegistry()
	if err := serverRegistry.Register(context.Background(), storage.ServerInfo{
		ServerID: "srv1", MapID: "map_forest", Addr: "10.0.0.1:9000", Capacity: 100,
	}); err != nil {
		t.Fatalf("register: %v", err)
	}

	gw := New(
		session.NewSessionManager(storage.NewMemorySessionStore()),
		registry.NewRegistryServiceWithAllocator(serverRegistry, alloc),
		testSecret,
		logger.New("error"),
	)
	go func() { _ = gw.Run("127.0.0.1:0") }()
	for i := 0; i < 50 && gw.Addr() == ""; i++ {
		time.Sleep(10 * time.Millisecond)
	}
	if gw.Addr() == "" {
		t.Fatal("gateway did not start in time")
	}
	t.Cleanup(gw.Shutdown)
	return gw
}

func TestGateway_EnterWorldAllocatesUnservedMap(t *testing.T) {
	allocated := storage.ServerInfo{
		ServerID: "map-servers-dev-xjh7p-6ndtl", // == GameServer/pod name
		Addr:     "192.168.65.3:7257",
		Capacity: 100,
	}

	tests := []struct {
		name      string
		mapID     string
		alloc     *countingAllocator
		wantAddr  string
		wantHits  int32
		wantError bool
	}{
		{
			name:     "unserved map triggers allocation",
			mapID:    "map_desert",
			alloc:    &countingAllocator{info: allocated},
			wantAddr: "192.168.65.3:7257",
			wantHits: 1,
		},
		{
			name:     "served map does not allocate",
			mapID:    "map_forest",
			alloc:    &countingAllocator{info: allocated},
			wantAddr: "10.0.0.1:9000",
			wantHits: 0,
		},
		{
			name:      "allocation failure surfaces to the client",
			mapID:     "map_desert",
			alloc:     &countingAllocator{err: errors.New("fleet exhausted")},
			wantHits:  1,
			wantError: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			gw := startGatewayWithAllocator(t, tt.alloc)

			conn := dialGateway(t, gw)
			defer conn.Close()

			token, _ := jwt.Sign("user1", testSecret, time.Hour)
			authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
			sendEnvelope(t, conn, authEnv)
			readEnvelope(t, conn)

			enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: tt.mapID})
			sendEnvelope(t, conn, enterEnv)

			var resp messages.EnterWorldResponse
			if err := readEnvelope(t, conn).UnmarshalPayload(&resp); err != nil {
				t.Fatalf("unmarshal: %v", err)
			}

			if got := tt.alloc.hits.Load(); got != tt.wantHits {
				t.Errorf("allocator hits = %d, want %d", got, tt.wantHits)
			}
			if tt.wantError {
				if resp.Error == "" {
					t.Fatal("expected an error response")
				}
				return
			}
			if resp.Error != "" {
				t.Fatalf("unexpected error: %s", resp.Error)
			}
			if resp.ServerAddr != tt.wantAddr {
				t.Errorf("ServerAddr = %q, want %q", resp.ServerAddr, tt.wantAddr)
			}
			if resp.JoinToken == "" {
				t.Fatal("JoinToken should not be empty")
			}
			// Server-id contract: the join token's sid must be the id the target
			// gameserver registers itself as (the GameServer/pod name).
			_, sid, err := transfer.ValidateJoinToken(resp.JoinToken, testSecret)
			if err != nil {
				t.Fatalf("validate join token: %v", err)
			}
			wantSID := "srv1"
			if tt.wantHits > 0 {
				wantSID = allocated.ServerID
			}
			if sid != wantSID {
				t.Errorf("join token sid = %q, want %q", sid, wantSID)
			}
		})
	}
}
