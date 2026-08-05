package server

import (
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/metrics"
	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/logger"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/testutil"
)

// startInstrumentedGateway mirrors startTestGateway but wires a private
// Prometheus registry so assertions cannot be polluted by other tests.
func startInstrumentedGateway(t *testing.T) (*Gateway, *metrics.Metrics) {
	t.Helper()

	serverRegistry := storage.NewMemoryServerRegistry()
	serverRegistry.Register(nil, storage.ServerInfo{
		ServerID:    "srv1",
		MapID:       "map_forest",
		Addr:        "10.0.0.1:9000",
		Capacity:    100,
		PlayerCount: 10,
	})

	m := metrics.New(prometheus.NewRegistry())
	gw := New(
		session.NewSessionManager(storage.NewMemorySessionStore()),
		registry.NewRegistryService(serverRegistry),
		testSecret,
		logger.New("error"),
		WithMetrics(m),
	)

	go func() {
		if err := gw.Run("127.0.0.1:0"); err != nil {
			select {
			case <-gw.done:
			default:
				t.Logf("gateway run error: %v", err)
			}
		}
	}()
	for i := 0; i < 50; i++ {
		if gw.Addr() != "" {
			return gw, m
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("gateway did not start in time")
	return nil, nil
}

func TestGatewayMetrics_AuthEnterWorldAndConnections(t *testing.T) {
	gw, m := startInstrumentedGateway(t)
	defer gw.Shutdown()

	conn := dialGateway(t, gw)
	defer conn.Close()

	waitForGauge(t, m.ConnectionsActive, 1)

	// Failed auth (bad token).
	badAuth, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: "bad-token"})
	sendEnvelope(t, conn, badAuth)
	readEnvelope(t, conn)

	// Successful auth.
	token, _ := jwt.Sign("user1", testSecret, time.Hour)
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn, authEnv)
	readEnvelope(t, conn)

	// Successful enter world, then one for a map nobody serves.
	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_forest"})
	sendEnvelope(t, conn, enterEnv)
	readEnvelope(t, conn)

	missEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_void"})
	sendEnvelope(t, conn, missEnv)
	readEnvelope(t, conn)

	checks := []struct {
		name    string
		counter prometheus.Counter
		want    float64
	}{
		{"auth ok", m.AuthTotal.WithLabelValues(metrics.ResultOK), 1},
		{"auth fail", m.AuthTotal.WithLabelValues(metrics.ResultFail), 1},
		{"enter_world ok", m.EnterWorldTotal.WithLabelValues(metrics.ResultOK), 1},
		{"enter_world fail", m.EnterWorldTotal.WithLabelValues(metrics.ResultFail), 1},
	}
	for _, c := range checks {
		if got := testutil.ToFloat64(c.counter); got != c.want {
			t.Errorf("%s = %v, want %v", c.name, got, c.want)
		}
	}

	// Closing the socket must return the gauge to zero (no leak per connection).
	conn.Close()
	waitForGauge(t, m.ConnectionsActive, 0)
}

func TestGatewayMetrics_RelayEvents(t *testing.T) {
	gw, m := startInstrumentedGateway(t)
	defer gw.Shutdown()

	gw.OnEvent(storage.Event{Type: "boss_killed"})
	gw.OnEvent(storage.Event{Type: "rare_drop"})

	if got := testutil.ToFloat64(m.RelayEventsTotal); got != 2 {
		t.Errorf("gateway_relay_events_total = %v, want 2", got)
	}
}

// waitForGauge polls a gauge: connection tracking happens on the accept and
// read goroutines, so the value is eventually — not immediately — consistent.
func waitForGauge(t *testing.T, g prometheus.Gauge, want float64) {
	t.Helper()
	for i := 0; i < 100; i++ {
		if testutil.ToFloat64(g) == want {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("gauge = %v, want %v", testutil.ToFloat64(g), want)
}
