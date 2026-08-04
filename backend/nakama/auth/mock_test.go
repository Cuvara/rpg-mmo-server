package auth

import (
	"context"
	"errors"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// mockStore is a minimal in-memory implementation of profileStore for tests.
// Only the storage methods used by the auth package are implemented.
type mockStore struct {
	objects   map[string]*api.StorageObject // key: collection/key/userID
	writes    []*runtime.StorageWrite
	readErr   error
	writeErr  error
	readCalls int
}

func newMockStore() *mockStore {
	return &mockStore{objects: make(map[string]*api.StorageObject)}
}

func storageKey(collection, key, userID string) string {
	return collection + "/" + key + "/" + userID
}

func (m *mockStore) seed(collection, key, userID, value string) {
	m.objects[storageKey(collection, key, userID)] = &api.StorageObject{
		Collection: collection,
		Key:        key,
		UserId:     userID,
		Value:      value,
	}
}

func (m *mockStore) StorageRead(_ context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error) {
	m.readCalls++
	if m.readErr != nil {
		return nil, m.readErr
	}
	out := make([]*api.StorageObject, 0, len(reads))
	for _, r := range reads {
		if obj, ok := m.objects[storageKey(r.Collection, r.Key, r.UserID)]; ok {
			out = append(out, obj)
		}
	}
	return out, nil
}

func (m *mockStore) StorageWrite(_ context.Context, writes []*runtime.StorageWrite) ([]*api.StorageObjectAck, error) {
	if m.writeErr != nil {
		return nil, m.writeErr
	}
	acks := make([]*api.StorageObjectAck, 0, len(writes))
	for _, w := range writes {
		m.writes = append(m.writes, w)
		m.objects[storageKey(w.Collection, w.Key, w.UserID)] = &api.StorageObject{
			Collection: w.Collection,
			Key:        w.Key,
			UserId:     w.UserID,
			Value:      w.Value,
		}
		acks = append(acks, &api.StorageObjectAck{Collection: w.Collection, Key: w.Key, UserId: w.UserID})
	}
	return acks, nil
}

// mockNakama satisfies the full runtime.NakamaModule interface by embedding it
// (nil) and overriding only the storage methods the auth hooks actually call.
// Any other method call panics, which is the intended test signal.
type mockNakama struct {
	runtime.NakamaModule
	*mockStore
}

func newMockNakama() *mockNakama {
	return &mockNakama{mockStore: newMockStore()}
}

// StorageRead disambiguates between the embedded interface and *mockStore.
func (m *mockNakama) StorageRead(ctx context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error) {
	return m.mockStore.StorageRead(ctx, reads)
}

// StorageWrite disambiguates between the embedded interface and *mockStore.
func (m *mockNakama) StorageWrite(ctx context.Context, writes []*runtime.StorageWrite) ([]*api.StorageObjectAck, error) {
	return m.mockStore.StorageWrite(ctx, writes)
}

// errStorage is a canned storage failure used in table-driven tests.
var errStorage = errors.New("storage unavailable")
