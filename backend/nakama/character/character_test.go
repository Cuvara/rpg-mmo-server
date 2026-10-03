package character

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"regexp"
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

var errVersionConflict = errors.New("Storage write rejected - version check failed.")

func fixedNow() time.Time { return time.Unix(1_700_000_000, 0).UTC() }

type record struct {
	value   string
	version string
}

// memStore reproduces Nakama's optimistic-concurrency semantics on
// StorageWrite: "" unconditional, "*" create-only, "<v>" must match.
type memStore struct {
	mu       sync.Mutex
	objects  map[string]*record
	seq      int
	failRead error
	// conflictNext, when > 0, makes that many next writes fail a version check
	// as if a concurrent writer had won, to exercise the retry loop.
	conflictNext int
	writes       int
}

func newMemStore() *memStore { return &memStore{objects: make(map[string]*record)} }

func objKey(c, k, u string) string { return c + "/" + k + "/" + u }

func (m *memStore) StorageRead(_ context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.failRead != nil {
		return nil, m.failRead
	}
	var out []*api.StorageObject
	for _, r := range reads {
		if rec, ok := m.objects[objKey(r.Collection, r.Key, r.UserID)]; ok {
			out = append(out, &api.StorageObject{
				Collection: r.Collection, Key: r.Key, UserId: r.UserID,
				Value: rec.value, Version: rec.version,
			})
		}
	}
	return out, nil
}

func (m *memStore) StorageWrite(_ context.Context, writes []*runtime.StorageWrite) ([]*api.StorageObjectAck, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.writes++
	if m.conflictNext > 0 {
		m.conflictNext--
		return nil, errVersionConflict
	}
	for _, w := range writes {
		rec, exists := m.objects[objKey(w.Collection, w.Key, w.UserID)]
		switch {
		case w.Version == "":
		case w.Version == "*" && exists:
			return nil, errVersionConflict
		case w.Version == "*":
		case !exists || rec.version != w.Version:
			return nil, errVersionConflict
		}
		if w.PermissionWrite != 0 || w.PermissionRead != 1 {
			return nil, fmt.Errorf("unexpected permissions r=%d w=%d", w.PermissionRead, w.PermissionWrite)
		}
	}
	acks := make([]*api.StorageObjectAck, 0, len(writes))
	for _, w := range writes {
		m.seq++
		v := fmt.Sprintf("v%d", m.seq)
		m.objects[objKey(w.Collection, w.Key, w.UserID)] = &record{value: w.Value, version: v}
		acks = append(acks, &api.StorageObjectAck{Collection: w.Collection, Key: w.Key, UserId: w.UserID, Version: v})
	}
	return acks, nil
}

// seedRoster stores a roster directly.
func (m *memStore) seedRoster(t *testing.T, userID string, cs ...Character) {
	t.Helper()
	b, err := json.Marshal(Roster{Characters: cs})
	if err != nil {
		t.Fatal(err)
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.seq++
	m.objects[objKey(Collection, RosterKey, userID)] = &record{value: string(b), version: fmt.Sprintf("v%d", m.seq)}
}

func userCtx(userID string) context.Context {
	return context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_ID, userID)
}

var uuidV4 = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)

func TestValidateName(t *testing.T) {
	tests := []struct {
		name  string
		input string
		ok    bool
	}{
		{"min length", "abc", true},
		{"max length", "abcdefghijklmnop", true},
		{"digits and underscore", "Hero_42", true},
		{"too short", "ab", false},
		{"too long", "abcdefghijklmnopq", false},
		{"empty", "", false},
		{"space", "two words", false},
		{"hyphen", "a-b-c", false},
		{"non ascii", "héros", false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := ValidateName(tt.input)
			if (err == nil) != tt.ok {
				t.Fatalf("ValidateName(%q) = %v, want ok=%v", tt.input, err, tt.ok)
			}
			if err != nil && !errors.Is(err, ErrInvalidName) {
				t.Fatalf("error = %v, want ErrInvalidName", err)
			}
		})
	}
}

func TestCreate(t *testing.T) {
	full := make([]Character, 0, MaxCharacterSlots)
	for i := 0; i < MaxCharacterSlots; i++ {
		full = append(full, Character{ID: fmt.Sprintf("c%d", i), Slot: i, Name: fmt.Sprintf("n%d0", i)})
	}
	tests := []struct {
		name     string
		seed     []Character
		userID   string
		charName string
		slot     int
		wantErr  error
		wantSlot int
	}{
		{name: "first character takes slot 0", charName: "Alice", userID: "u1", slot: -1, wantSlot: 0},
		{name: "lowest free slot", userID: "u1", charName: "Bob", slot: -1,
			seed: []Character{{ID: "a", Slot: 0}, {ID: "b", Slot: 2}}, wantSlot: 1},
		{name: "explicit free slot", userID: "u1", charName: "Bob", slot: 3,
			seed: []Character{{ID: "a", Slot: 0}}, wantSlot: 3},
		{name: "explicit taken slot", userID: "u1", charName: "Bob", slot: 0,
			seed: []Character{{ID: "a", Slot: 0}}, wantErr: ErrSlotTaken},
		{name: "slot out of range", userID: "u1", charName: "Bob", slot: MaxCharacterSlots, wantErr: ErrInvalidSlot},
		{name: "roster full", userID: "u1", charName: "Bob", slot: -1, seed: full, wantErr: ErrRosterFull},
		{name: "invalid name", userID: "u1", charName: "x", slot: -1, wantErr: ErrInvalidName},
		{name: "no user", userID: "", charName: "Bob", slot: -1, wantErr: ErrUnauthenticated},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			st := newMemStore()
			if tt.seed != nil {
				st.seedRoster(t, tt.userID, tt.seed...)
			}
			c, err := Create(context.Background(), st, tt.userID, tt.charName, tt.slot, fixedNow)
			if tt.wantErr != nil {
				if !errors.Is(err, tt.wantErr) {
					t.Fatalf("err = %v, want %v", err, tt.wantErr)
				}
				return
			}
			if err != nil {
				t.Fatalf("Create: %v", err)
			}
			if c.Slot != tt.wantSlot || c.Name != tt.charName || c.CreatedAt != fixedNow().Unix() {
				t.Errorf("character = %+v, want slot %d name %q", c, tt.wantSlot, tt.charName)
			}
			if !uuidV4.MatchString(c.ID) {
				t.Errorf("id %q is not a UUID v4", c.ID)
			}
			owns, err := Owns(context.Background(), st, tt.userID, c.ID)
			if err != nil || !owns {
				t.Errorf("Owns after create = %v, %v", owns, err)
			}
			list, _ := List(context.Background(), st, tt.userID)
			if len(list) != len(tt.seed)+1 {
				t.Errorf("roster size = %d, want %d", len(list), len(tt.seed)+1)
			}
		})
	}
}

func TestCreate_RetriesVersionConflict(t *testing.T) {
	tests := []struct {
		name      string
		conflicts int
		wantErr   error
	}{
		{name: "one lost race then success", conflicts: 1},
		{name: "all attempts lost", conflicts: maxWriteAttempts, wantErr: ErrRosterBusy},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			st := newMemStore()
			st.conflictNext = tt.conflicts
			_, err := Create(context.Background(), st, "u1", "Alice", -1, fixedNow)
			if tt.wantErr != nil {
				if !errors.Is(err, tt.wantErr) {
					t.Fatalf("err = %v, want %v", err, tt.wantErr)
				}
				return
			}
			if err != nil {
				t.Fatalf("Create: %v", err)
			}
		})
	}
}

// TestCreate_ConcurrentCannotExceedSlots races more creates than there are
// slots; the version check must hold the cap and slot uniqueness.
func TestCreate_ConcurrentCannotExceedSlots(t *testing.T) {
	st := newMemStore()
	const racers = MaxCharacterSlots * 3
	var wg sync.WaitGroup
	for i := 0; i < racers; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			_, _ = Create(context.Background(), st, "u1", fmt.Sprintf("Hero%d", i), -1, fixedNow)
		}(i)
	}
	wg.Wait()
	list, err := List(context.Background(), st, "u1")
	if err != nil {
		t.Fatal(err)
	}
	if len(list) > MaxCharacterSlots {
		t.Fatalf("roster has %d characters, cap is %d", len(list), MaxCharacterSlots)
	}
	seen := map[int]bool{}
	for _, c := range list {
		if seen[c.Slot] {
			t.Fatalf("slot %d used twice: %+v", c.Slot, list)
		}
		seen[c.Slot] = true
	}
}

func TestDelete(t *testing.T) {
	tests := []struct {
		name     string
		seed     []Character
		userID   string
		deleteID string
		wantErr  error
		wantLeft int
	}{
		{name: "deletes own character", userID: "u1", deleteID: "a",
			seed: []Character{{ID: "a", Slot: 0}, {ID: "b", Slot: 1}}, wantLeft: 1},
		{name: "unknown id", userID: "u1", deleteID: "zzz",
			seed: []Character{{ID: "a", Slot: 0}}, wantErr: ErrCharacterNotFound},
		{name: "empty roster", userID: "u1", deleteID: "a", wantErr: ErrCharacterNotFound},
		{name: "missing id", userID: "u1", deleteID: "", wantErr: ErrCharacterIDRequired},
		{name: "no user", userID: "", deleteID: "a", wantErr: ErrUnauthenticated},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			st := newMemStore()
			if tt.seed != nil {
				st.seedRoster(t, tt.userID, tt.seed...)
			}
			err := Delete(context.Background(), st, tt.userID, tt.deleteID)
			if tt.wantErr != nil {
				if !errors.Is(err, tt.wantErr) {
					t.Fatalf("err = %v, want %v", err, tt.wantErr)
				}
				return
			}
			if err != nil {
				t.Fatalf("Delete: %v", err)
			}
			list, _ := List(context.Background(), st, tt.userID)
			if len(list) != tt.wantLeft {
				t.Errorf("left = %d, want %d", len(list), tt.wantLeft)
			}
			if owns, _ := Owns(context.Background(), st, tt.userID, tt.deleteID); owns {
				t.Error("still owns deleted character")
			}
		})
	}
}

func TestOwns(t *testing.T) {
	st := newMemStore()
	st.seedRoster(t, "u1", Character{ID: "mine", Slot: 0})
	st.seedRoster(t, "u2", Character{ID: "theirs", Slot: 0})
	tests := []struct {
		name   string
		userID string
		cid    string
		want   bool
	}{
		{"own character", "u1", "mine", true},
		{"another account's character", "u1", "theirs", false},
		{"unknown character", "u1", "nope", false},
		{"empty character id", "u1", "", false},
		{"no roster at all", "u3", "mine", false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := Owns(context.Background(), st, tt.userID, tt.cid)
			if err != nil {
				t.Fatalf("Owns: %v", err)
			}
			if got != tt.want {
				t.Errorf("Owns = %v, want %v", got, tt.want)
			}
		})
	}
}

func TestOwns_StorageErrorPropagates(t *testing.T) {
	st := newMemStore()
	st.failRead = errors.New("db down")
	if _, err := Owns(context.Background(), st, "u1", "c"); err == nil {
		t.Fatal("want error")
	}
}

func TestRPCs(t *testing.T) {
	tests := []struct {
		name    string
		ctx     context.Context
		call    func(ctx context.Context, st *memStore) (string, error)
		wantErr error
		check   func(t *testing.T, out string)
	}{
		{
			name: "list empty roster",
			ctx:  userCtx("rpc-list-empty"),
			call: func(ctx context.Context, st *memStore) (string, error) { return listRPC(ctx, noopLogger{}, st) },
			check: func(t *testing.T, out string) {
				var r ListResponse
				if err := json.Unmarshal([]byte(out), &r); err != nil {
					t.Fatal(err)
				}
				if r.Characters == nil || len(r.Characters) != 0 || r.MaxSlots != MaxCharacterSlots {
					t.Errorf("list = %s", out)
				}
			},
		},
		{
			name: "create with slot",
			ctx:  userCtx("rpc-create"),
			call: func(ctx context.Context, st *memStore) (string, error) {
				return createRPC(ctx, noopLogger{}, st, `{"name":"Alice","slot":2}`)
			},
			check: func(t *testing.T, out string) {
				var r CreateResponse
				if err := json.Unmarshal([]byte(out), &r); err != nil {
					t.Fatal(err)
				}
				if r.Character.Slot != 2 || r.Character.Name != "Alice" {
					t.Errorf("create = %s", out)
				}
			},
		},
		{
			name: "create without payload",
			ctx:  userCtx("rpc-create-empty"),
			call: func(ctx context.Context, st *memStore) (string, error) {
				return createRPC(ctx, noopLogger{}, st, "")
			},
			wantErr: ErrInvalidPayload,
		},
		{
			name: "create bad json",
			ctx:  userCtx("rpc-create-badjson"),
			call: func(ctx context.Context, st *memStore) (string, error) {
				return createRPC(ctx, noopLogger{}, st, `{`)
			},
			wantErr: ErrInvalidPayload,
		},
		{
			name: "create bad name",
			ctx:  userCtx("rpc-create-badname"),
			call: func(ctx context.Context, st *memStore) (string, error) {
				return createRPC(ctx, noopLogger{}, st, `{"name":"!"}`)
			},
			wantErr: ErrInvalidName,
		},
		{
			name: "delete unknown",
			ctx:  userCtx("rpc-delete"),
			call: func(ctx context.Context, st *memStore) (string, error) {
				return deleteRPC(ctx, noopLogger{}, st, `{"character_id":"nope"}`)
			},
			wantErr: ErrCharacterNotFound,
		},
		{
			name: "storage failure is internal",
			ctx:  userCtx("rpc-list-fail"),
			call: func(ctx context.Context, st *memStore) (string, error) {
				st.failRead = errors.New("pq: relation storage does not exist")
				return listRPC(ctx, noopLogger{}, st)
			},
			wantErr: ErrInternal,
		},
		{
			name:    "no session",
			ctx:     context.Background(),
			call:    func(ctx context.Context, st *memStore) (string, error) { return listRPC(ctx, noopLogger{}, st) },
			wantErr: ErrUnauthenticated,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			out, err := tt.call(tt.ctx, newMemStore())
			if tt.wantErr != nil {
				if err != tt.wantErr {
					t.Fatalf("err = %v, want %v", err, tt.wantErr)
				}
				return
			}
			if err != nil {
				t.Fatalf("rpc: %v", err)
			}
			tt.check(t, out)
		})
	}
}

// TestRPCs_RateLimited drives the shared write bucket past its burst.
func TestRPCs_RateLimited(t *testing.T) {
	tests := []struct {
		name string
		call func(ctx context.Context, st *memStore) error
	}{
		{name: "create/delete share a bucket", call: func(ctx context.Context, st *memStore) error {
			_, err := deleteRPC(ctx, noopLogger{}, st, `{"character_id":"x"}`)
			return err
		}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			ctx := userCtx("rate-" + tt.name)
			st := newMemStore()
			var limited bool
			for i := 0; i < RosterWriteBurst+2; i++ {
				if err := tt.call(ctx, st); err == ErrRateLimited {
					limited = true
					break
				}
			}
			if !limited {
				t.Fatalf("never rate limited after %d calls", RosterWriteBurst+2)
			}
		})
	}
}
