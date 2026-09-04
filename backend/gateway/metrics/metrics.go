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
	"sort"
	"strings"
	"sync"
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

	// RedisUp is 1 when the last dependency probe reached Redis, 0 otherwise.
	// A gauge rather than a counter because alerting wants "is it down right
	// now", and because it is the series that explains a spike in
	// SessionChecksTotal{result="store_error"}.
	RedisUp prometheus.Gauge

	// RelayUp is 1 once the event relay is subscribed. It stays 0 while the
	// gateway runs degraded after a failed relay start.
	RelayUp prometheus.Gauge

	// SessionChecksTotal splits session validation by outcome so a Redis blip
	// (store_error) is distinguishable from real expiry on a dashboard —
	// without it both looked like "session expired" and a store outage was
	// invisible until players complained.
	SessionChecksTotal *prometheus.CounterVec

	// StreamGroupLossTotal counts consumer-group disappearances the relay had
	// to recover from (NOGROUP after a Redis wipe/restore).
	StreamGroupLossTotal prometheus.Counter

	// KickPublishTotal counts session-supersede events published to the
	// events:kick stream on duplicate login, labelled ok/fail. A fail is a
	// duplicate login whose OLD game-server connection will NOT be kicked
	// (the new login proceeds regardless), so it is worth alerting on.
	KickPublishTotal *prometheus.CounterVec
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

// Result label values for gateway_session_checks_total.
const (
	// SessionCheckOK is a session that validated normally.
	SessionCheckOK = "ok"
	// SessionCheckExpired is a session the store confirmed is gone.
	SessionCheckExpired = "expired"
	// SessionCheckStoreError is a session that could not be validated because
	// the store itself failed. Distinct from expired on purpose: this one means
	// the infrastructure is sick, not that the player's session ended.
	SessionCheckStoreError = "store_error"
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
		RedisUp: prometheus.NewGauge(prometheus.GaugeOpts{
			Name: "gateway_redis_up",
			Help: "1 when the last Redis dependency probe succeeded, 0 otherwise.",
		}),
		RelayUp: prometheus.NewGauge(prometheus.GaugeOpts{
			Name: "gateway_relay_up",
			Help: "1 when the event relay is subscribed, 0 while running degraded.",
		}),
		SessionChecksTotal: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "gateway_session_checks_total",
			Help: "Session validations by outcome (ok, expired, store_error).",
		}, []string{"result"}),
		StreamGroupLossTotal: prometheus.NewCounter(prometheus.CounterOpts{
			Name: "gateway_stream_group_loss_total",
			Help: "Event-stream consumer groups found missing and re-created.",
		}),
		KickPublishTotal: prometheus.NewCounterVec(prometheus.CounterOpts{
			Name: "gateway_kick_publish_total",
			Help: "Duplicate-login supersede events published to events:kick, by result.",
		}, []string{"result"}),
	}
	if reg != nil {
		reg.MustRegister(
			m.ConnectionsActive,
			m.AuthTotal,
			m.EnterWorldTotal,
			m.AllocationsTotal,
			m.RelayEventsTotal,
			m.RateLimitedTotal,
			m.RedisUp,
			m.RelayUp,
			m.SessionChecksTotal,
			m.StreamGroupLossTotal,
			m.KickPublishTotal,
		)
		for _, v := range []string{SessionCheckOK, SessionCheckExpired, SessionCheckStoreError} {
			m.SessionChecksTotal.WithLabelValues(v)
		}
		// Same zero-priming rationale as the result counters below: a limiter
		// that has never fired should export 0, not nothing.
		m.RateLimitedTotal.WithLabelValues(RateLimitReasonConnection)
		m.RateLimitedTotal.WithLabelValues(RateLimitReasonMessage)
		// Pre-create both label values so a freshly started gateway exports
		// `...{result="fail"} 0` instead of nothing — rate() over a series that
		// only appears on the first failure produces misleading graphs.
		for _, cv := range []*prometheus.CounterVec{m.AuthTotal, m.EnterWorldTotal, m.AllocationsTotal, m.KickPublishTotal} {
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

// KickPublishResult records one duplicate-login supersede publish outcome.
func (m *Metrics) KickPublishResult(ok bool) {
	if m == nil {
		return
	}
	m.KickPublishTotal.WithLabelValues(result(ok)).Inc()
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

// SessionCheckResult records one session validation outcome. result must be one
// of the SessionCheck* constants.
func (m *Metrics) SessionCheckResult(result string) {
	if m == nil {
		return
	}
	m.SessionChecksTotal.WithLabelValues(result).Inc()
}

// SetRedisUp records the outcome of a Redis dependency probe.
func (m *Metrics) SetRedisUp(up bool) {
	if m == nil {
		return
	}
	m.RedisUp.Set(boolGauge(up))
}

// SetRelayUp records whether the event relay is subscribed.
func (m *Metrics) SetRelayUp(up bool) {
	if m == nil {
		return
	}
	m.RelayUp.Set(boolGauge(up))
}

// StreamGroupLost records n consumer-group recoveries. n comes from a sampled
// monotonic counter, so callers pass the delta since the last sample.
func (m *Metrics) StreamGroupLost(n int64) {
	if m == nil || n <= 0 {
		return
	}
	m.StreamGroupLossTotal.Add(float64(n))
}

func boolGauge(b bool) float64 {
	if b {
		return 1
	}
	return 0
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

// DependencyChecker reports the health of an external dependency. It must
// return promptly — it runs inside a probe handler.
type DependencyChecker func(context.Context) error

// Readiness is a mutable set of dependency checks backing /readyz.
//
// It exists because the metrics listener is deliberately started before the
// storage backend is wired (so a crash-looping gateway is still scrapeable and
// probeable), which means the Redis client does not exist yet at that point.
// Registering checks afterwards from main while probe requests are already
// being served is a data race, so the set guards itself.
type Readiness struct {
	mu     sync.RWMutex
	checks map[string]DependencyChecker
}

// NewReadiness returns an empty check set. A set with no checks is always
// ready, which is the correct answer for the memory backend: it has no external
// dependency to be unready about.
func NewReadiness() *Readiness {
	return &Readiness{checks: make(map[string]DependencyChecker)}
}

// Register adds (or replaces) a named check. A nil Readiness is a no-op so
// callers never have to nil-check.
func (r *Readiness) Register(name string, check DependencyChecker) {
	if r == nil || check == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.checks[name] = check
}

// snapshot copies the checks so probes never hold the lock while running them —
// a slow dependency must not block Register or another probe.
func (r *Readiness) snapshot() map[string]DependencyChecker {
	if r == nil {
		return nil
	}
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make(map[string]DependencyChecker, len(r.checks))
	for k, v := range r.checks {
		out[k] = v
	}
	return out
}

// Handler builds the metrics mux with no dependency checks: /readyz then
// degrades to a pure process check.
func Handler(g prometheus.Gatherer) http.Handler {
	return HandlerWithChecks(g, nil)
}

// HandlerWithChecks builds the metrics mux:
//
//   - /metrics  promhttp over g.
//   - /healthz  liveness. Always 200 while the process is alive, *even when a
//     dependency is down*.
//   - /readyz   readiness. 200 only when every check passes; 503 with the
//     failing check names otherwise.
//
// Why the split matters: Kubernetes restarts a container that fails liveness
// but only removes it from service on a readiness failure. Wiring Redis into
// /healthz would mean a Redis outage triggers a rolling restart of every
// gateway pod simultaneously — killing the connections of players whose
// gameplay does not touch Redis at all (the gateway is not in the gameplay data
// path, ADR-3), and hammering Redis with reconnect storms exactly when it is
// least able to cope. A restart cannot fix a sick dependency, so liveness must
// not depend on one. Readiness is the correct signal: stop sending *new* logins
// to a gateway that cannot reach Redis, keep the process and its existing
// connections alive.
func HandlerWithChecks(g prometheus.Gatherer, ready *Readiness) http.Handler {
	mux := http.NewServeMux()
	mux.Handle("/metrics", promhttp.HandlerFor(g, promhttp.HandlerOpts{}))
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok\n"))
	})
	mux.HandleFunc("/readyz", func(w http.ResponseWriter, r *http.Request) {
		ctx, cancel := context.WithTimeout(r.Context(), readyzTimeout)
		defer cancel()

		var failed []string
		for name, check := range ready.snapshot() {
			if err := check(ctx); err != nil {
				failed = append(failed, name)
			}
		}
		sort.Strings(failed)

		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		if len(failed) > 0 {
			w.WriteHeader(http.StatusServiceUnavailable)
			// Names only, never the error text: this endpoint is often exposed
			// more widely than intended and the errors carry internal addresses.
			_, _ = fmt.Fprintf(w, "not ready: %s\n", strings.Join(failed, ","))
			return
		}
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ready\n"))
	})
	return mux
}

// readyzTimeout bounds the whole readiness probe so a hung dependency cannot
// pile up probe handlers.
const readyzTimeout = 2 * time.Second

// Serve binds addr and serves the metrics handler in a background goroutine.
// An empty addr disables metrics entirely and returns (nil, nil).
func Serve(addr string, g prometheus.Gatherer, log *slog.Logger) (*Server, error) {
	return ServeWithChecks(addr, g, nil, log)
}

// ServeWithChecks is Serve with a readiness set wired into /readyz. Checks may
// be registered on ready after this returns.
func ServeWithChecks(addr string, g prometheus.Gatherer, ready *Readiness, log *slog.Logger) (*Server, error) {
	if addr == "" {
		return nil, nil
	}
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return nil, fmt.Errorf("metrics listen %s: %w", addr, err)
	}
	srv := &Server{
		http: &http.Server{
			Handler:           HandlerWithChecks(g, ready),
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
		log.Info("metrics listening", "addr", ln.Addr().String(), "paths", "/metrics,/healthz,/readyz")
	}
	return srv, nil
}
