package social

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"
	"time"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// noopLogger implements runtime.Logger and discards everything.
type noopLogger struct{}

func (noopLogger) Debug(string, ...interface{})                       {}
func (noopLogger) Info(string, ...interface{})                        {}
func (noopLogger) Warn(string, ...interface{})                        {}
func (noopLogger) Error(string, ...interface{})                       {}
func (l noopLogger) WithField(string, interface{}) runtime.Logger     { return l }
func (l noopLogger) WithFields(map[string]interface{}) runtime.Logger { return l }
func (noopLogger) Fields() map[string]interface{}                     { return nil }

// errVersionConflict is the shape Nakama returns when a storage write's
// version check fails; the exact text is Nakama's.
var errVersionConflict = errors.New("Storage write rejected - version check failed.")

// errStorage is a canned non-conflict storage failure.
var errStorage = errors.New("storage unavailable")

// fixedNow is a deterministic clock for tests.
func fixedNow() time.Time { return time.Unix(1_700_000_000, 0).UTC() }

// record is one stored object plus its optimistic-concurrency version.
type record struct {
	value   string
	version string
}

// memStore is an in-memory storage engine that reproduces the ONE Nakama
// behaviour this package depends on for correctness: optimistic concurrency on
// StorageWrite/StorageDelete versions, checked for every object in a
// MultiUpdate before any of them is applied, with the whole update rejected if
// any check fails.
//
// Version semantics, matching Nakama:
//
//	""      unconditional upsert / delete
//	"*"     create-only: the object must NOT exist
//	"<v>"   the object must exist at exactly version <v>
//
// It is safe for concurrent use, because the concurrency test drives it from
// real goroutines rather than simulating interleavings.
type memStore struct {
	mu      sync.Mutex
	objects map[string]*record
	seq     int

	multiCalls int
	readCalls  int

	// failMultiUpdate, when set, makes MultiUpdate fail with this error
	// before any version check — a storage outage, not a conflict.
	failMultiUpdate error
	// failRead, when set, makes StorageRead fail.
	failRead error

	// onPartyRead, when set, is invoked after every read of PartyCollection.
	// The concurrency test uses it to hold all racers at the same version.
	onPartyRead func()
}

func newMemStore() *memStore {
	return &memStore{objects: make(map[string]*record)}
}

func objKey(collection, key, userID string) string {
	return collection + "/" + key + "/" + userID
}

// put writes unconditionally, for seeding. Caller must not hold mu.
func (m *memStore) put(collection, key, userID, value string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.putLocked(collection, key, userID, value)
}

func (m *memStore) putLocked(collection, key, userID, value string) {
	m.seq++
	m.objects[objKey(collection, key, userID)] = &record{
		value:   value,
		version: fmt.Sprintf("v%d", m.seq),
	}
}

func (m *memStore) get(collection, key, userID string) (*record, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	r, ok := m.objects[objKey(collection, key, userID)]
	if !ok {
		return nil, false
	}
	copied := *r
	return &copied, true
}

func (m *memStore) StorageRead(_ context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error) {
	m.mu.Lock()
	if m.failRead != nil {
		err := m.failRead
		m.mu.Unlock()
		return nil, err
	}
	m.readCalls++
	out := make([]*api.StorageObject, 0, len(reads))
	sawParty := false
	for _, r := range reads {
		if r.Collection == PartyCollection {
			sawParty = true
		}
		if rec, ok := m.objects[objKey(r.Collection, r.Key, r.UserID)]; ok {
			out = append(out, &api.StorageObject{
				Collection: r.Collection,
				Key:        r.Key,
				UserId:     r.UserID,
				Value:      rec.value,
				Version:    rec.version,
			})
		}
	}
	hook := m.onPartyRead
	m.mu.Unlock()

	// The hook runs OUTSIDE the lock: it blocks, and holding the store's lock
	// while blocking would serialise the racers we are trying to overlap.
	if sawParty && hook != nil {
		hook()
	}
	return out, nil
}

// checkVersionLocked reports whether a write/delete at the given version is
// allowed against current state.
func (m *memStore) checkVersionLocked(collection, key, userID, version string) error {
	rec, exists := m.objects[objKey(collection, key, userID)]
	switch {
	case version == "":
		return nil // unconditional
	case version == "*":
		if exists {
			return fmt.Errorf("%s/%s already exists: %w", collection, key, errVersionConflict)
		}
		return nil
	case !exists:
		return fmt.Errorf("%s/%s does not exist: %w", collection, key, errVersionConflict)
	case rec.version != version:
		return fmt.Errorf("%s/%s is at %s not %s: %w", collection, key, rec.version, version, errVersionConflict)
	}
	return nil
}

// MultiUpdate applies every storage write and delete atomically: all version
// checks first, then all mutations, and nothing at all on failure.
func (m *memStore) MultiUpdate(_ context.Context, _ []*runtime.AccountUpdate, writes []*runtime.StorageWrite,
	deletes []*runtime.StorageDelete, _ []*runtime.WalletUpdate, _ bool,
) ([]*api.StorageObjectAck, []*runtime.WalletUpdateResult, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.multiCalls++
	if m.failMultiUpdate != nil {
		return nil, nil, m.failMultiUpdate
	}
	for _, w := range writes {
		if err := m.checkVersionLocked(w.Collection, w.Key, w.UserID, w.Version); err != nil {
			return nil, nil, err
		}
	}
	for _, d := range deletes {
		if err := m.checkVersionLocked(d.Collection, d.Key, d.UserID, d.Version); err != nil {
			return nil, nil, err
		}
	}
	acks := make([]*api.StorageObjectAck, 0, len(writes))
	for _, w := range writes {
		m.putLocked(w.Collection, w.Key, w.UserID, w.Value)
		acks = append(acks, &api.StorageObjectAck{
			Collection: w.Collection, Key: w.Key, UserId: w.UserID,
			Version: m.objects[objKey(w.Collection, w.Key, w.UserID)].version,
		})
	}
	for _, d := range deletes {
		delete(m.objects, objKey(d.Collection, d.Key, d.UserID))
	}
	return acks, nil, nil
}

// mustParty reads a party out of the store or fails the test.
func (m *memStore) mustParty(t *testing.T, partyID string) *Party {
	t.Helper()
	p, _, err := readParty(context.Background(), m, partyID)
	if err != nil {
		t.Fatalf("read party %s: %v", partyID, err)
	}
	if p == nil {
		t.Fatalf("party %s not found", partyID)
	}
	return p
}

// seedParty writes a party and the membership index of each member, the way
// CreateParty+JoinParty would have left them.
func (m *memStore) seedParty(t *testing.T, partyID string, members ...string) *Party {
	t.Helper()
	if len(members) == 0 {
		t.Fatal("seedParty needs at least one member")
	}
	p := &Party{
		ID:        partyID,
		LeaderID:  members[0],
		Members:   members,
		CreatedAt: fixedNow().Unix(),
		UpdatedAt: fixedNow().Unix(),
	}
	w := partyWrite(p, "")
	m.put(w.Collection, w.Key, w.UserID, w.Value)
	for _, u := range members {
		mw := membershipWrite(u, partyID, fixedNow())
		m.put(mw.Collection, mw.Key, mw.UserID, mw.Value)
	}
	return p
}

// clientCtx mimics an authenticated client-session invocation.
func clientCtx(userID string) context.Context {
	ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_ID, userID)
	return context.WithValue(ctx, runtime.RUNTIME_CTX_SESSION_ID, "s-"+userID)
}

// serverCtx mimics a runtime.http_key (gateway) invocation: no session markers.
func serverCtx() context.Context { return context.Background() }

// barrier releases all arrivals once `want` of them are waiting. Arrivals
// beyond that pass straight through, so a retry loop cannot deadlock on it.
type barrier struct {
	mu   sync.Mutex
	n    int
	want int
	ch   chan struct{}
	open bool
}

func newBarrier(want int) *barrier {
	return &barrier{want: want, ch: make(chan struct{})}
}

func (b *barrier) arrive() {
	b.mu.Lock()
	if b.open {
		b.mu.Unlock()
		return
	}
	b.n++
	if b.n >= b.want {
		b.open = true
		close(b.ch)
		b.mu.Unlock()
		return
	}
	b.mu.Unlock()
	<-b.ch
}
