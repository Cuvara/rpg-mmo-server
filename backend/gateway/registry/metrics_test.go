package registry

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/shared/storage"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/testutil"
)

func TestRegistryMetrics_AllocationsTotal(t *testing.T) {
	// An allocation counts as a success only once the allocated server has
	// registered itself and a client could actually be sent there.
	tests := []struct {
		name             string
		newAlloc         func(store storage.ServerRegistry) Allocator
		wantOK, wantFail float64
		wantErr          bool
	}{
		{
			name: "allocation succeeds once the server registers",
			newAlloc: func(store storage.ServerRegistry) Allocator {
				return &fakeAllocator{
					info: storage.ServerInfo{
						ServerID: "gs-1", MapID: "map_void", Addr: "10.0.0.9:9000", Capacity: 50,
					},
					selfRegister:  store,
					registerAfter: 10 * time.Millisecond,
				}
			},
			wantOK: 1,
		},
		{
			name: "allocation fails",
			newAlloc: func(storage.ServerRegistry) Allocator {
				return &fakeAllocator{err: errors.New("fleet exhausted")}
			},
			wantFail: 1,
			wantErr:  true,
		},
		{
			name: "allocated server never registers counts as a failure",
			newAlloc: func(storage.ServerRegistry) Allocator {
				return &fakeAllocator{info: storage.ServerInfo{
					ServerID: "gs-ghost", MapID: "map_void", Addr: "10.0.0.9:9000", Capacity: 50,
				}}
			},
			wantFail: 1,
			wantErr:  true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			m := metrics.New(prometheus.NewRegistry())
			store := storage.NewMemoryServerRegistry()
			svc := NewRegistryServiceWithAllocator(store, tt.newAlloc(store), WithMetrics(m),
				WithAllocationWait(200*time.Millisecond, 5*time.Millisecond))

			_, err := svc.FindServer(context.Background(), "map_void")
			if (err != nil) != tt.wantErr {
				t.Fatalf("FindServer err = %v, wantErr %v", err, tt.wantErr)
			}

			if got := testutil.ToFloat64(m.AllocationsTotal.WithLabelValues(metrics.ResultOK)); got != tt.wantOK {
				t.Errorf("allocations ok = %v, want %v", got, tt.wantOK)
			}
			if got := testutil.ToFloat64(m.AllocationsTotal.WithLabelValues(metrics.ResultFail)); got != tt.wantFail {
				t.Errorf("allocations fail = %v, want %v", got, tt.wantFail)
			}
		})
	}
}

// No allocator configured: an unserved map is a plain error and must not be
// counted as an allocation attempt.
func TestRegistryMetrics_NoAllocatorNoCount(t *testing.T) {
	m := metrics.New(prometheus.NewRegistry())
	svc := NewRegistryService(storage.NewMemoryServerRegistry(), WithMetrics(m))

	if _, err := svc.FindServer(context.Background(), "map_void"); err == nil {
		t.Fatal("FindServer should fail with no server and no allocator")
	}
	for _, res := range []string{metrics.ResultOK, metrics.ResultFail} {
		if got := testutil.ToFloat64(m.AllocationsTotal.WithLabelValues(res)); got != 0 {
			t.Errorf("allocations %s = %v, want 0", res, got)
		}
	}
}
