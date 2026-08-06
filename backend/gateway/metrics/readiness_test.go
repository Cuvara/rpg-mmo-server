package metrics

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"

	"github.com/prometheus/client_golang/prometheus"
)

func probe(t *testing.T, h http.Handler, path string) (int, string) {
	t.Helper()
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, path, nil))
	body, _ := io.ReadAll(rec.Body)
	return rec.Code, string(body)
}

// TestLivenessIgnoresDependencies is the core G9 decision, pinned as a test:
// /healthz must stay 200 even when a dependency is down.
//
// If liveness tracked Redis, a Redis outage would fail liveness on every
// gateway pod at once and Kubernetes would restart all of them simultaneously —
// killing player connections that do not depend on Redis (the gateway is not in
// the gameplay data path, ADR-3) and hitting a recovering Redis with a
// reconnect storm. A restart cannot heal a sick dependency.
func TestLivenessIgnoresDependencies(t *testing.T) {
	ready := NewReadiness()
	ready.Register("redis", func(context.Context) error {
		return errors.New("dial tcp 10.0.1.7:6379: connect: connection refused")
	})
	h := HandlerWithChecks(prometheus.NewRegistry(), ready)

	code, body := probe(t, h, "/healthz")
	if code != http.StatusOK {
		t.Errorf("/healthz = %d with a dead dependency, want 200 (liveness must not track dependencies)", code)
	}
	if strings.TrimSpace(body) != "ok" {
		t.Errorf("/healthz body = %q, want %q", body, "ok")
	}
}

// TestReadinessReflectsDependencies covers the other side: /readyz must fail
// when a dependency is down, so the pod is pulled from service without being
// restarted.
func TestReadinessReflectsDependencies(t *testing.T) {
	tests := []struct {
		name     string
		checks   map[string]DependencyChecker
		wantCode int
		wantBody string
	}{
		{
			name:     "no checks is ready",
			checks:   nil,
			wantCode: http.StatusOK,
			wantBody: "ready",
		},
		{
			name: "healthy dependency is ready",
			checks: map[string]DependencyChecker{
				"redis": func(context.Context) error { return nil },
			},
			wantCode: http.StatusOK,
			wantBody: "ready",
		},
		{
			name: "failing dependency is not ready",
			checks: map[string]DependencyChecker{
				"redis": func(context.Context) error { return errors.New("connection refused") },
			},
			wantCode: http.StatusServiceUnavailable,
			wantBody: "not ready: redis",
		},
		{
			name: "failing names are sorted and comma joined",
			checks: map[string]DependencyChecker{
				"redis":    func(context.Context) error { return errors.New("down") },
				"postgres": func(context.Context) error { return errors.New("down") },
				"healthy":  func(context.Context) error { return nil },
			},
			wantCode: http.StatusServiceUnavailable,
			wantBody: "not ready: postgres,redis",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			ready := NewReadiness()
			for name, c := range tt.checks {
				ready.Register(name, c)
			}
			h := HandlerWithChecks(prometheus.NewRegistry(), ready)

			code, body := probe(t, h, "/readyz")
			if code != tt.wantCode {
				t.Errorf("/readyz = %d, want %d", code, tt.wantCode)
			}
			if strings.TrimSpace(body) != tt.wantBody {
				t.Errorf("/readyz body = %q, want %q", strings.TrimSpace(body), tt.wantBody)
			}
		})
	}
}

// TestReadinessDoesNotLeakErrorText guards the same class of bug as G7: probe
// endpoints are often exposed more widely than intended, and the underlying
// errors carry internal addresses.
func TestReadinessDoesNotLeakErrorText(t *testing.T) {
	ready := NewReadiness()
	ready.Register("redis", func(context.Context) error {
		return errors.New("dial tcp 10.0.1.7:6379: connect: connection refused")
	})
	h := HandlerWithChecks(prometheus.NewRegistry(), ready)

	_, body := probe(t, h, "/readyz")
	for _, leak := range []string{"10.0.1.7", "6379", "dial tcp", "connection refused"} {
		if strings.Contains(body, leak) {
			t.Errorf("/readyz body %q leaks %q", body, leak)
		}
	}
}

// TestReadinessRegisterAfterServe is why Readiness is a guarded type rather
// than a plain map: the metrics listener starts before the Redis client exists,
// so checks are registered while probes are already being served. Under -race
// this fails immediately if the set is not synchronised.
func TestReadinessRegisterAfterServe(t *testing.T) {
	ready := NewReadiness()
	h := HandlerWithChecks(prometheus.NewRegistry(), ready)

	var wg sync.WaitGroup
	wg.Add(2)
	go func() {
		defer wg.Done()
		for i := 0; i < 100; i++ {
			probe(t, h, "/readyz")
		}
	}()
	go func() {
		defer wg.Done()
		for i := 0; i < 100; i++ {
			ready.Register("redis", func(context.Context) error { return nil })
		}
	}()
	wg.Wait()
}

// TestNilReadinessRegisterIsNoop keeps the memory backend path (no checks
// registered at all) from needing nil guards at every call site.
func TestNilReadinessRegisterIsNoop(t *testing.T) {
	var ready *Readiness
	ready.Register("redis", func(context.Context) error { return nil }) // must not panic

	h := HandlerWithChecks(prometheus.NewRegistry(), ready)
	if code, _ := probe(t, h, "/readyz"); code != http.StatusOK {
		t.Errorf("/readyz with nil Readiness = %d, want 200", code)
	}
}

// TestNewMetricsExportsDependencyGauges pins the G9 metric surface, including
// zero-priming: a gauge that only appears after the first failure produces
// misleading graphs and unfireable alerts.
func TestNewMetricsExportsDependencyGauges(t *testing.T) {
	reg := prometheus.NewRegistry()
	m := New(reg)
	m.SetRedisUp(true)
	m.SetRelayUp(false)
	m.SessionCheckResult(SessionCheckStoreError)
	m.StreamGroupLost(2)

	h := HandlerWithChecks(reg, nil)
	_, body := probe(t, h, "/metrics")

	for _, want := range []string{
		"gateway_redis_up 1",
		"gateway_relay_up 0",
		`gateway_session_checks_total{result="store_error"} 1`,
		`gateway_session_checks_total{result="expired"} 0`,
		`gateway_session_checks_total{result="ok"} 0`,
		"gateway_stream_group_loss_total 2",
	} {
		if !strings.Contains(body, want) {
			t.Errorf("/metrics missing %q", want)
		}
	}
}

// TestNilMetricsHelpersAreSafe keeps the uninstrumented path working.
func TestNilMetricsHelpersAreSafe(t *testing.T) {
	var m *Metrics
	m.SetRedisUp(true)
	m.SetRelayUp(true)
	m.SessionCheckResult(SessionCheckOK)
	m.StreamGroupLost(1)
}
