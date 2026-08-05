package metrics

import (
	"io"
	"net/http"
	"strings"
	"testing"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/testutil"
)

func TestMetrics_RecordersUpdateCollectors(t *testing.T) {
	reg := prometheus.NewRegistry()
	m := New(reg)

	m.ConnOpened()
	m.ConnOpened()
	m.ConnClosed()
	m.AuthResult(true)
	m.AuthResult(false)
	m.AuthResult(false)
	m.EnterWorldResult(true)
	m.AllocationResult(false)
	m.RelayEvent()
	m.RelayEvent()
	m.RelayEvent()

	want := strings.NewReader(`
# HELP gateway_connections_active Client connections currently held by the gateway.
# TYPE gateway_connections_active gauge
gateway_connections_active 1
# HELP gateway_auth_total Client authentication attempts by result.
# TYPE gateway_auth_total counter
gateway_auth_total{result="fail"} 2
gateway_auth_total{result="ok"} 1
# HELP gateway_enter_world_total EnterWorld (map assignment) attempts by result.
# TYPE gateway_enter_world_total counter
gateway_enter_world_total{result="fail"} 0
gateway_enter_world_total{result="ok"} 1
# HELP gateway_allocations_total Game server allocation requests by result.
# TYPE gateway_allocations_total counter
gateway_allocations_total{result="fail"} 1
gateway_allocations_total{result="ok"} 0
# HELP gateway_relay_events_total Cross-server events delivered by the event relay.
# TYPE gateway_relay_events_total counter
gateway_relay_events_total 3
`)
	if err := testutil.GatherAndCompare(reg, want,
		"gateway_connections_active",
		"gateway_auth_total",
		"gateway_enter_world_total",
		"gateway_allocations_total",
		"gateway_relay_events_total",
	); err != nil {
		t.Fatal(err)
	}
}

// A nil *Metrics is the "uninstrumented gateway" case — every recorder must be
// a no-op rather than a panic.
func TestMetrics_NilIsNoOp(t *testing.T) {
	var m *Metrics
	m.ConnOpened()
	m.ConnClosed()
	m.AuthResult(true)
	m.EnterWorldResult(false)
	m.AllocationResult(true)
	m.RelayEvent()
}

// New must pre-create both result label values so rate() has a zero baseline.
func TestMetrics_LabelsPrecreated(t *testing.T) {
	reg := prometheus.NewRegistry()
	m := New(reg)

	for name, cv := range map[string]*prometheus.CounterVec{
		"gateway_auth_total":        m.AuthTotal,
		"gateway_enter_world_total": m.EnterWorldTotal,
		"gateway_allocations_total": m.AllocationsTotal,
	} {
		if got := testutil.CollectAndCount(cv); got != 2 {
			t.Errorf("%s: %d series, want 2 (ok+fail)", name, got)
		}
	}
}

func TestServe_MetricsAndHealthz(t *testing.T) {
	reg := prometheus.NewRegistry()
	m := New(reg)
	m.AuthResult(true)

	srv, err := Serve("127.0.0.1:0", reg, nil)
	if err != nil {
		t.Fatalf("serve: %v", err)
	}
	t.Cleanup(func() { _ = srv.Shutdown() })

	base := "http://" + srv.Addr()

	body, status := get(t, base+"/healthz")
	if status != http.StatusOK {
		t.Errorf("/healthz status = %d, want 200", status)
	}
	if strings.TrimSpace(body) != "ok" {
		t.Errorf("/healthz body = %q, want %q", body, "ok")
	}

	body, status = get(t, base+"/metrics")
	if status != http.StatusOK {
		t.Errorf("/metrics status = %d, want 200", status)
	}
	if !strings.Contains(body, `gateway_auth_total{result="ok"} 1`) {
		t.Errorf("/metrics body missing gateway_auth_total:\n%s", body)
	}
}

// An empty address means "metrics disabled": no listener, and Shutdown on the
// nil server must still be safe.
func TestServe_EmptyAddrDisables(t *testing.T) {
	srv, err := Serve("", prometheus.NewRegistry(), nil)
	if err != nil {
		t.Fatalf("serve: %v", err)
	}
	if srv != nil {
		t.Fatalf("srv = %v, want nil", srv)
	}
	if srv.Addr() != "" {
		t.Errorf("Addr() = %q, want empty", srv.Addr())
	}
	if err := srv.Shutdown(); err != nil {
		t.Errorf("Shutdown() = %v, want nil", err)
	}
}

func get(t *testing.T, url string) (string, int) {
	t.Helper()
	resp, err := http.Get(url)
	if err != nil {
		t.Fatalf("get %s: %v", url, err)
	}
	defer resp.Body.Close()
	b, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("read %s: %v", url, err)
	}
	return string(b), resp.StatusCode
}
