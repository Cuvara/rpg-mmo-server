// Package metrics holds the gateway's Prometheus instrumentation: the metric
// definitions, nil-safe recording helpers, and the standalone HTTP listener
// that exposes /metrics and /healthz.
//
// The listener is deliberately separate from the realtime listener: the
// realtime port speaks the binary Envelope protocol (TCP/KCP) and must never
// serve HTTP, and in k8s the metrics port is the one Prometheus scrapes and
// probes hit.
//
// Every recording helper is nil-safe (`m *Metrics` may be nil), so a Gateway
// constructed without WithMetrics behaves exactly as before — tests and
// embedded uses do not have to wire a registry.
package metrics

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"time"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/collectors"
	"github.com/prometheus/client_golang/prometheus/promhttp"
)

// DefaultAddr is the metrics listener address used when neither
// --metrics-addr nor METRICS_ADDR is set.
//
// 9100 is Nakama's Prometheus port and 9101 is the C# game server's, so the
// gateway takes 9102 to keep a single-host dev stack collision-free.
const DefaultAddr = ":9102"

// Result label values for the *_total counters.
const (
	ResultOK   = "ok"
	ResultFail = "fail"
)

// Metrics is the gateway's metric set. Build one with New (which registers
// every collector) and hand it to server.WithMetrics / registry.WithMetrics.
type Metrics struct {
	// ConnectionsActive tracks client sockets the gateway currently holds.
	ConnectionsActive prometheus.Gauge
	// AuthTotal counts MsgAuth outcomes, labelled ok/fail.
	AuthTotal *prometheus.CounterVec
	// EnterWorldTotal counts MsgEnterWorld outcomes, labelled ok/fail.
	EnterWorldTotal *prometheus.CounterVec
	// AllocationsTotal counts allocator (Agones) requests, labelled ok/fail.
	AllocationsTotal *prometheus.CounterVec
	// RelayEventsTotal counts cross-server events delivered by the relay.
	RelayEventsTotal prometheus.Counter
	// RateLimitedTotal counts requests rejected by a rate limiter, labelled
	// with which limiter fired (see the RateLimitReason* constants).
	RateLimitedTotal *prometheus.CounterVec
}

// Reason label values for gateway_rate_limited_total.
const (
	// RateLimitReasonConnection is a TCP/KCP accept rejected by the per-IP
	// connection limiter.
	RateLimitReasonConnection = "connection"
	// RateLimitReasonMessage is an inbound frame rejected by the
	// per-connection message limiter.
	RateLimitReasonMessage = "message"
)

// New builds the metric set and registers it with reg. Passing a fresh
// prometheus.NewRegistry() keeps tests isolated; main uses NewDefault.
func New(reg prometheus.Registerer) *Metrics {
	m := &Metrics{
		ConnectionsActive: prometheus.NewGauge(prometheus.GaugeOpts{
			Name: "gateway_connections_active",
			Help: "Client connections currently held by the gateway.",
		}),
		AuthTotal: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "gateway_auth_total",
			Help: "Client authentication attempts by result.",
		}, []string{"result"}),
		EnterWorldTotal: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "gateway_enter_world_total",
			Help: "EnterWorld (map assignment) attempts by result.",
		}, []string{"result"}),
		AllocationsTotal: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "gateway_allocations_total",
			Help: "Game server allocation requests by result.",
		}, []string{"result"}),
		RelayEventsTotal: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "gateway_relay_events_total",
			Help: "Cross-server events delivered by the event relay.",
		}),
		RateLimitedTotal: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "gateway_rate_limited_total",
			Help: "Requests rejected by a rate limiter, by reason.",
		}, []string{"reason"}),
	}
	if reg != nil {
		reg.MustRegister(
			m.ConnectionsActive,
			m.AuthTotal,
			m.EnterWorldTotal,
			m.AllocationsTotal,
			m.RelayEventsTotal,
			m.RateLimitedTotal,
		)
		// Same zero-priming rationale as the result counters below: a limiter
		// that has never fired should export 0, not nothing.
		m.RateLimitedTotal.WithLabelValues(RateLimitReasonConnection)
		m.RateLimitedTotal.WithLabelValues(RateLimitReasonMessage)
		// Pre-create both label values so a freshly started gateway exports
		// `...{result="fail"} 0` instead of nothing — rate() over a series that
		// only appears on the first failure produces misleading graphs.
		for _, cv := range []*prometheus.CounterVec{m.AuthTotal, m.EnterWorldTotal, m.AllocationsTotal} {
			cv.WithLabelValues(ResultOK)
			cv.WithLabelValues(ResultFail)
		}
	}
	return m
}

// NewDefault registers the metric set (plus the Go runtime and process
// collectors) on a private registry and returns both.
func NewDefault() (*Metrics, *prometheus.Registry) {
	reg := prometheus.NewRegistry()
	reg.MustRegister(
		collectors.NewGoCollector(),
		collectors.NewProcessCollector(collectors.ProcessCollectorOpts{}),
	)
	return New(reg), reg
}

// ConnOpened records an accepted client connection.
func (m *Metrics) ConnOpened() {
	if m == nil {
		return
	}
	m.ConnectionsActive.Inc()
}

// ConnClosed records a client connection going away.
func (m *Metrics) ConnClosed() {
	if m == nil {
		return
	}
	m.ConnectionsActive.Dec()
}

// AuthResult records one authentication outcome.
func (m *Metrics) AuthResult(ok bool) {
	if m == nil {
		return
	}
	m.AuthTotal.WithLabelValues(result(ok)).Inc()
}

// EnterWorldResult records one map-assignment outcome.
func (m *Metrics) EnterWorldResult(ok bool) {
	if m == nil {
		return
	}
	m.EnterWorldTotal.WithLabelValues(result(ok)).Inc()
}

// AllocationResult records one allocator request outcome.
func (m *Metrics) AllocationResult(ok bool) {
	if m == nil {
		return
	}
	m.AllocationsTotal.WithLabelValues(result(ok)).Inc()
}

// RateLimited records one request rejected by a rate limiter. reason must be
// one of the RateLimitReason* constants — it is a metric label, so it must stay
// a small closed set and must never carry an IP or user id.
func (m *Metrics) RateLimited(reason string) {
	if m == nil {
		return
	}
	m.RateLimitedTotal.WithLabelValues(reason).Inc()
}

// RelayEvent records one event delivered by the relay.
func (m *Metrics) RelayEvent() {
	if m == nil {
		return
	}
	m.RelayEventsTotal.Inc()
}

func result(ok bool) string {
	if ok {
		return ResultOK
	}
	return ResultFail
}

// Server is a running metrics HTTP listener.
type Server struct {
	http *http.Server
	ln   net.Listener
}

// Addr returns the resolved listen address (useful when the port was :0).
func (s *Server) Addr() string {
	if s == nil || s.ln == nil {
		return ""
	}
	return s.ln.Addr().String()
}

// Shutdown stops the listener, waiting up to 5s for in-flight scrapes.
func (s *Server) Shutdown() error {
	if s == nil || s.http == nil {
		return nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := s.http.Shutdown(ctx); err != nil {
		return fmt.Errorf("metrics server shutdown: %w", err)
	}
	return nil
}

// Handler builds the metrics mux: /metrics (promhttp over g) and /healthz
// (always 200 while the process is alive — a liveness, not readiness, probe).
func Handler(g prometheus.Gatherer) http.Handler {
	mux := http.NewServeMux()
	mux.Handle("/metrics", promhttp.HandlerFor(g, promhttp.HandlerOpts{}))
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok\n"))
	})
	return mux
}

// Serve binds addr and serves the metrics handler in a background goroutine.
// An empty addr disables metrics entirely and returns (nil, nil).
func Serve(addr string, g prometheus.Gatherer, log *slog.Logger) (*Server, error) {
	if addr == "" {
		return nil, nil
	}
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return nil, fmt.Errorf("metrics listen %s: %w", addr, err)
	}
	srv := &Server{
		http: &http.Server{
			Handler:           Handler(g),
			ReadHeaderTimeout: 5 * time.Second,
		},
		ln: ln,
	}
	go func() {
		if serr := srv.http.Serve(ln); serr != nil && !errors.Is(serr, http.ErrServerClosed) && log != nil {
			log.Error("metrics server exited", "err", serr)
		}
	}()
	if log != nil {
		log.Info("metrics listening", "addr", ln.Addr().String(), "paths", "/metrics,/healthz")
	}
	return srv, nil
}
