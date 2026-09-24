package social

import (
	"context"
	"encoding/json"
	"errors"
	"sync"
	"testing"

	"github.com/heroiclabs/nakama-common/runtime"
)

// ---------------------------------------------------------------------------
// party_create
// ---------------------------------------------------------------------------

func TestCreateParty(t *testing.T) {
	ctx := context.Background()
	store := newMemStore()

	p, err := CreateParty(ctx, store, "u1", fixedNow)
	if err != nil {
		t.Fatalf("CreateParty: %v", err)
	}
	if p.LeaderID != "u1" {
		t.Errorf("leader = %q, want u1", p.LeaderID)
	}
	if len(p.Members) != 1 || p.Members[0] != "u1" {
		t.Errorf("members = %v, want [u1]", p.Members)
	}
	if len(p.ID) != 32 {
		t.Errorf("party id = %q, want 32 hex chars", p.ID)
	}

	// The party record is server-owned, so it is readable by a runtime call
	// with an empty user id and invisible to Nakama's public storage API.
	if _, ok := store.get(PartyCollection, p.ID, ""); !ok {
		t.Error("party record must be stored system-owned (empty user id)")
	}
	// The reverse index exists and points at the new party.
	rec, ok := store.get(MembershipCollection, MembershipKey, "u1")
	if !ok {
		t.Fatal("membership index not written")
	}
	var m membership
	if err := json.Unmarshal([]byte(rec.value), &m); err != nil {
		t.Fatalf("membership: %v", err)
	}
	if m.PartyID != p.ID {
		t.Errorf("membership party = %q, want %q", m.PartyID, p.ID)
	}
}

func TestCreateParty_Errors(t *testing.T) {
	cases := []struct {
		name  string
		setup func(*memStore)
		user  string
		want  error
	}{
		{"empty user id", nil, "", ErrUnauthenticated},
		{
			name:  "already in a party",
			setup: func(s *memStore) { s.put(MembershipCollection, MembershipKey, "u1", `{"party_id":"other"}`) },
			user:  "u1",
			want:  ErrAlreadyInParty,
		},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			store := newMemStore()
			if c.setup != nil {
				c.setup(store)
			}
			if _, err := CreateParty(context.Background(), store, c.user, fixedNow); !errors.Is(err, c.want) {
				t.Fatalf("err = %v, want %v", err, c.want)
			}
		})
	}
}

func TestCreateParty_StorageFailureIsWrapped(t *testing.T) {
	store := newMemStore()
	store.failMultiUpdate = errStorage
	_, err := CreateParty(context.Background(), store, "u1", fixedNow)
	if !errors.Is(err, errStorage) {
		t.Fatalf("err = %v, want it to wrap %v", err, errStorage)
	}
	// Nothing partially written.
	if _, ok := store.get(MembershipCollection, MembershipKey, "u1"); ok {
		t.Error("failed create must leave no membership index")
	}
}

// ---------------------------------------------------------------------------
// party_join
// ---------------------------------------------------------------------------

func TestJoinParty(t *testing.T) {
	ctx := context.Background()
	store := newMemStore()
	seeded := store.seedParty(t, "party-a", "u1")

	p, err := JoinParty(ctx, store, "u2", seeded.ID, fixedNow)
	if err != nil {
		t.Fatalf("JoinParty: %v", err)
	}
	if len(p.Members) != 2 || p.Members[1] != "u2" {
		t.Errorf("members = %v, want [u1 u2]", p.Members)
	}
	if p.LeaderID != "u1" {
		t.Errorf("joining must not change the leader, got %q", p.LeaderID)
	}
	if got := store.mustParty(t, seeded.ID); len(got.Members) != 2 {
		t.Errorf("stored members = %v, want 2", got.Members)
	}
}

// The cap is MaxPartyMembers, and the (MaxPartyMembers+1)-th join fails with a
// typed error rather than being a silent no-op.
func TestJoinParty_CapIsEnforced(t *testing.T) {
	ctx := context.Background()
	store := newMemStore()
	store.seedParty(t, "party-a", "u1")

	for i := 2; i <= MaxPartyMembers; i++ {
		user := "u" + string(rune('0'+i))
		if _, err := JoinParty(ctx, store, user, "party-a", fixedNow); err != nil {
			t.Fatalf("join %d (%s): %v", i, user, err)
		}
	}
	full := store.mustParty(t, "party-a")
	if len(full.Members) != MaxPartyMembers {
		t.Fatalf("members = %v, want %d", full.Members, MaxPartyMembers)
	}

	// The 5th member (MaxPartyMembers is 4) must be rejected.
	_, err := JoinParty(ctx, store, "u-overflow", "party-a", fixedNow)
	if !errors.Is(err, ErrPartyFull) {
		t.Fatalf("overflow join err = %v, want ErrPartyFull", err)
	}
	after := store.mustParty(t, "party-a")
	if len(after.Members) != MaxPartyMembers {
		t.Fatalf("rejected join changed the party: %v", after.Members)
	}
	if _, ok := store.get(MembershipCollection, MembershipKey, "u-overflow"); ok {
		t.Error("rejected join must leave no membership index")
	}
}

func TestJoinParty_Errors(t *testing.T) {
	cases := []struct {
		name    string
		setup   func(*testing.T, *memStore)
		user    string
		partyID string
		want    error
	}{
		{"empty user id", nil, "", "party-a", ErrUnauthenticated},
		{"empty party id", nil, "u2", "", ErrPartyIDRequired},
		{"unknown party", nil, "u2", "nope", ErrPartyNotFound},
		{
			name: "already in a different party",
			setup: func(t *testing.T, s *memStore) {
				s.seedParty(t, "party-a", "u1")
				s.seedParty(t, "party-b", "u2")
			},
			user: "u2", partyID: "party-a", want: ErrAlreadyInParty,
		},
		{
			name: "membership index outlived its party",
			setup: func(t *testing.T, s *memStore) {
				s.put(MembershipCollection, MembershipKey, "u2", `{"party_id":"ghost"}`)
			},
			user: "u2", partyID: "ghost", want: ErrPartyNotFound,
		},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			store := newMemStore()
			if c.setup != nil {
				c.setup(t, store)
			}
			if _, err := JoinParty(context.Background(), store, c.user, c.partyID, fixedNow); !errors.Is(err, c.want) {
				t.Fatalf("err = %v, want %v", err, c.want)
			}
		})
	}
}

// Re-joining the party you are already in is idempotent success, not
// ErrAlreadyInParty: a client retrying after a timeout must not be told it is
// already in a party when that party is the one it asked for.
func TestJoinParty_SamePartyIsIdempotent(t *testing.T) {
	ctx := context.Background()
	store := newMemStore()
	store.seedParty(t, "party-a", "u1", "u2")

	p, err := JoinParty(ctx, store, "u2", "party-a", fixedNow)
	if err != nil {
		t.Fatalf("re-join: %v", err)
	}
	if len(p.Members) != 2 {
		t.Fatalf("members = %v, want unchanged [u1 u2]", p.Members)
	}
}

// ---------------------------------------------------------------------------
// Concurrency — the guard that makes the cap mean something
// ---------------------------------------------------------------------------

// Five players join a one-member party at the same moment, all of them having
// read the SAME party version (the barrier holds them there). Only
// MaxPartyMembers-1 may get in, every winner must appear in the final party,
// and the losers must get ErrPartyFull.
//
// This is the test that fails without the optimistic-concurrency guard. With
// `Version` dropped from the party write, all five writes are accepted, each
// one overwriting the others' member list — so the winners' count no longer
// matches the party's contents. With the member cap check dropped, the party
// exceeds MaxPartyMembers.
func TestJoinParty_ConcurrentJoins_RespectCap(t *testing.T) {
	const racers = 5
	ctx := context.Background()
	store := newMemStore()
	store.seedParty(t, "party-a", "leader")

	bar := newBarrier(racers)
	store.onPartyRead = bar.arrive

	var (
		mu       sync.Mutex
		admitted []string
		refused  []error
		wg       sync.WaitGroup
	)
	for i := 0; i < racers; i++ {
		user := "racer-" + string(rune('a'+i))
		wg.Add(1)
		go func() {
			defer wg.Done()
			_, err := JoinParty(ctx, store, user, "party-a", fixedNow)
			mu.Lock()
			defer mu.Unlock()
			if err != nil {
				refused = append(refused, err)
				return
			}
			admitted = append(admitted, user)
		}()
	}
	wg.Wait()

	wantAdmitted := MaxPartyMembers - 1 // the seeded leader holds one slot
	if len(admitted) != wantAdmitted {
		t.Errorf("admitted %d (%v), want %d", len(admitted), admitted, wantAdmitted)
	}
	if len(refused) != racers-wantAdmitted {
		t.Errorf("refused %d, want %d", len(refused), racers-wantAdmitted)
	}
	for _, err := range refused {
		if !errors.Is(err, ErrPartyFull) {
			t.Errorf("refusal = %v, want ErrPartyFull", err)
		}
	}

	final := store.mustParty(t, "party-a")
	if len(final.Members) != MaxPartyMembers {
		t.Errorf("final members = %v, want exactly %d", final.Members, MaxPartyMembers)
	}
	// A lost update shows up here: an admitted racer missing from the party.
	for _, user := range admitted {
		if !final.IsMember(user) {
			t.Errorf("admitted %s is not in the final party %v (lost update)", user, final.Members)
		}
	}
	seen := map[string]bool{}
	for _, m := range final.Members {
		if seen[m] {
			t.Errorf("duplicate member %s in %v", m, final.Members)
		}
		seen[m] = true
	}
	// Every refused racer must be left with no membership index, or it would
	// be unable to join anything ever again.
	for i := 0; i < racers; i++ {
		user := "racer-" + string(rune('a'+i))
		_, indexed := store.get(MembershipCollection, MembershipKey, user)
		if indexed != final.IsMember(user) {
			t.Errorf("%s: membership index=%v but in party=%v", user, indexed, final.IsMember(user))
		}
	}
}

// One user, two simultaneous creates: the create-only membership write means
// exactly one can win, so a double-tapped "create party" button cannot leave
// the user leading two parties.
func TestCreateParty_ConcurrentBySameUser_OnlyOneWins(t *testing.T) {
	ctx := context.Background()
	store := newMemStore()
	bar := newBarrier(2)

	var (
		mu    sync.Mutex
		ok    []*Party
		errsN int
		wg    sync.WaitGroup
	)
	for i := 0; i < 2; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			bar.arrive() // both past the membership read before either writes
			p, err := CreateParty(ctx, store, "u1", fixedNow)
			mu.Lock()
			defer mu.Unlock()
			if err != nil {
				errsN++
				return
			}
			ok = append(ok, p)
		}()
	}
	wg.Wait()

	if len(ok) != 1 || errsN != 1 {
		t.Fatalf("created %d parties with %d errors, want 1 and 1", len(ok), errsN)
	}
	rec, found := store.get(MembershipCollection, MembershipKey, "u1")
	if !found {
		t.Fatal("winner left no membership index")
	}
	var m membership
	_ = json.Unmarshal([]byte(rec.value), &m)
	if m.PartyID != ok[0].ID {
		t.Errorf("membership points at %q, want the winning party %q", m.PartyID, ok[0].ID)
	}
}

// One user, two simultaneous joins of two different parties: the create-only
// membership write admits exactly one, so "at most one party at a time" holds
// under a race and not merely under the read check.
func TestJoinParty_ConcurrentDifferentParties_OnlyOneWins(t *testing.T) {
	ctx := context.Background()
	store := newMemStore()
	store.seedParty(t, "party-a", "u1")
	store.seedParty(t, "party-b", "u2")

	bar := newBarrier(2)
	store.onPartyRead = bar.arrive

	var (
		mu     sync.Mutex
		joined []string
		errsN  int
		wg     sync.WaitGroup
	)
	for _, target := range []string{"party-a", "party-b"} {
		wg.Add(1)
		go func() {
			defer wg.Done()
			_, err := JoinParty(ctx, store, "u3", target, fixedNow)
			mu.Lock()
			defer mu.Unlock()
			if err != nil {
				errsN++
				return
			}
			joined = append(joined, target)
		}()
	}
	wg.Wait()

	if len(joined) != 1 || errsN != 1 {
		t.Fatalf("joined %v with %d errors, want exactly one of each", joined, errsN)
	}
	inA := store.mustParty(t, "party-a").IsMember("u3")
	inB := store.mustParty(t, "party-b").IsMember("u3")
	if inA == inB {
		t.Fatalf("u3 in party-a=%v, party-b=%v — must be in exactly one", inA, inB)
	}
}

// ---------------------------------------------------------------------------
// party_leave
// ---------------------------------------------------------------------------

func TestLeaveParty(t *testing.T) {
	cases := []struct {
		name        string
		members     []string
		leaver      string
		wantLeader  string
		wantMembers []string
		wantDeleted bool
	}{
		{
			name: "non-leader leaves", members: []string{"u1", "u2", "u3"}, leaver: "u2",
			wantLeader: "u1", wantMembers: []string{"u1", "u3"},
		},
		{
			name:    "leader leaves, leadership transfers to longest-standing member",
			members: []string{"u1", "u2", "u3"}, leaver: "u1",
			wantLeader: "u2", wantMembers: []string{"u2", "u3"},
		},
		{
			name: "leader of a pair leaves", members: []string{"u1", "u2"}, leaver: "u1",
			wantLeader: "u2", wantMembers: []string{"u2"},
		},
		{
			name:    "last member leaves, party is deleted",
			members: []string{"u1"}, leaver: "u1", wantDeleted: true,
		},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			ctx := context.Background()
			store := newMemStore()
			store.seedParty(t, "party-a", c.members...)

			p, err := LeaveParty(ctx, store, c.leaver, fixedNow)
			if err != nil {
				t.Fatalf("LeaveParty: %v", err)
			}

			// The leaver's index is always cleared, so they can join again.
			if _, ok := store.get(MembershipCollection, MembershipKey, c.leaver); ok {
				t.Error("leaver's membership index must be deleted")
			}

			if c.wantDeleted {
				if p != nil {
					t.Errorf("returned party = %+v, want nil", p)
				}
				if _, ok := store.get(PartyCollection, "party-a", ""); ok {
					t.Error("party record must be deleted when the last member leaves")
				}
				return
			}

			if p == nil {
				t.Fatal("returned party = nil, want the surviving party")
			}
			if p.LeaderID != c.wantLeader {
				t.Errorf("leader = %q, want %q", p.LeaderID, c.wantLeader)
			}
			if len(p.Members) != len(c.wantMembers) {
				t.Fatalf("members = %v, want %v", p.Members, c.wantMembers)
			}
			for i, want := range c.wantMembers {
				if p.Members[i] != want {
					t.Errorf("members[%d] = %q, want %q (order is load-bearing)", i, p.Members[i], want)
				}
			}
			stored := store.mustParty(t, "party-a")
			if stored.LeaderID != c.wantLeader || len(stored.Members) != len(c.wantMembers) {
				t.Errorf("stored party = %+v, want leader %q and %v", stored, c.wantLeader, c.wantMembers)
			}
			// The remaining members keep their indexes.
			for _, u := range c.wantMembers {
				if _, ok := store.get(MembershipCollection, MembershipKey, u); !ok {
					t.Errorf("remaining member %s lost its membership index", u)
				}
			}
		})
	}
}

func TestLeaveParty_Errors(t *testing.T) {
	cases := []struct {
		name  string
		setup func(*memStore)
		user  string
		want  error
	}{
		{"empty user id", nil, "", ErrUnauthenticated},
		{"not in a party", nil, "u1", ErrNotInParty},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			store := newMemStore()
			if c.setup != nil {
				c.setup(store)
			}
			if _, err := LeaveParty(context.Background(), store, c.user, fixedNow); !errors.Is(err, c.want) {
				t.Fatalf("err = %v, want %v", err, c.want)
			}
		})
	}
}

// A membership index pointing at a party that no longer exists must be
// clearable, or the user is permanently unable to join anything.
func TestLeaveParty_StaleIndexIsCleared(t *testing.T) {
	store := newMemStore()
	store.put(MembershipCollection, MembershipKey, "u1", `{"party_id":"ghost"}`)

	p, err := LeaveParty(context.Background(), store, "u1", fixedNow)
	if err != nil {
		t.Fatalf("LeaveParty: %v", err)
	}
	if p != nil {
		t.Errorf("returned party = %+v, want nil", p)
	}
	if _, ok := store.get(MembershipCollection, MembershipKey, "u1"); ok {
		t.Error("stale membership index must be deleted")
	}
}

// Leave-then-join must work: the create-only membership write only enforces
// one-party-at-a-time while the index exists.
func TestLeaveThenJoinAnotherParty(t *testing.T) {
	ctx := context.Background()
	store := newMemStore()
	store.seedParty(t, "party-a", "u1", "u2")
	store.seedParty(t, "party-b", "u3")

	if _, err := LeaveParty(ctx, store, "u2", fixedNow); err != nil {
		t.Fatalf("leave: %v", err)
	}
	p, err := JoinParty(ctx, store, "u2", "party-b", fixedNow)
	if err != nil {
		t.Fatalf("join after leave: %v", err)
	}
	if !p.IsMember("u2") {
		t.Errorf("u2 not in party-b: %v", p.Members)
	}
}

// Retries are bounded: a store that always rejects the write must surface
// ErrPartyBusy rather than spin, and must not have written anything.
func TestJoinParty_ContentionGivesUpWithErrPartyBusy(t *testing.T) {
	store := newMemStore()
	store.seedParty(t, "party-a", "u1")
	store.failMultiUpdate = errVersionConflict

	_, err := JoinParty(context.Background(), store, "u2", "party-a", fixedNow)
	if !errors.Is(err, ErrPartyBusy) {
		t.Fatalf("err = %v, want ErrPartyBusy", err)
	}
	if !errors.Is(err, errVersionConflict) {
		t.Errorf("err = %v, want it to also wrap the underlying conflict", err)
	}
	if store.multiCalls != maxWriteAttempts {
		t.Errorf("multiCalls = %d, want exactly maxWriteAttempts (%d)", store.multiCalls, maxWriteAttempts)
	}
	if len(store.mustParty(t, "party-a").Members) != 1 {
		t.Error("failed join must not change the party")
	}
}

// ---------------------------------------------------------------------------
// party_get
// ---------------------------------------------------------------------------

func TestGetParty(t *testing.T) {
	store := newMemStore()
	store.seedParty(t, "party-a", "u1", "u2")

	p, err := GetParty(context.Background(), store, "party-a")
	if err != nil {
		t.Fatalf("GetParty: %v", err)
	}
	if p.LeaderID != "u1" || !p.IsMember("u2") {
		t.Errorf("party = %+v", p)
	}
	if p.IsMember("u9") {
		t.Error("IsMember must be false for a non-member")
	}

	if _, err := GetParty(context.Background(), store, "nope"); !errors.Is(err, ErrPartyNotFound) {
		t.Errorf("unknown party err = %v, want ErrPartyNotFound", err)
	}
	if _, err := GetParty(context.Background(), store, ""); !errors.Is(err, ErrPartyIDRequired) {
		t.Errorf("empty id err = %v, want ErrPartyIDRequired", err)
	}
}

func TestGetParty_ReadFailureIsWrapped(t *testing.T) {
	store := newMemStore()
	store.failRead = errStorage
	if _, err := GetParty(context.Background(), store, "party-a"); !errors.Is(err, errStorage) {
		t.Fatalf("err = %v, want it to wrap %v", err, errStorage)
	}
}

// ---------------------------------------------------------------------------
// Caller identity
// ---------------------------------------------------------------------------

func TestCallerID(t *testing.T) {
	cases := []struct {
		name    string
		ctx     context.Context
		wantID  string
		wantErr error
	}{
		{"client session", clientCtx("u1"), "u1", nil},
		{"http_key server call has no subject", serverCtx(), "", ErrUnauthenticated},
		{"empty user id", context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_ID, ""), "", ErrUnauthenticated},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got, err := callerID(c.ctx)
			if !errors.Is(err, c.wantErr) {
				t.Fatalf("err = %v, want %v", err, c.wantErr)
			}
			if got != c.wantID {
				t.Errorf("id = %q, want %q", got, c.wantID)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Response shape
// ---------------------------------------------------------------------------

func TestRespond(t *testing.T) {
	out, err := respond(&Party{ID: "p1", LeaderID: "u1", Members: []string{"u1", "u2"}, CreatedAt: 7, UpdatedAt: 9})
	if err != nil {
		t.Fatalf("respond: %v", err)
	}
	var resp PartyResponse
	if err := json.Unmarshal([]byte(out), &resp); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if resp.PartyID != "p1" || resp.LeaderID != "u1" || resp.MemberCount != 2 || resp.MaxMembers != MaxPartyMembers {
		t.Errorf("resp = %+v", resp)
	}

	// A deleted party still answers with the cap, and with no party id.
	out, err = respond(nil)
	if err != nil {
		t.Fatalf("respond(nil): %v", err)
	}
	resp = PartyResponse{}
	_ = json.Unmarshal([]byte(out), &resp)
	if resp.PartyID != "" || resp.MemberCount != 0 || resp.MaxMembers != MaxPartyMembers {
		t.Errorf("resp = %+v, want an empty party with the cap set", resp)
	}
}

func TestDecodeRequest(t *testing.T) {
	cases := []struct {
		name    string
		payload string
		wantID  string
		wantErr error
	}{
		{"empty payload is valid", "", "", nil},
		{"party id", `{"party_id":"p1"}`, "p1", nil},
		{"unknown fields ignored", `{"party_id":"p1","junk":2}`, "p1", nil},
		{"not json", `{`, "", ErrInvalidPayload},
		{"wrong type", `{"party_id":5}`, "", ErrInvalidPayload},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			req, err := decodeRequest(c.payload)
			if !errors.Is(err, c.wantErr) {
				t.Fatalf("err = %v, want %v", err, c.wantErr)
			}
			if req.PartyID != c.wantID {
				t.Errorf("party_id = %q, want %q", req.PartyID, c.wantID)
			}
		})
	}
}
