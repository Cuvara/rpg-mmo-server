package server

import (
	"context"
	"errors"
	"fmt"
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

// TestEnterWorldWorstCaseBudgetFitsHandlerWindow pins the SUM, not one leg.
//
// One handleEnterWorld can stack three sequential waits: the registry lookup's
// retry window (registry.RetryTotalTimeout), the Agones allocation HTTP call
// (registry.DefaultTimeout), and the wait for the allocated pod to
// self-register (registry.DefaultAllocationWaitTimeout). Each leg is bounded on
// its own, but their sum used to exceed MaxHandlerBlockingWait, so the
// gateway's heartbeat killed the connection mid-allocation (issue #235). The
// fix caps the handler at EnterWorldBudget; this test derives the worst case
// from the same constants the code runs on, so it moves when they move.
func TestEnterWorldWorstCaseBudgetFitsHandlerWindow(t *testing.T) {
	stacked := registry.RetryTotalTimeout + registry.DefaultTimeout + registry.DefaultAllocationWaitTimeout

	// The handler runs under one deadline, so what it can actually block for
	// is min(stacked legs, EnterWorldBudget).
	worst := stacked
	if EnterWorldBudget < worst {
		worst = EnterWorldBudget
	}
	if worst >= MaxHandlerBlockingWait {
		t.Fatalf("worst-case enter_world block = %s (stacked legs %s, budget %s), must be < MaxHandlerBlockingWait = %s",
			worst, stacked, EnterWorldBudget, MaxHandlerBlockingWait)
	}
	if margin := MaxHandlerBlockingWait - worst; margin < time.Second {
		t.Fatalf("worst-case enter_world block = %s leaves only %s of MaxHandlerBlockingWait = %s; need >= 1s for the session write-back and response flush",
			worst, margin, MaxHandlerBlockingWait)
	}

	// The budget's own arithmetic: carved out of the handler window, never the
	// other way round, with a real slice reserved for the write-back.
	if EnterWorldBudget != MaxHandlerBlockingWait-enterWorldWriteMargin {
		t.Errorf("EnterWorldBudget = %s, want MaxHandlerBlockingWait - enterWorldWriteMargin = %s",
			EnterWorldBudget, MaxHandlerBlockingWait-enterWorldWriteMargin)
	}
	if enterWorldWriteMargin < time.Second {
		t.Errorf("enterWorldWriteMargin = %s, want >= 1s", enterWorldWriteMargin)
	}

	// Document why the budget is load-bearing rather than slack: if a constants
	// change ever brings the stacked legs inside the window, the deadline
	// becomes belt-and-braces and this note should be revisited, not deleted.
	if stacked <= MaxHandlerBlockingWait {
		t.Logf("stacked legs = %s now fit inside MaxHandlerBlockingWait = %s; EnterWorldBudget is no longer the binding constraint", stacked, MaxHandlerBlockingWait)
	}
}

// A cold-map allocation slower than the handler budget must yield the retryable
// "server is starting" error instead of blocking until the heartbeat kills the
// connection — and the single-flight leader must keep running detached, so the
// same client's retry finds the server ready without a second allocation
// (issue #235).
func TestGateway_SlowAllocationYieldsRetryableAndLeaderCompletes(t *testing.T) {
	allocated := storage.ServerInfo{
		ServerID: "map-servers-dev-slow-1",
		Addr:     "192.168.65.3:9000",
		Capacity: 100,
	}
	serverRegistry := storage.NewMemoryServerRegistry()
	alloc := &countingAllocator{
		info:          allocated,
		selfRegister:  serverRegistry,
		registerAfter: 500 * time.Millisecond, // far past the handler budget below
		selfAddr:      "192.168.65.3:7311",    // the port the pod really got
	}

	gw := New(
		session.NewSessionManager(storage.NewMemorySessionStore()),
		registry.NewRegistryServiceWithAllocator(serverRegistry, alloc,
			// A registration wait longer than the handler budget: the budget,
			// not this wait, must be what unblocks the handler.
			registry.WithAllocationWait(2*time.Second, 5*time.Millisecond)),
		testSecret,
		logger.New("error"),
	)
	// Scaled-down budget so the test proves the deadline, not the wall clock;
	// set before Run so the handler goroutines only ever read it.
	gw.enterWorldBudget = 100 * time.Millisecond
	go func() { _ = gw.Run("127.0.0.1:0") }()
	for i := 0; i < 50 && gw.Addr() == ""; i++ {
		time.Sleep(10 * time.Millisecond)
	}
	if gw.Addr() == "" {
		t.Fatal("gateway did not start in time")
	}
	t.Cleanup(gw.Shutdown)

	conn := dialGateway(t, gw)
	defer conn.Close()

	token, _ := jwt.Sign("user1", testSecret, time.Hour)
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn, authEnv)
	readEnvelope(t, conn)

	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_desert"})
	start := time.Now()
	sendEnvelope(t, conn, enterEnv)

	var resp messages.EnterWorldResponse
	if err := readEnvelope(t, conn).UnmarshalPayload(&resp); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	blocked := time.Since(start)
	if resp.Error != msgServerStarting {
		t.Fatalf("Error = %q, want the retryable %q", resp.Error, msgServerStarting)
	}
	if resp.JoinToken != "" || resp.ServerAddr != "" {
		t.Fatalf("timed-out assignment handed out token=%q addr=%q", resp.JoinToken, resp.ServerAddr)
	}
	// The answer must come from the budget, not from the 2s allocation wait.
	if blocked >= time.Second {
		t.Errorf("handler blocked %s, want ~the 100ms budget (deadline not applied)", blocked)
	}

	// The connection must have survived the timed-out assignment: a ping still
	// round-trips on the same socket.
	pingEnv, _ := messages.NewEnvelope(messages.MsgPing, messages.PingMessage{Timestamp: time.Now().UnixMilli()})
	sendEnvelope(t, conn, pingEnv)
	if env := readEnvelope(t, conn); env.Type != messages.MsgPong {
		t.Fatalf("after timed-out enter_world, ping answered with %v, want MsgPong", env.Type)
	}

	// The detached leader keeps waiting: the pod registers at 500ms, and the
	// SAME connection's retry then resolves it straight from the registry —
	// exactly one allocation across both attempts.
	time.Sleep(700 * time.Millisecond)
	sendEnvelope(t, conn, enterEnv)
	var retry messages.EnterWorldResponse
	if err := readEnvelope(t, conn).UnmarshalPayload(&retry); err != nil {
		t.Fatalf("retry unmarshal: %v", err)
	}
	if retry.Error != "" {
		t.Fatalf("retry after the pod registered failed: %q", retry.Error)
	}
	if retry.ServerAddr != alloc.selfAddr {
		t.Errorf("retry ServerAddr = %q, want the pod's self-reported %q", retry.ServerAddr, alloc.selfAddr)
	}
	if retry.JoinToken == "" {
		t.Error("retry should mint a join token")
	}
	if got := alloc.hits.Load(); got != 1 {
		t.Errorf("allocator hits = %d, want exactly 1 (retry must reuse the detached leader's allocation)", got)
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
			// An allocator failure that is NOT a capacity answer may have
			// allocated a pod whose response was lost, so it keeps the
			// terminal message: telling a client to retry that is how
			// un-reclaimable pods accumulate.
			name:      "allocation failure surfaces to the client",
			mapID:     "map_desert",
			alloc:     &countingAllocator{err: errors.New("allocate: allocation api status 500: internal")},
			wantHits:  1,
			wantError: msgNoServerAvailable,
		},
		{
			// The fleet answered `UnAllocated`: no GameServer was handed
			// out and none is Ready right now, but the Fleet controller is
			// already bringing a replacement up (5.38s on k3d, ADR-18).
			// The client must get the retryable message, not the terminal
			// one — issue #152. Retrying costs one allocation POST and no
			// pod, and the retry that succeeds usually costs neither,
			// because a Ready pod self-registers before any allocation.
			name:  "momentarily exhausted fleet is retryable",
			mapID: "map_desert",
			alloc: &countingAllocator{err: fmt.Errorf("allocate: fleet map-servers-dotnet-k8s: %w (state %q)",
				registry.ErrNoCapacity, "UnAllocated")},
			wantHits:  1,
			wantError: msgFleetBusy,
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
