package registry

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/storage"
)

// allocatedBody is a trimmed copy of a real Agones allocation response.
const allocatedBody = `{
  "kind": "GameServerAllocation",
  "apiVersion": "allocation.agones.dev/v1",
  "status": {
    "state": "Allocated",
    "gameServerName": "map-servers-dev-xjh7p-6ndtl",
    "ports": [{"name": "game", "port": 7257}],
    "address": "192.168.65.3"
  }
}`

const unallocatedBody = `{"status":{"state":"UnAllocated"}}`

func TestAgonesAllocator_Allocate(t *testing.T) {
	tests := []struct {
		name       string
		status     int
		body       string
		delay      time.Duration
		timeout    time.Duration
		want       storage.ServerInfo
		wantErr    bool
		wantErrIs  error
		wantErrSub string
	}{
		{
			name:   "success",
			status: http.StatusCreated,
			body:   allocatedBody,
			want: storage.ServerInfo{
				ServerID: "map-servers-dev-xjh7p-6ndtl",
				MapID:    "map_01",
				Addr:     "192.168.65.3:7257",
				// Empty AgonesConfig.Transport normalizes to the tcp default.
				Transport:   "tcp",
				Capacity:    DefaultCapacity,
				PlayerCount: 0,
			},
		},
		{
			name:      "no capacity - UnAllocated",
			status:    http.StatusCreated,
			body:      unallocatedBody,
			wantErr:   true,
			wantErrIs: ErrNoCapacity,
		},
		{
			name:       "api error carries status message",
			status:     http.StatusForbidden,
			body:       `{"kind":"Status","message":"gameserverallocations is forbidden"}`,
			wantErr:    true,
			wantErrSub: "gameserverallocations is forbidden",
		},
		{
			name:       "malformed body",
			status:     http.StatusCreated,
			body:       `not json`,
			wantErr:    true,
			wantErrSub: "decode response",
		},
		{
			name:       "allocated but no ports",
			status:     http.StatusCreated,
			body:       `{"status":{"state":"Allocated","gameServerName":"gs-1","address":"10.0.0.1"}}`,
			wantErr:    true,
			wantErrSub: `no "game" port`,
		},
		{
			name:       "timeout",
			status:     http.StatusCreated,
			body:       allocatedBody,
			delay:      200 * time.Millisecond,
			timeout:    20 * time.Millisecond,
			wantErr:    true,
			wantErrSub: "call allocation api",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if tt.delay > 0 {
					time.Sleep(tt.delay)
				}
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(tt.status)
				_, _ = io.WriteString(w, tt.body)
			}))
			defer srv.Close()

			alloc := newAgonesAllocator(srv.Client(), srv.URL, AgonesConfig{Timeout: tt.timeout})
			got, err := alloc.AllocateServer(context.Background(), "map_01")

			if tt.wantErr {
				if err == nil {
					t.Fatalf("expected error, got %+v", got)
				}
				if tt.wantErrIs != nil && !errors.Is(err, tt.wantErrIs) {
					t.Fatalf("expected errors.Is(%v), got %v", tt.wantErrIs, err)
				}
				if tt.wantErrSub != "" && !strings.Contains(err.Error(), tt.wantErrSub) {
					t.Fatalf("error %q does not contain %q", err, tt.wantErrSub)
				}
				return
			}
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if got != tt.want {
				t.Fatalf("got %+v, want %+v", got, tt.want)
			}
		})
	}
}

// TestAgonesAllocator_RequestShape asserts the exact payload and URL the
// gateway posts to the aggregated allocation API.
func TestAgonesAllocator_RequestShape(t *testing.T) {
	tests := []struct {
		name      string
		kind      string
		wantFleet string
	}{
		{name: "map kind selects map fleet", kind: KindMap, wantFleet: "fleet-map"},
		{name: "empty kind defaults to map", kind: "", wantFleet: "fleet-map"},
		{name: "dungeon kind selects dungeon fleet", kind: KindDungeon, wantFleet: "fleet-dungeon"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var (
				gotPath   string
				gotMethod string
				gotBody   allocationRequestBody
			)
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				gotPath, gotMethod = r.URL.Path, r.Method
				_ = json.NewDecoder(r.Body).Decode(&gotBody)
				w.WriteHeader(http.StatusCreated)
				_, _ = io.WriteString(w, allocatedBody)
			}))
			defer srv.Close()

			alloc := newAgonesAllocator(srv.Client(), srv.URL, AgonesConfig{
				Namespace:    "rpg-ns",
				FleetMap:     "fleet-map",
				FleetDungeon: "fleet-dungeon",
			})
			if _, err := alloc.Allocate(context.Background(), AllocationRequest{Kind: tt.kind, MapID: "m"}); err != nil {
				t.Fatalf("allocate: %v", err)
			}

			if gotMethod != http.MethodPost {
				t.Errorf("method = %s, want POST", gotMethod)
			}
			wantPath := "/apis/allocation.agones.dev/v1/namespaces/rpg-ns/gameserverallocations"
			if gotPath != wantPath {
				t.Errorf("path = %s, want %s", gotPath, wantPath)
			}
			if gotBody.APIVersion != "allocation.agones.dev/v1" || gotBody.Kind != "GameServerAllocation" {
				t.Errorf("bad type meta: %+v", gotBody)
			}
			if gotBody.Metadata.Namespace != "rpg-ns" {
				t.Errorf("namespace = %s, want rpg-ns", gotBody.Metadata.Namespace)
			}
			if len(gotBody.Spec.Selectors) != 1 {
				t.Fatalf("selectors = %+v, want 1", gotBody.Spec.Selectors)
			}
			if got := gotBody.Spec.Selectors[0].MatchLabels[fleetLabel]; got != tt.wantFleet {
				t.Errorf("%s = %s, want %s", fleetLabel, got, tt.wantFleet)
			}
		})
	}
}

func TestAgonesAllocator_UnknownKind(t *testing.T) {
	alloc := newAgonesAllocator(http.DefaultClient, "http://unused", AgonesConfig{})
	if _, err := alloc.Allocate(context.Background(), AllocationRequest{Kind: "raid"}); err == nil {
		t.Fatal("expected error for unknown kind")
	}
}

func TestAgonesConfig_Defaults(t *testing.T) {
	alloc := newAgonesAllocator(http.DefaultClient, "http://unused/", AgonesConfig{})
	if alloc.namespace != DefaultNamespace {
		t.Errorf("namespace = %s, want %s", alloc.namespace, DefaultNamespace)
	}
	if alloc.fleets[KindMap] != DefaultFleetMap || alloc.fleets[KindDungeon] != DefaultFleetDungeon {
		t.Errorf("fleets = %+v", alloc.fleets)
	}
	if alloc.capacity != DefaultCapacity || alloc.timeout != DefaultTimeout {
		t.Errorf("capacity/timeout = %d/%s", alloc.capacity, alloc.timeout)
	}
	if alloc.baseURL != "http://unused" {
		t.Errorf("baseURL = %s, want trailing slash trimmed", alloc.baseURL)
	}
}

// TestAgonesAllocator_Transport pins the transport the allocator stamps onto
// an allocated ServerInfo. The gateway announces that value to the client
// before the pod's own registration lands, so a wrong value sends the client
// to a game server over the wrong transport.
func TestAgonesAllocator_Transport(t *testing.T) {
	const allocated = `{"status":{"state":"Allocated","gameServerName":"gs-1","address":"10.0.0.1",` +
		`"ports":[{"name":"game","port":7777}]}}`

	tests := []struct {
		name      string
		configure string
		want      string
	}{
		{name: "unset defaults to tcp", configure: "", want: "tcp"},
		{name: "explicit tcp", configure: "tcp", want: "tcp"},
		{name: "kcp fleet", configure: "kcp", want: "kcp"},
		{name: "normalized", configure: "KCP", want: "kcp"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(http.StatusCreated)
				_, _ = io.WriteString(w, allocated)
			}))
			defer srv.Close()

			alloc := newAgonesAllocator(srv.Client(), srv.URL, AgonesConfig{Transport: tt.configure})
			got, err := alloc.AllocateServer(context.Background(), "map_01")
			if err != nil {
				t.Fatalf("AllocateServer: %v", err)
			}
			if got.Transport != tt.want {
				t.Errorf("Transport = %q, want %q", got.Transport, tt.want)
			}
		})
	}
}
