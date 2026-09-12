package transfer

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// --- doubles ---------------------------------------------------------------

type fakeParty struct {
	members map[string][]string
	err     error
	calls   int32
}

func (f *fakeParty) IsMember(_ context.Context, partyID, userID string) (bool, error) {
	atomic.AddInt32(&f.calls, 1)
	if f.err != nil {
		return false, f.err
	}
	ms, ok := f.members[partyID]
	if !ok {
		return false, ErrPartyUnknown
	}
	for _, m := range ms {
		if m == userID {
			return true, nil
		}
	}
	return false, nil
}

type fakeRegistry struct {
	mu         sync.Mutex
	allocated  int32
	servers    map[string]storage.ServerInfo
	allocDelay time.Duration
	allocErr   error
	nextID     int
}

func newFakeRegistry() *fakeRegistry {
	return &fakeRegistry{servers: make(map[string]storage.ServerInfo)}
}

func (f *fakeRegistry) AllocateDungeon(_ context.Context, contentID string) (storage.ServerInfo, error) {
	atomic.AddInt32(&f.allocated, 1)
	if f.allocDelay > 0 {
		time.Sleep(f.allocDelay)
	}
	if f.allocErr != nil {
		return storage.ServerInfo{}, f.allocErr
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	f.nextID++
	id := fmt.Sprintf("dungeon-pod-%d", f.nextID)
	info := storage.ServerInfo{
		ServerID:  id,
		MapID:     contentID,
		Addr:      fmt.Sprintf("10.0.0.%d:7100", f.nextID),
		Transport: "tcp",
	}
	f.servers[id] = info
	return info, nil
}

func (f *fakeRegistry) GetServer(_ context.Context, serverID string) (storage.ServerInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	info, ok := f.servers[serverID]
	if !ok {
		return storage.ServerInfo{}, storage.ErrNotFound
	}
	return info, nil
}

// forget makes a published instance vanish the way a dead pod's
// servers:id: entry does when its TTL lapses.
func (f *fakeRegistry) forget(serverID string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	delete(f.servers, serverID)
}

func testDeps(t *testing.T, reg *fakeRegistry, party *fakeParty) DungeonDeps {
	t.Helper()
	keys, err := jwt.ParseKeyring("dungeon-test-secret-long-enough-for-hs256")
	if err != nil {
		t.Fatalf("parse keyring: %v", err)
	}
	return DungeonDeps{
		Registry: reg,
		Index:    storage.NewMemoryDungeonIndex(),
		Party:    party,
		JoinKeys: keys,
	}
}

// --- tests -----------------------------------------------------------------

func TestAssignDungeonAllocatesOnceForTheFirstMember(t *testing.T) {
	reg := newFakeRegistry()
	party := &fakeParty{members: map[string][]string{"p1": {"u1", "u2"}}}
	deps := testDeps(t, reg, party)

	res, err := AssignDungeon(context.Background(), "u1", "p1", "dungeon_01", deps)
	if err != nil {
		t.Fatalf("first member: %v", err)
	}
	if res.ServerAddr == "" || res.JoinToken == "" || res.JTI == "" {
		t.Fatalf("incomplete result: %+v", res)
	}
	if got := atomic.LoadInt32(&reg.allocated); got != 1 {
		t.Fatalf("allocations = %d, want 1", got)
	}
}

func TestEveryMemberOfOnePartyLandsOnTheSameInstance(t *testing.T) {
	// The whole point of ADR-26 decision 2. A per-content key would pass the
	// allocation count here and still be wrong; a per-USER key would allocate
	// four pods, which is what this pins.
	reg := newFakeRegistry()
	party := &fakeParty{members: map[string][]string{"p1": {"u1", "u2", "u3", "u4"}}}
	deps := testDeps(t, reg, party)

	var addr string
	for _, u := range []string{"u1", "u2", "u3", "u4"} {
		res, err := AssignDungeon(context.Background(), u, "p1", "dungeon_01", deps)
		if err != nil {
			t.Fatalf("member %s: %v", u, err)
		}
		if addr == "" {
			addr = res.ServerAddr
			continue
		}
		if res.ServerAddr != addr {
			t.Fatalf("member %s got %s, first member got %s", u, res.ServerAddr, addr)
		}
	}
	if got := atomic.LoadInt32(&reg.allocated); got != 1 {
		t.Fatalf("allocations = %d for one party, want 1", got)
	}
}

func TestTwoPartiesGetTwoInstances(t *testing.T) {
	// The other half of decision 2: keying by CONTENT would give both parties
	// the same pod, which is the opposite of instancing.
	reg := newFakeRegistry()
	party := &fakeParty{members: map[string][]string{"p1": {"u1"}, "p2": {"u2"}}}
	deps := testDeps(t, reg, party)

	a, err := AssignDungeon(context.Background(), "u1", "p1", "dungeon_01", deps)
	if err != nil {
		t.Fatalf("party 1: %v", err)
	}
	b, err := AssignDungeon(context.Background(), "u2", "p2", "dungeon_01", deps)
	if err != nil {
		t.Fatalf("party 2: %v", err)
	}
	if a.ServerAddr == b.ServerAddr {
		t.Fatalf("both parties landed on %s", a.ServerAddr)
	}
	if got := atomic.LoadInt32(&reg.allocated); got != 2 {
		t.Fatalf("allocations = %d for two parties, want 2", got)
	}
}

func TestSimultaneousMembersAllocateExactlyOnePod(t *testing.T) {
	// Four members entering at the same instant. Without the allocation
	// election every one of them allocates, and three pods are orphaned
	// immediately -- nothing will ever look them up again.
	reg := newFakeRegistry()
	reg.allocDelay = 50 * time.Millisecond // widen the window the race lives in
	party := &fakeParty{members: map[string][]string{"p1": {"u1", "u2", "u3", "u4"}}}
	deps := testDeps(t, reg, party)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	var wg sync.WaitGroup
	results := make([]AssignResult, 4)
	errs := make([]error, 4)
	start := make(chan struct{})

	for i, u := range []string{"u1", "u2", "u3", "u4"} {
		wg.Add(1)
		go func(i int, u string) {
			defer wg.Done()
			<-start
			results[i], errs[i] = AssignDungeon(ctx, u, "p1", "dungeon_01", deps)
		}(i, u)
	}
	close(start)
	wg.Wait()

	for i, err := range errs {
		if err != nil {
			t.Fatalf("member %d: %v", i, err)
		}
	}
	if got := atomic.LoadInt32(&reg.allocated); got != 1 {
		t.Fatalf("allocations = %d under a concurrent entry, want 1", got)
	}
	for i, r := range results {
		if r.ServerAddr != results[0].ServerAddr {
			t.Fatalf("member %d on %s, member 0 on %s", i, r.ServerAddr, results[0].ServerAddr)
		}
	}
}

func TestANonMemberIsRefusedAndNothingIsAllocated(t *testing.T) {
	// ADR-26 decision 3. An allocation is the most expensive thing a request
	// can trigger here, so the membership check runs BEFORE it, not after.
	reg := newFakeRegistry()
	party := &fakeParty{members: map[string][]string{"p1": {"u1"}}}
	deps := testDeps(t, reg, party)

	_, err := AssignDungeon(context.Background(), "intruder", "p1", "dungeon_01", deps)
	if !errors.Is(err, ErrNotAPartyMember) {
		t.Fatalf("err = %v, want ErrNotAPartyMember", err)
	}
	if got := atomic.LoadInt32(&reg.allocated); got != 0 {
		t.Fatalf("allocations = %d for a refused request, want 0", got)
	}
}

func TestAnUnknownPartyIsDistinctFromAnOutage(t *testing.T) {
	// "The party is gone" is the client's fault; "Nakama did not answer" is an
	// outage. Collapsing them reports an outage to players as a permissions
	// problem and makes them retry a refusal forever.
	reg := newFakeRegistry()

	_, err := AssignDungeon(context.Background(), "u1", "ghost", "dungeon_01",
		testDeps(t, reg, &fakeParty{members: map[string][]string{}}))
	if !errors.Is(err, ErrPartyUnknown) {
		t.Fatalf("unknown party err = %v, want ErrPartyUnknown", err)
	}

	outage := errors.New("dial tcp: connection refused")
	_, err = AssignDungeon(context.Background(), "u1", "p1", "dungeon_01",
		testDeps(t, reg, &fakeParty{err: outage}))
	if errors.Is(err, ErrPartyUnknown) || errors.Is(err, ErrNotAPartyMember) {
		t.Fatalf("outage was reported as a client fault: %v", err)
	}
	if !errors.Is(err, outage) {
		t.Fatalf("outage err = %v, want it to wrap the transport error", err)
	}
}

func TestAStaleMappingIsReplacedRatherThanHandedOut(t *testing.T) {
	// The index entry outlives a pod that died. Handing the next member that
	// address gives them a connection refused with no explanation; allocating
	// a fresh instance is the only useful answer.
	reg := newFakeRegistry()
	party := &fakeParty{members: map[string][]string{"p1": {"u1", "u2"}}}
	deps := testDeps(t, reg, party)

	first, err := AssignDungeon(context.Background(), "u1", "p1", "dungeon_01", deps)
	if err != nil {
		t.Fatalf("first member: %v", err)
	}
	reg.forget(first.ServerID)

	second, err := AssignDungeon(context.Background(), "u2", "p1", "dungeon_01", deps)
	if err != nil {
		t.Fatalf("second member after the pod died: %v", err)
	}
	if second.ServerID == first.ServerID {
		t.Fatalf("second member was handed the dead instance %s", first.ServerID)
	}
	if got := atomic.LoadInt32(&reg.allocated); got != 2 {
		t.Fatalf("allocations = %d, want 2 (one dead, one fresh)", got)
	}
}

func TestAFailedAllocationReleasesTheClaim(t *testing.T) {
	// Otherwise the first member's failure wedges the whole party until the
	// claim TTL expires: every later member would lose an election to a holder
	// that is never going to publish an answer.
	reg := newFakeRegistry()
	reg.allocErr = errors.New("fleet exhausted")
	party := &fakeParty{members: map[string][]string{"p1": {"u1", "u2"}}}
	deps := testDeps(t, reg, party)

	if _, err := AssignDungeon(context.Background(), "u1", "p1", "dungeon_01", deps); err == nil {
		t.Fatal("expected the allocation failure to surface")
	}

	reg.allocErr = nil
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if _, err := AssignDungeon(ctx, "u2", "p1", "dungeon_01", deps); err != nil {
		t.Fatalf("second member after a failed allocation: %v", err)
	}
}

func TestAnEmptyPartyIDIsRejectedBeforeAnythingElse(t *testing.T) {
	reg := newFakeRegistry()
	party := &fakeParty{members: map[string][]string{"p1": {"u1"}}}
	deps := testDeps(t, reg, party)

	if _, err := AssignDungeon(context.Background(), "u1", "", "dungeon_01", deps); err == nil {
		t.Fatal("expected an empty party id to be rejected")
	}
	if got := atomic.LoadInt32(&party.calls); got != 0 {
		t.Fatalf("party checked %d times for an empty id, want 0", got)
	}
}
