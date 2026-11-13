package metrics

import (
	"io"
	"net/http"
	"strings"
	"testing"

	"github.com/duycuong/rpg-mmo/shared/constants"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/testutil"

	"github.com/duycuong/rpg-mmo/shared/transport"
)

func TestMetrics_RecordersUpdateCollectors(t *testing.T) {
	reg := prometheus.NewRegistry()
	m := New(reg)

	m.ConnOpened()
	m.ConnOpened()
	m.ConnClosed()
	m.AuthResult(constants.DefaultGameID, true)
	m.AuthResult(constants.DefaultGameID, false)
	m.AuthResult(constants.DefaultGameID, false)
	m.EnterWorldResult(constants.DefaultGameID, true)
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
gateway_auth_total{game="` + constants.DefaultGameID + `",result="fail"} 2
gateway_auth_total{game="` + constants.DefaultGameID + `",result="ok"} 1
# HELP gateway_enter_world_total EnterWorld (map assignment) attempts by result.
# TYPE gateway_enter_world_total counter
gateway_enter_world_total{game="` + constants.DefaultGameID + `",result="fail"} 0
gateway_enter_world_total{game="` + constants.DefaultGameID + `",result="ok"} 1
# HELP gateway_allocations_total Game server allocation requests by result.
# TYPE gateway_allocations_total counter
gateway_allocations_total{game="` + constants.DefaultGameID + `",result="fail"} 1
gateway_allocations_total{game="` + constants.DefaultGameID + `",result="ok"} 0
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
	m.AuthResult(constants.DefaultGameID, true)
	m.EnterWorldResult(constants.DefaultGameID, false)
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
	m.AuthResult(constants.DefaultGameID, true)

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
	if !strings.Contains(body, `gateway_auth_total{game="`+constants.DefaultGameID+`",result="ok"} 1`) {
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

// TestSetTransportPostureReportsTcpKeyAsUnencrypted pins the regression this
// replaced. The gateway used to log `encrypted` as `transportKey != ""`, so a
// gateway configured with transport=tcp and a key set reported encrypted=true
// while putting every auth frame and join token on the wire in cleartext — TCP
// has no packet-crypt layer and the key is ignored. A security signal that is
// confidently wrong is worse than one that is missing, because nobody looks
// behind it.
func TestSetTransportPostureReportsTcpKeyAsUnencrypted(t *testing.T) {
	reg := prometheus.NewRegistry()
	m := New(reg)

	m.SetTransportPosture(transport.Posture(transport.KindTCP,
		"00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", ":8000"))

	if got := testutil.ToFloat64(m.TransportEncrypted.WithLabelValues("tcp", transport.CipherNone)); got != 0 {
		t.Errorf("gateway_transport_encrypted = %v for tcp with a key set, want 0", got)
	}
	if got := testutil.ToFloat64(m.TransportAuthenticated.WithLabelValues("tcp", transport.CipherNone)); got != 0 {
		t.Errorf("gateway_transport_authenticated = %v, want 0", got)
	}
}

// The one configuration that does encrypt still reports so — and still reports
// that it does not authenticate.
func TestSetTransportPostureReportsKcpKeyAsEncryptedNotAuthenticated(t *testing.T) {
	reg := prometheus.NewRegistry()
	m := New(reg)

	m.SetTransportPosture(transport.Posture(transport.KindKCP,
		"00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", ":8000"))

	if got := testutil.ToFloat64(m.TransportEncrypted.WithLabelValues("kcp", transport.CipherAESCFB)); got != 1 {
		t.Errorf("gateway_transport_encrypted = %v for kcp with a key, want 1", got)
	}
	if got := testutil.ToFloat64(m.TransportAuthenticated.WithLabelValues("kcp", transport.CipherAESCFB)); got != 0 {
		t.Errorf("gateway_transport_authenticated = %v, want 0 — AES-CFB with a CRC32 is not a MAC", got)
	}
}

// A gauge is present when it reads 0; that is why these are gauges and not
// counters. Asserted, because "the metric is missing" is the answer this whole
// change exists to prevent.
func TestTransportPostureGaugesArePresentWhenZero(t *testing.T) {
	reg := prometheus.NewRegistry()
	m := New(reg)
	m.SetTransportPosture(transport.Posture(transport.KindTCP, "", ":8000"))

	families, err := reg.Gather()
	if err != nil {
		t.Fatalf("Gather: %v", err)
	}
	seen := map[string]bool{}
	for _, f := range families {
		seen[f.GetName()] = true
	}
	for _, name := range []string{"gateway_transport_encrypted", "gateway_transport_authenticated"} {
		if !seen[name] {
			t.Errorf("%s absent from /metrics while reporting 0 — a security question must never be answered by a missing field", name)
		}
	}
}

func TestGameLabelsAndPriming(t *testing.T) {
	for _, tt := range []struct {
		name      string
		ids       []string
		wantGames []string
	}{
		{"default and foo", []string{constants.DefaultGameID, "foo"}, []string{constants.DefaultGameID, "foo"}},
		{"foo only", []string{"foo"}, []string{"foo"}},
	} {
		t.Run(tt.name, func(t *testing.T) {
			reg := prometheus.NewRegistry()
			m := New(reg)
			m.SetGames(tt.ids)
			for _, game := range []string{constants.DefaultGameID, "foo", "unknown", ""} {
				want := GameOther
				for _, configured := range tt.wantGames {
					if game == configured {
						want = game
					}
				}
				if got := m.GameLabel(game); got != want {
					t.Errorf("GameLabel(%q) = %q, want %q", game, got, want)
				}
			}
			families, err := reg.Gather()
			if err != nil {
				t.Fatal(err)
			}
			counts := map[string]int{"gateway_auth_total": 2, "gateway_enter_world_total": 2, "gateway_allocations_total": 2, "gateway_session_checks_total": 3}
			for _, family := range families {
				perGame, ok := counts[family.GetName()]
				if !ok {
					continue
				}
				if got, want := len(family.Metric), perGame*len(tt.wantGames); got != want {
					t.Errorf("%s has %d series, want %d", family.GetName(), got, want)
				}
				for _, metric := range family.Metric {
					game := ""
					for _, label := range metric.Label {
						if label.GetName() == "game" {
							game = label.GetValue()
						}
					}
					found := false
					for _, want := range tt.wantGames {
						if game == want {
							found = true
						}
					}
					if !found {
						t.Errorf("%s has unexpected game %q", family.GetName(), game)
					}
				}
				delete(counts, family.GetName())
			}
			if len(counts) != 0 {
				t.Errorf("missing families: %v", counts)
			}
		})
	}
}

func TestUnconfiguredGameRecordsAsOther(t *testing.T) {
	m := New(prometheus.NewRegistry())
	m.AuthResult("unknown", true)
	m.EnterWorldResult("", false)
	m.AllocationResultGame("unknown", true)
	m.SessionCheckResult("unknown", SessionCheckExpired)
	for name, counter := range map[string]prometheus.Counter{
		"auth":       m.AuthTotal.WithLabelValues(GameOther, ResultOK),
		"enter":      m.EnterWorldTotal.WithLabelValues(GameOther, ResultFail),
		"allocation": m.AllocationsTotal.WithLabelValues(GameOther, ResultOK),
		"session":    m.SessionChecksTotal.WithLabelValues(GameOther, SessionCheckExpired),
	} {
		if got := testutil.ToFloat64(counter); got != 1 {
			t.Errorf("%s = %v, want 1", name, got)
		}
	}
}

func TestNewGameMethodsNilSafe(t *testing.T) {
	var m *Metrics
	m.SetGames([]string{"foo"})
	if got := m.GameLabel("foo"); got != GameOther {
		t.Errorf("nil GameLabel = %q", got)
	}
	m.AllocationResultGame("foo", true)
}

func TestUnchangedFamilyLabels(t *testing.T) {
	m, reg := NewDefault()
	m.SetTransportPosture(transport.Posture(transport.KindTCP, "", ":8000"))
	families, err := reg.Gather()
	if err != nil {
		t.Fatal(err)
	}
	want := map[string]string{
		"gateway_connections_active": "", "gateway_relay_events_total": "",
		"gateway_rate_limited_total": "reason", "gateway_kick_publish_total": "result",
		"gateway_transport_encrypted": "cipher,transport", "gateway_transport_authenticated": "cipher,transport",
		"gateway_redis_up": "", "gateway_relay_up": "", "gateway_stream_group_loss_total": "",
		"gateway_unversioned_handshakes_total": "", "gateway_protocol_version_refused_total": "",
		"go_goroutines": "", "process_cpu_seconds_total": "",
	}
	for _, family := range families {
		expected, ok := want[family.GetName()]
		if !ok {
			continue
		}
		for _, metric := range family.Metric {
			var names []string
			for _, label := range metric.Label {
				names = append(names, label.GetName())
			}
			if got := strings.Join(names, ","); got != expected {
				t.Errorf("%s labels = %q, want %q", family.GetName(), got, expected)
			}
		}
		delete(want, family.GetName())
	}
	if len(want) != 0 {
		t.Errorf("missing families: %v", want)
	}
}
