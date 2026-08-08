package registry

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

type failingRegistry struct {
	storage.ServerRegistry
	failsRemaining atomic.Int32
	callCount      atomic.Int32
	err            error
}

func newFailingRegistry(inner storage.ServerRegistry, failures int, err error) *failingRegistry {
	f := &failingRegistry{ServerRegistry: inner, err: err}
	f.failsRemaining.Store(int32(failures))
	return f
}

func (f *failingRegistry) FindByMapID(ctx context.Context, mapID string) ([]storage.ServerInfo, error) {
	f.callCount.Add(1)
	if f.failsRemaining.Add(-1) >= 0 {
		return nil, f.err
	}
	return f.ServerRegistry.FindByMapID(ctx, mapID)
}

func (f *failingRegistry) GetServer(ctx context.Context, serverID string) (storage.ServerInfo, error) {
	f.callCount.Add(1)
	if f.failsRemaining.Add(-1) >= 0 {
		return storage.ServerInfo{}, f.err
	}
	return f.ServerRegistry.GetServer(ctx, serverID)
}

type spyLogger struct {
	calls atomic.Int32
}

func (l *spyLogger) Warn(_ string, _ ...any) { l.calls.Add(1) }

func TestRetry_SucceedsAfterTransientFailure(t *testing.T) {
	inner := storage.NewMemoryServerRegistry()
	ctx := context.Background()
	_ = inner.Register(ctx, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	})
	reg := newFailingRegistry(inner, 2, errors.New("redis: connection refused"))
	spy := &spyLogger{}
	svc := NewRegistryService(reg, WithLogger(spy))

	got, err := svc.FindServer(ctx, "map_01")
	if err != nil {
		t.Fatalf("FindServer() error: %v", err)
	}
	if got.ServerID != "srv1" {
		t.Errorf("ServerID = %q, want %q", got.ServerID, "srv1")
	}
	if calls := reg.callCount.Load(); calls != 3 {
		t.Errorf("FindByMapID called %d times, want 3", calls)
	}
	if warns := spy.calls.Load(); warns != 2 {
		t.Errorf("Warn called %d times, want 2", warns)
	}
}

func TestRetry_ExhaustsRetries(t *testing.T) {
	inner := storage.NewMemoryServerRegistry()
	reg := newFailingRegistry(inner, 10, errors.New("redis: connection refused"))
	svc := NewRegistryService(reg)

	_, err := svc.FindServer(context.Background(), "map_01")
	if err == nil {
		t.Fatal("FindServer() should fail after exhausting retries")
	}
	if calls := reg.callCount.Load(); calls != 4 {
		t.Errorf("FindByMapID called %d times, want 4", calls)
	}
}

func TestRetry_DoesNotRetryBusinessLogicError(t *testing.T) {
	inner := storage.NewMemoryServerRegistry()
	reg := newFailingRegistry(inner, 10, storage.ErrNotFound)
	svc := NewRegistryService(reg)

	_, err := svc.FindServer(context.Background(), "map_01")
	if err == nil {
		t.Fatal("FindServer() should fail on ErrNotFound")
	}
	if calls := reg.callCount.Load(); calls != 1 {
		t.Errorf("FindByMapID called %d times, want 1", calls)
	}
}

func TestRetry_NoRetryOnSuccess(t *testing.T) {
	inner := storage.NewMemoryServerRegistry()
	ctx := context.Background()
	_ = inner.Register(ctx, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	})
	reg := newFailingRegistry(inner, 0, nil)
	svc := NewRegistryService(reg)

	_, err := svc.FindServer(ctx, "map_01")
	if err != nil {
		t.Fatalf("FindServer() error: %v", err)
	}
	if calls := reg.callCount.Load(); calls != 1 {
		t.Errorf("FindByMapID called %d times, want 1", calls)
	}
}

func TestRetry_AbortsOnContextCancel(t *testing.T) {
	inner := storage.NewMemoryServerRegistry()
	reg := newFailingRegistry(inner, 100, errors.New("redis: timeout"))
	svc := NewRegistryService(reg)

	ctx, cancel := context.WithCancel(context.Background())
	go func() {
		time.Sleep(50 * time.Millisecond)
		cancel()
	}()

	start := time.Now()
	_, err := svc.FindServer(ctx, "map_01")
	elapsed := time.Since(start)

	if err == nil {
		t.Fatal("FindServer() should fail on cancelled context")
	}
	if !errors.Is(err, context.Canceled) {
		t.Errorf("error should wrap context.Canceled, got: %v", err)
	}
	if elapsed > 2*time.Second {
		t.Errorf("took %v, expected abort within 2s", elapsed)
	}
}

func TestRetry_GetServerRetriesTransientErrors(t *testing.T) {
	inner := storage.NewMemoryServerRegistry()
	ctx := context.Background()
	_ = inner.Register(ctx, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_01", Addr: "10.0.0.1:9000", Capacity: 100,
	})
	reg := newFailingRegistry(inner, 1, errors.New("redis: connection refused"))
	spy := &spyLogger{}
	svc := NewRegistryService(reg, WithLogger(spy))

	got, err := svc.GetServer(ctx, "srv1")
	if err != nil {
		t.Fatalf("GetServer() error: %v", err)
	}
	if got.ServerID != "srv1" {
		t.Errorf("ServerID = %q, want %q", got.ServerID, "srv1")
	}
	if calls := reg.callCount.Load(); calls != 2 {
		t.Errorf("GetServer called %d times, want 2", calls)
	}
}

func TestIsRetriable(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want bool
	}{
		{"nil", nil, false},
		{"ErrNoServerAvailable", ErrNoServerAvailable, false},
		{"wrapped ErrNoServerAvailable", errors.Join(ErrNoServerAvailable, errors.New("details")), false},
		{"ErrNotFound", storage.ErrNotFound, false},
		{"redis connection error", errors.New("redis: connection refused"), true},
		{"redis timeout", errors.New("redis: i/o timeout"), true},
		{"generic error", errors.New("something went wrong"), true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := isRetriable(tt.err); got != tt.want {
				t.Errorf("isRetriable() = %v, want %v", got, tt.want)
			}
		})
	}
}
