package registry

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"k8s.io/client-go/rest"
	"k8s.io/client-go/tools/clientcmd"

	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// Allocation kinds. The kind selects which Agones Fleet the allocation targets.
const (
	KindMap     = "map"
	KindDungeon = "dungeon"
)

// Defaults for the Agones allocator. They match backend/deploy/agones/*-dev.yaml.
const (
	DefaultNamespace    = "rpg-realtime"
	DefaultFleetMap     = "map-servers-dev"
	DefaultFleetDungeon = "dungeon-servers-dev"
	DefaultCapacity     = 100
	DefaultTimeout      = 10 * time.Second

	// allocationPath is the aggregated allocation API endpoint. %s is the namespace.
	allocationPath = "/apis/allocation.agones.dev/v1/namespaces/%s/gameserverallocations"

	// fleetLabel is the label Agones stamps on every GameServer of a Fleet.
	fleetLabel = "agones.dev/fleet"

	// gamePortName matches the `ports[].name` of the fleet manifests.
	gamePortName = "game"
)

// ErrNoCapacity is returned when the allocation API answers UnAllocated: every
// GameServer in the fleet is already taken and the fleet cannot grow right now.
var ErrNoCapacity = errors.New("no game server available in fleet")

// AllocationRequest describes what the gateway wants allocated.
type AllocationRequest struct {
	// Kind selects the fleet: KindMap or KindDungeon. Empty means KindMap.
	Kind string
	// MapID (or dungeon id) that the allocated server will host. It is only
	// used to fill the resulting ServerInfo — fleet selection is by Kind.
	MapID string
}

// KindAllocator allocates a game server for a specific fleet kind. It is the
// richer form of Allocator; AgonesAllocator implements both.
type KindAllocator interface {
	Allocate(ctx context.Context, req AllocationRequest) (storage.ServerInfo, error)
}

// AgonesConfig configures the Agones allocator.
type AgonesConfig struct {
	// Namespace holding the fleets (default DefaultNamespace).
	Namespace string
	// FleetMap / FleetDungeon are the Fleet names selected per kind.
	FleetMap     string
	FleetDungeon string
	// Kubeconfig is an explicit kubeconfig path. Empty falls back to the
	// in-cluster service account, then $KUBECONFIG, then ~/.kube/config.
	Kubeconfig string
	// Capacity reported for an allocated server in the registry.
	Capacity int
	// Transport the fleet's game servers listen with ("tcp" or "kcp"). It is
	// stamped onto the allocated ServerInfo so the gateway announces the right
	// transport to the client before the pod's own registration lands. Empty
	// means tcp. Must match the fleet manifest's --transport argument.
	Transport string
	// Timeout bounds a single allocation call (default DefaultTimeout).
	Timeout time.Duration
}

func (c *AgonesConfig) applyDefaults() {
	if c.Namespace == "" {
		c.Namespace = DefaultNamespace
	}
	if c.FleetMap == "" {
		c.FleetMap = DefaultFleetMap
	}
	if c.FleetDungeon == "" {
		c.FleetDungeon = DefaultFleetDungeon
	}
	if c.Capacity <= 0 {
		c.Capacity = DefaultCapacity
	}
	if c.Timeout <= 0 {
		c.Timeout = DefaultTimeout
	}
	c.Transport = transport.Normalize(c.Transport)
}

// AgonesAllocator asks the Agones aggregated allocation API for a GameServer.
//
// It deliberately speaks the allocation REST endpoint directly instead of
// importing the Agones clientset: the wire shape is a handful of fields, while
// agones.dev/agones/pkg/apis drags viper/hcl/fsnotify into the gateway binary.
// See docs/DESIGN.md ("Agones allocator").
type AgonesAllocator struct {
	http      *http.Client
	baseURL   string
	namespace string
	fleets    map[string]string
	capacity  int
	timeout   time.Duration
	transport string
}

// Compile-time interface checks.
var (
	_ Allocator     = (*AgonesAllocator)(nil)
	_ KindAllocator = (*AgonesAllocator)(nil)
)

// NewAgonesAllocator builds an allocator from the ambient Kubernetes config:
// the in-cluster service account when the gateway runs as a pod, otherwise a
// kubeconfig (cfg.Kubeconfig, $KUBECONFIG, ~/.kube/config — in that order).
func NewAgonesAllocator(cfg AgonesConfig) (*AgonesAllocator, error) {
	restCfg, err := resolveRESTConfig(cfg.Kubeconfig)
	if err != nil {
		return nil, fmt.Errorf("kubernetes config: %w", err)
	}
	httpClient, err := rest.HTTPClientFor(restCfg)
	if err != nil {
		return nil, fmt.Errorf("kubernetes http client: %w", err)
	}
	return newAgonesAllocator(httpClient, restCfg.Host, cfg), nil
}

// newAgonesAllocator wires an allocator around an explicit HTTP client and API
// base URL. Tests use it against an httptest server.
func newAgonesAllocator(httpClient *http.Client, baseURL string, cfg AgonesConfig) *AgonesAllocator {
	cfg.applyDefaults()
	return &AgonesAllocator{
		http:      httpClient,
		baseURL:   strings.TrimRight(baseURL, "/"),
		namespace: cfg.Namespace,
		fleets: map[string]string{
			KindMap:     cfg.FleetMap,
			KindDungeon: cfg.FleetDungeon,
		},
		capacity:  cfg.Capacity,
		timeout:   cfg.Timeout,
		transport: cfg.Transport,
	}
}

// resolveRESTConfig prefers the in-cluster service account, then an explicit
// kubeconfig path, $KUBECONFIG, and finally ~/.kube/config.
func resolveRESTConfig(kubeconfig string) (*rest.Config, error) {
	if kubeconfig == "" {
		if cfg, err := rest.InClusterConfig(); err == nil {
			return cfg, nil
		}
		kubeconfig = os.Getenv("KUBECONFIG")
	}
	if kubeconfig == "" {
		home, err := os.UserHomeDir()
		if err != nil {
			return nil, fmt.Errorf("locate home dir for kubeconfig: %w", err)
		}
		kubeconfig = filepath.Join(home, ".kube", "config")
	}
	cfg, err := clientcmd.BuildConfigFromFlags("", kubeconfig)
	if err != nil {
		return nil, fmt.Errorf("load kubeconfig %s: %w", kubeconfig, err)
	}
	return cfg, nil
}

// AllocateServer allocates a map server for mapID (Allocator interface).
func (a *AgonesAllocator) AllocateServer(ctx context.Context, mapID string) (storage.ServerInfo, error) {
	return a.Allocate(ctx, AllocationRequest{Kind: KindMap, MapID: mapID})
}

// Allocate posts a GameServerAllocation and turns the response into a
// ServerInfo. The GameServer name doubles as the server id: pods derive their
// --server-id from POD_NAME (see gameserver/cmd/gameserver/main.go), so the id
// the gateway mints for the join token matches the one the pod registers.
func (a *AgonesAllocator) Allocate(ctx context.Context, req AllocationRequest) (storage.ServerInfo, error) {
	kind := req.Kind
	if kind == "" {
		kind = KindMap
	}
	fleet, ok := a.fleets[kind]
	if !ok {
		return storage.ServerInfo{}, fmt.Errorf("allocate: unknown kind %q", kind)
	}

	body, err := json.Marshal(newAllocationBody(a.namespace, fleet))
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("allocate: marshal request: %w", err)
	}

	ctx, cancel := context.WithTimeout(ctx, a.timeout)
	defer cancel()

	url := a.baseURL + fmt.Sprintf(allocationPath, a.namespace)
	httpReq, err := http.NewRequestWithContext(ctx, http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("allocate: build request: %w", err)
	}
	httpReq.Header.Set("Content-Type", "application/json")
	httpReq.Header.Set("Accept", "application/json")

	resp, err := a.http.Do(httpReq)
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("allocate: call allocation api: %w", err)
	}
	defer resp.Body.Close()

	raw, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("allocate: read response: %w", err)
	}
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		return storage.ServerInfo{}, fmt.Errorf("allocate: allocation api status %d: %s",
			resp.StatusCode, apiErrorMessage(raw))
	}

	var out allocationResponse
	if err := json.Unmarshal(raw, &out); err != nil {
		return storage.ServerInfo{}, fmt.Errorf("allocate: decode response: %w", err)
	}
	if out.Status.State != stateAllocated {
		return storage.ServerInfo{}, fmt.Errorf("allocate: fleet %s: %w (state %q)",
			fleet, ErrNoCapacity, out.Status.State)
	}
	port, err := out.Status.gamePort()
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("allocate: gameserver %s: %w", out.Status.GameServerName, err)
	}
	if out.Status.Address == "" || out.Status.GameServerName == "" {
		return storage.ServerInfo{}, fmt.Errorf("allocate: incomplete allocation status (name=%q address=%q)",
			out.Status.GameServerName, out.Status.Address)
	}

	return storage.ServerInfo{
		ServerID:    out.Status.GameServerName,
		MapID:       req.MapID,
		Addr:        fmt.Sprintf("%s:%d", out.Status.Address, port),
		Transport:   a.transport,
		Capacity:    a.capacity,
		PlayerCount: 0,
	}, nil
}

// --- wire types -------------------------------------------------------------
//
// Minimal mirrors of allocation.agones.dev/v1 GameServerAllocation. Only the
// fields the gateway sends or reads are modelled; unknown fields are ignored.

const stateAllocated = "Allocated"

type allocationRequestBody struct {
	APIVersion string             `json:"apiVersion"`
	Kind       string             `json:"kind"`
	Metadata   allocationMetadata `json:"metadata"`
	Spec       allocationSpec     `json:"spec"`
}

type allocationMetadata struct {
	Namespace string `json:"namespace"`
}

type allocationSpec struct {
	Selectors  []allocationSelector `json:"selectors"`
	Scheduling string               `json:"scheduling,omitempty"`
}

type allocationSelector struct {
	MatchLabels map[string]string `json:"matchLabels"`
}

type allocationResponse struct {
	Status allocationStatus `json:"status"`
}

type allocationStatus struct {
	State          string           `json:"state"`
	GameServerName string           `json:"gameServerName"`
	Address        string           `json:"address"`
	Ports          []allocationPort `json:"ports"`
}

type allocationPort struct {
	Name string `json:"name"`
	Port int32  `json:"port"`
}

// gamePort returns the host port named "game", falling back to the single
// published port when the fleet does not name it.
func (s allocationStatus) gamePort() (int32, error) {
	for _, p := range s.Ports {
		if p.Name == gamePortName {
			return p.Port, nil
		}
	}
	if len(s.Ports) == 1 {
		return s.Ports[0].Port, nil
	}
	return 0, fmt.Errorf("no %q port in allocation status", gamePortName)
}

func newAllocationBody(namespace, fleet string) allocationRequestBody {
	return allocationRequestBody{
		APIVersion: "allocation.agones.dev/v1",
		Kind:       "GameServerAllocation",
		Metadata:   allocationMetadata{Namespace: namespace},
		Spec: allocationSpec{
			Selectors:  []allocationSelector{{MatchLabels: map[string]string{fleetLabel: fleet}}},
			Scheduling: "Packed",
		},
	}
}

// apiErrorMessage extracts .message from a Kubernetes Status body, falling back
// to the raw payload when it is not one.
func apiErrorMessage(raw []byte) string {
	var status struct {
		Message string `json:"message"`
	}
	if err := json.Unmarshal(raw, &status); err == nil && status.Message != "" {
		return status.Message
	}
	return strings.TrimSpace(string(raw))
}
