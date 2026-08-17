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
//
// A real allocation only tells the gateway which pod was picked; the pod itself
// registers its address later, once it has booted and bound. selfRegister
// reproduces that: when set, the pod publishes its own entry after
// registerAfter, and it publishes a *different* address and transport from the
// allocation response so a test can prove which one the client was handed.
type countingAllocator struct {
	info storage.ServerInfo
	err  error
	hits atomic.Int32

	selfRegister  storage.ServerRegistry
	registerAfter time.Duration
	selfAddr      string
	selfTransport string
	// selfMapID overrides the map the pod publishes. A fleet is allocated by
	// name, and its pods serve whatever their fleet spec's GAMESERVER_MAP_ID
	// says — which is not necessarily the map the client asked for.
	selfMapID string
}

func (c *countingAllocator) AllocateServer(_ context.Context, mapID string) (storage.ServerInfo, error) {
	c.hits.Add(1)
	if c.err != nil {
		return storage.ServerInfo{}, c.err
	}
	info := c.info
	info.MapID = mapID
	if c.selfRegister != nil {
		self := info
		if c.selfAddr != "" {
			self.Addr = c.selfAddr
		}
		self.Transport = c.selfTransport
		if c.selfMapID != "" {
			self.MapID = c.selfMapID
		}
		time.AfterFunc(c.registerAfter, func() {
			_ = c.selfRegister.Register(context.Background(), self)
		})
	}
	return info, nil
}

// startGatewayWithAllocator starts a gateway whose registry knows about
// map_forest (has room) and map_full (at capacity) only, so any other map has to
// go through the allocator.
//
// The returned store is the same one the allocator may self-register into.
func startGatewayWithAllocator(t *testing.T, alloc *countingAllocator) (*Gateway, storage.ServerRegistry) {
	t.Helper()

	serverRegistry := storage.NewMemoryServerRegistry()
	seed := []storage.ServerInfo{
		{ServerID: "srv1", MapID: "map_forest", Addr: "10.0.0.1:9000", Capacity: 100},
		{ServerID: "srv-full", MapID: "map_full", Addr: "10.0.0.2:9000", Capacity: 10, PlayerCount: 10},
	}
	for _, info := range seed {
		if err := serverRegistry.Register(context.Background(), info); err != nil {
			t.Fatalf("register %s: %v", info.ServerID, err)
		}
	}
	if alloc != nil && alloc.selfRegister == nil && alloc.registerAfter > 0 {
		alloc.selfRegister = serverRegistry
	}

	gw := New(
		session.NewSessionManager(storage.NewMemorySessionStore()),
		registry.NewRegistryServiceWithAllocator(serverRegistry, alloc,
			// Tests must not wait the production 20s for a pod that will never
			// show up; the behaviour under test is the same.
			registry.WithAllocationWait(300*time.Millisecond, 5*time.Millisecond)),
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
	return gw, serverRegistry
}

// handleEnterWorld blocks the connection's read loop for the allocation wait,
// and that same loop is what records MsgPong. The shipped default must sit
// strictly below the point where that starves the heartbeat, or the gateway
// disconnects the very client it is waiting for. cmd/gateway enforces the same
// bound at start-up for configured values; this guards the default itself, which
// no start-up check would catch until someone ran it.
func TestAllocationWaitDefaultCannotStarveHeartbeat(t *testing.T) {
	if registry.DefaultAllocationWaitTimeout >= MaxHandlerBlockingWait {
		t.Fatalf("DefaultAllocationWaitTimeout = %s, must be < MaxHandlerBlockingWait = %s (pongTimeout %s - pingInterval %s)",
			registry.DefaultAllocationWaitTimeout, MaxHandlerBlockingWait, pongTimeout, pingInterval)
	}
	if MaxHandlerBlockingWait != pongTimeout-pingInterval {
		t.Errorf("MaxHandlerBlockingWait = %s, want pongTimeout-pingInterval = %s",
			MaxHandlerBlockingWait, pongTimeout-pingInterval)
	}
}

// A client that keeps asking for a map no fleet serves must not keep draining
// the fleet. The gateway's single-flight only merges CONCURRENT callers; a retry
// loop is sequential, and every sequential attempt used to allocate another
// GameServer that Agones can never un-allocate.
func TestGateway_UnservableMapDoesNotLeakAllocationsAcrossRetries(t *testing.T) {
	const retries = 4
	alloc := &countingAllocator{
		info: storage.ServerInfo{
			ServerID: "map-servers-dotnet-dev-q7bdn-hctpd",
			Addr:     "127.0.0.1:7002",
			Capacity: 100,
		},
		registerAfter: 10 * time.Millisecond,
		selfAddr:      "127.0.0.1:7002",
		selfMapID:     "map_01", // the only map the configured fleet serves
	}
	gw, _ := startGatewayWithAllocator(t, alloc)

	for i := 0; i < retries; i++ {
		conn := dialGateway(t, gw)
		token, _ := jwt.Sign("user1", testSecret, time.Hour)
		authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
		sendEnvelope(t, conn, authEnv)
		readEnvelope(t, conn)

		enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_77"})
		sendEnvelope(t, conn, enterEnv)

		var resp messages.EnterWorldResponse
		if err := readEnvelope(t, conn).UnmarshalPayload(&resp); err != nil {
			t.Fatalf("attempt %d: unmarshal: %v", i, err)
		}
		if resp.Error != msgUnknownMap {
			t.Fatalf("attempt %d: Error = %q, want %q", i, resp.Error, msgUnknownMap)
		}
		if resp.Error == msgServerStarting {
			t.Fatalf("attempt %d: an unservable map must not be reported as retryable", i)
		}
		if resp.JoinToken != "" || resp.ServerAddr != "" {
			t.Fatalf("attempt %d: handed out token=%q addr=%q for a map the fleet cannot serve",
				i, resp.JoinToken, resp.ServerAddr)
		}
		conn.Close()
	}

	// The bound the fix guarantees: one allocation per map per mismatch TTL
	// (registry.DefaultMapMismatchTTL, far longer than this test).
	if got := alloc.hits.Load(); got != 1 {
		t.Errorf("allocator hits = %d over %d requests, want exactly 1", got, retries)
	}
}

func TestGateway_EnterWorldAllocatesUnservedMap(t *testing.T) {
	allocated := storage.ServerInfo{
		ServerID: "map-servers-dev-xjh7p-6ndtl", // == GameServer/pod name
		Addr:     "192.168.65.3:9000",           // the allocation response's guess
		Capacity: 100,
	}

	tests := []struct {
		name string
		// mapID the client asks for.
		mapID string
		alloc *countingAllocator
		// wantAddr / wantTransport are what the client must be told.
		wantAddr      string
		wantTransport string
		wantSID       string
		wantHits      int32
		wantError     string // exact client-facing message, "" = success
	}{
		{
			// Allocation exists to serve a map with NO live server.
			name:  "unserved map allocates and waits for the pod to register",
			mapID: "map_desert",
			alloc: &countingAllocator{
				info:          allocated,
				registerAfter: 20 * time.Millisecond,
				selfAddr:      "192.168.65.3:7257", // the port the pod really got
				selfTransport: "kcp",
			},
			// The self-reported entry wins over the allocation response.
			wantAddr:      "192.168.65.3:7257",
			wantTransport: "kcp",
			wantSID:       allocated.ServerID,
			wantHits:      1,
		},
		{
			// The token is minted last: if the pod never registers, no token and
			// no address are handed out, and the failure is the retryable one.
			name:      "allocated pod that never registers is a retryable error",
			mapID:     "map_desert",
			alloc:     &countingAllocator{info: allocated},
			wantHits:  1,
			wantError: msgServerStarting,
		},
		{
			name:          "served map does not allocate",
			mapID:         "map_forest",
			alloc:         &countingAllocator{info: allocated},
			wantAddr:      "10.0.0.1:9000",
			wantTransport: "",
			wantSID:       "srv1",
			wantHits:      0,
		},
		{
			// ADR-2: a full map must NOT gain a second live server. Refusing the
			// join is the correct, loud failure; the client must not retry.
			name:      "full map is refused without allocating",
			mapID:     "map_full",
			alloc:     &countingAllocator{info: allocated},
			wantHits:  0,
			wantError: msgNoServerAvailable,
		},
		{
			// The fleet answered, the pod is healthy — but it serves its own
			// fleet map, not the requested one. The client must be refused, not
			// silently dropped into another world, and the message must not be
			// the retryable one: a retry cannot change a fleet's map and each
			// retry allocates a GameServer Agones will never reclaim.
			name:  "allocated pod serving another map is refused, not announced",
			mapID: "map_77",
			alloc: &countingAllocator{
				info:          allocated,
				registerAfter: 20 * time.Millisecond,
				selfAddr:      "192.168.65.3:7257",
				selfMapID:     "map_01", // the fleet's GAMESERVER_MAP_ID
			},
			wantHits:  1,
			wantError: msgUnknownMap,
		},
		{
			name:      "allocation failure surfaces to the client",
			mapID:     "map_desert",
			alloc:     &countingAllocator{err: errors.New("fleet exhausted")},
			wantHits:  1,
			wantError: msgNoServerAvailable,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			gw, store := startGatewayWithAllocator(t, tt.alloc)

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
			if tt.wantError != "" {
				if resp.Error != tt.wantError {
					t.Fatalf("Error = %q, want %q", resp.Error, tt.wantError)
				}
				if resp.JoinToken != "" {
					t.Error("a failed assignment must not mint a join token")
				}
				if resp.ServerAddr != "" {
					t.Errorf("ServerAddr = %q, want empty on failure", resp.ServerAddr)
				}
				// The gateway never writes a game server's registry entry. Only
				// meaningful when the pod itself never registered — otherwise the
				// entry under test is the pod's own.
				if tt.alloc.registerAfter == 0 {
					if _, gerr := store.GetServer(context.Background(), allocated.ServerID); gerr == nil && tt.wantHits > 0 {
						t.Error("gateway registered the allocated server itself; only the game server may")
					}
				}
				return
			}
			if resp.Error != "" {
				t.Fatalf("unexpected error: %s", resp.Error)
			}
			if resp.ServerAddr != tt.wantAddr {
				t.Errorf("ServerAddr = %q, want %q", resp.ServerAddr, tt.wantAddr)
			}
			if resp.Transport != tt.wantTransport {
				t.Errorf("Transport = %q, want %q", resp.Transport, tt.wantTransport)
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
			if sid != tt.wantSID {
				t.Errorf("join token sid = %q, want %q", sid, tt.wantSID)
			}
		})
	}
}
