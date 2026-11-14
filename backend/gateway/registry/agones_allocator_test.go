package registry

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/transport"
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
				GameID:      constants.DefaultGameID,
				ServerID:    "map-servers-dev-xjh7p-6ndtl",
				MapID:       "map_01",
				Addr:        "192.168.65.3:7257",
				Transport:   transport.Gameplay,
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
			got, err := alloc.AllocateServer(context.Background(), constants.DefaultGameID, "map_01")

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

// The default map fleet must name the fleet that actually exists. It named the
// retired Go fleet (`map-servers-dev`) until 2026-08-17 and nothing caught it:
// the allocator never validates the name, so the failure only appeared at the
// first allocation.
func TestAgonesConfig_DefaultMapFleetIsTheDeployedFleet(t *testing.T) {
	const deployed = "map-servers-dotnet-dev"
	if DefaultFleetMap != deployed {
		t.Errorf("DefaultFleetMap = %q, want %q (the only deployed fleet; keep in step with backend/deploy/agones/)",
			DefaultFleetMap, deployed)
	}
}

// No dungeon fleet is deployed, so a dungeon allocation must fail as a
// configuration error naming the flag to set — not as a Kubernetes 404 for a
// fleet that never existed.
func TestAgonesAllocator_DungeonKindUnconfiguredByDefault(t *testing.T) {
	alloc := newAgonesAllocator(http.DefaultClient, "http://unused", AgonesConfig{})
	if _, ok := alloc.fleets[KindDungeon]; ok {
		t.Fatalf("dungeon fleet is configured by default: %+v", alloc.fleets)
	}

	_, err := alloc.Allocate(context.Background(), AllocationRequest{Kind: KindDungeon, MapID: "d"})
	if !errors.Is(err, ErrKindNotConfigured) {
		t.Fatalf("Allocate(dungeon) error = %v, want ErrKindNotConfigured", err)
	}
	if !strings.Contains(err.Error(), "ALLOCATOR_FLEET_DUNGEON") {
		t.Errorf("error %q does not name the setting that fixes it", err)
	}

	// Explicitly configured, it works again.
	configured := newAgonesAllocator(http.DefaultClient, "http://unused", AgonesConfig{FleetDungeon: "fleet-dungeon"})
	if configured.fleets[KindDungeon] != "fleet-dungeon" {
		t.Errorf("fleets = %+v, want the configured dungeon fleet", configured.fleets)
	}
}

func TestAgonesConfig_Defaults(t *testing.T) {
	alloc := newAgonesAllocator(http.DefaultClient, "http://unused/", AgonesConfig{})
	if alloc.namespace != DefaultNamespace {
		t.Errorf("namespace = %s, want %s", alloc.namespace, DefaultNamespace)
	}
	if alloc.fleets[KindMap] != DefaultFleetMap {
		t.Errorf("fleets = %+v", alloc.fleets)
	}
	if alloc.capacity != DefaultCapacity || alloc.timeout != DefaultTimeout {
		t.Errorf("capacity/timeout = %d/%s", alloc.capacity, alloc.timeout)
	}
	if alloc.baseURL != "http://unused" {
		t.Errorf("baseURL = %s, want trailing slash trimmed", alloc.baseURL)
	}
}

// TestAgonesAllocator_Transport pins that an allocated server is always stamped
// with the gameplay transport. There is no per-fleet transport knob any more:
// every game server speaks KCP, so nothing here can route a client to a game
// server over TCP.
func TestAgonesAllocator_Transport(t *testing.T) {
	const allocated = `{"status":{"state":"Allocated","gameServerName":"gs-1","address":"10.0.0.1",` +
		`"ports":[{"name":"game","port":7777}]}}`
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusCreated)
		_, _ = io.WriteString(w, allocated)
	}))
	defer srv.Close()

	alloc := newAgonesAllocator(srv.Client(), srv.URL, AgonesConfig{})
	got, err := alloc.AllocateServer(context.Background(), constants.DefaultGameID, "map_01")
	if err != nil {
		t.Fatalf("AllocateServer: %v", err)
	}
	if got.Transport != transport.Gameplay {
		t.Errorf("Transport = %q, want %q", got.Transport, transport.Gameplay)
	}
}

func TestAgonesAllocator_PerGameFleetSelection(t *testing.T) {
	for _, tt := range []struct {
		name, game, kind, fleet, namespace, env string
	}{
		{name: "absent game map", fleet: "default-map", namespace: "stack"},
		{name: "cuvara map", game: constants.DefaultGameID, kind: KindMap, fleet: "default-map", namespace: "stack"},
		{name: "cuvara dungeon", game: constants.DefaultGameID, kind: KindDungeon, fleet: "default-dungeon", namespace: "stack"},
		{name: "foo map", game: "foo", kind: KindMap, fleet: "foo-map", namespace: "stack-foo"},
		{name: "foo dungeon", game: "foo", kind: KindDungeon, fleet: "foo-dungeon", namespace: "stack-foo"},
		{name: "bar map custom namespace", game: "bar", kind: KindMap, fleet: "bar-map", namespace: "custom-bar"},
		{name: "bar dungeon missing", game: "bar", kind: KindDungeon, env: "ALLOCATOR_FLEET_DUNGEON_BAR"},
		{name: "unknown game map", game: "baz", kind: KindMap, env: "ALLOCATOR_FLEET_MAP_BAZ"},
		{name: "unknown game dungeon", game: "baz", kind: KindDungeon, env: "ALLOCATOR_FLEET_DUNGEON_BAZ"},
	} {
		t.Run(tt.name, func(t *testing.T) {
			calls := 0
			var gotBody allocationRequestBody
			var gotPath string
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				gotPath = r.URL.Path
				if err := json.NewDecoder(r.Body).Decode(&gotBody); err != nil {
					t.Error(err)
				}
				body := allocatedBody
				if tt.game != "" && tt.game != constants.DefaultGameID {
					body = strings.ReplaceAll(body, "map-servers-dev-xjh7p-6ndtl", tt.game+"-allocated-server")
				}
				_, _ = io.WriteString(w, body)
			}))
			defer srv.Close()
			alloc := newAgonesAllocator(srv.Client(), srv.URL, AgonesConfig{
				Namespace: "stack", FleetMap: "default-map", FleetDungeon: "default-dungeon",
				Games: map[string]GameFleets{
					"foo": {Map: "foo-map", Dungeon: "foo-dungeon"},
					"bar": {Map: "bar-map", Namespace: "custom-bar"},
					// Default-game settings retain precedence over Games entries.
					constants.DefaultGameID: {Map: "ignored", Dungeon: "ignored", Namespace: "ignored"},
				},
			})
			got, err := alloc.Allocate(context.Background(), AllocationRequest{Game: tt.game, Kind: tt.kind, MapID: "map_01"})
			if tt.env != "" {
				if !errors.Is(err, ErrKindNotConfigured) || !strings.Contains(err.Error(), tt.env) || !strings.Contains(err.Error(), tt.game) {
					t.Fatalf("Allocate = %+v, %v; want ErrKindNotConfigured naming %s and %s", got, err, tt.game, tt.env)
				}
				if calls != 0 || got != (storage.ServerInfo{}) {
					t.Fatalf("unconfigured allocation issued %d HTTP calls or returned %+v", calls, got)
				}
				return
			}
			if err != nil || calls != 1 || got.GameID != constants.GameOrDefault(tt.game) || got.MapID != "map_01" {
				t.Fatalf("Allocate = %+v, %v; calls=%d", got, err, calls)
			}
			wantPath := fmt.Sprintf(allocationPath, tt.namespace)
			if gotPath != wantPath || gotBody.Metadata.Namespace != tt.namespace {
				t.Errorf("namespace: path=%q body=%q; want %q", gotPath, gotBody.Metadata.Namespace, tt.namespace)
			}
			if len(gotBody.Spec.Selectors) != 1 || gotBody.Spec.Selectors[0].MatchLabels[fleetLabel] != tt.fleet {
				t.Errorf("fleet selectors = %+v; want %s", gotBody.Spec.Selectors, tt.fleet)
			}
		})
	}
}

func TestFleetEnvName(t *testing.T) {
	for _, tt := range []struct{ game, kind, want string }{
		{"", KindMap, "ALLOCATOR_FLEET_MAP"},
		{constants.DefaultGameID, KindMap, "ALLOCATOR_FLEET_MAP"},
		{"", KindDungeon, "ALLOCATOR_FLEET_DUNGEON"},
		{constants.DefaultGameID, KindDungeon, "ALLOCATOR_FLEET_DUNGEON"},
		{"foo", KindMap, "ALLOCATOR_FLEET_MAP_FOO"},
		{"foo", KindDungeon, "ALLOCATOR_FLEET_DUNGEON_FOO"},
	} {
		t.Run(tt.game+"/"+tt.kind, func(t *testing.T) {
			if got := FleetEnvName(tt.kind, tt.game); got != tt.want {
				t.Errorf("FleetEnvName = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestAgonesAllocator_AllocatedNameMustBelongToGame(t *testing.T) {
	for _, tt := range []struct {
		game, server string
		wantErr      bool
	}{
		{constants.DefaultGameID, "map-servers-dev-xjh7p-6ndtl", false},
		{"foo", "foo-map-servers-dev-xjh7p-6ndtl", false},
		{"foo", "map-servers-dev-xjh7p-6ndtl", true},
		{"foo", "foobar-map-servers-dev-xjh7p-6ndtl", true},
		{"foo", "bar-map-servers-dev-xjh7p-6ndtl", true},
		{"foo", "foo-", true},
	} {
		t.Run(tt.game+"/"+tt.server, func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				_, _ = io.WriteString(w, strings.ReplaceAll(allocatedBody, "map-servers-dev-xjh7p-6ndtl", tt.server))
			}))
			defer srv.Close()
			alloc := newAgonesAllocator(srv.Client(), srv.URL, AgonesConfig{Games: map[string]GameFleets{
				"foo": {Map: "foo-fleet"},
			}})
			got, err := alloc.AllocateServer(context.Background(), tt.game, "map_01")
			if tt.wantErr {
				if err == nil || got != (storage.ServerInfo{}) || !strings.Contains(err.Error(), tt.game+"-") || !strings.Contains(err.Error(), tt.server) {
					t.Fatalf("AllocateServer = %+v, %v; want refusal naming server and expected game prefix", got, err)
				}
			} else if err != nil || got.ServerID != tt.server || got.GameID != tt.game {
				t.Fatalf("AllocateServer = %+v, %v", got, err)
			}
		})
	}
}
