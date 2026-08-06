package server

import (
	"errors"
	"net"
	"strings"
	"syscall"
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
	dto "github.com/prometheus/client_model/go"
)

// startGatewayWithOptions starts a gateway on a random port with extra options
// and returns it plus its metric set. The auth secret is testSecret.
func startGatewayWithOptions(t *testing.T, opts ...Option) (*Gateway, *metrics.Metrics) {
	t.Helper()
	return startGatewayFull(t, testSecret, opts...)
}

// startGatewayWithSecretAndOptions is startGatewayWithOptions with an explicit
// auth secret (which may be a comma-separated rotation list).
func startGatewayWithSecretAndOptions(t *testing.T, secret string, opts ...Option) *Gateway {
	t.Helper()
	gw, _ := startGatewayFull(t, secret, opts...)
	return gw
}

func startGatewayFull(t *testing.T, secret string, opts ...Option) (*Gateway, *metrics.Metrics) {
	t.Helper()

	sessionStore := storage.NewMemorySessionStore()
	serverRegistry := storage.NewMemoryServerRegistry()
	serverRegistry.Register(nil, storage.ServerInfo{
		ServerID: "srv1", MapID: "map_forest", Addr: "10.0.0.1:9000",
		Capacity: 100, PlayerCount: 10,
	})

	met := metrics.New(prometheus.NewRegistry())
	sessions := session.NewSessionManager(sessionStore)
	reg := registry.NewRegistryService(serverRegistry)

	gw := New(sessions, reg, secret, logger.New("error"),
		append([]Option{WithMetrics(met)}, opts...)...)
	t.Cleanup(gw.Shutdown)

	go func() { _ = gw.Run("127.0.0.1:0") }()
	for i := 0; i < 100; i++ {
		if gw.Addr() != "" {
			return gw, met
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("gateway did not start in time")
	return nil, nil
}

// counterValue reads one labelled value out of a CounterVec.
func counterValue(t *testing.T, cv *prometheus.CounterVec, label string) float64 {
	t.Helper()
	m := &dto.Metric{}
	c, err := cv.GetMetricWithLabelValues(label)
	if err != nil {
		t.Fatalf("GetMetricWithLabelValues(%q): %v", label, err)
	}
	if err := c.(prometheus.Metric).Write(m); err != nil {
		t.Fatalf("write metric: %v", err)
	}
	return m.GetCounter().GetValue()
}

// TestConnRateLimit checks that the per-IP accept limiter admits the burst and
// then closes further connections, and that the rejection is counted.
func TestConnRateLimit(t *testing.T) {
	// Burst 2, and a rate slow enough that no token refills during the test.
	gw, met := startGatewayWithOptions(t, WithConnRateLimit(0.01, 2))

	var kept []net.Conn
	t.Cleanup(func() {
		for _, c := range kept {
			c.Close()
		}
	})

	// The first two connections survive.
	for i := 0; i < 2; i++ {
		conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
		if err != nil {
			t.Fatalf("connection %d should be admitted: %v", i, err)
		}
		kept = append(kept, conn)
	}

	// The third is accepted by the kernel but closed immediately by the
	// gateway, so the first read returns EOF instead of blocking.
	conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()
	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 1)
	if _, rerr := conn.Read(buf); rerr == nil {
		t.Fatal("over-limit connection should have been closed by the gateway")
	}

	if got := counterValue(t, met.RateLimitedTotal, metrics.RateLimitReasonConnection); got < 1 {
		t.Errorf("gateway_rate_limited_total{reason=connection} = %v, want >= 1", got)
	}
}

// TestConnRateLimitDisabled proves the limiter is genuinely opt-out: with no
// WithConnRateLimit option a burst of connections all survive.
func TestConnRateLimitDisabled(t *testing.T) {
	gw, met := startGatewayWithOptions(t)

	for i := 0; i < 20; i++ {
		conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
		if err != nil {
			t.Fatalf("connection %d rejected with limiting disabled: %v", i, err)
		}
		defer conn.Close()
	}
	if got := counterValue(t, met.RateLimitedTotal, metrics.RateLimitReasonConnection); got != 0 {
		t.Errorf("gateway_rate_limited_total{reason=connection} = %v, want 0", got)
	}
}

// TestMsgRateLimit floods one connection and expects a "rate limited" error
// frame followed by a close.
func TestMsgRateLimit(t *testing.T) {
	// Burst 3 frames, then effectively no refill for the duration of the test.
	gw, met := startGatewayWithOptions(t, WithMsgRateLimit(0.01, 3))

	conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	token, err := jwt.Sign("user-flood", testSecret, time.Hour)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	authEnv, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	if err != nil {
		t.Fatalf("envelope: %v", err)
	}
	frame, err := messages.Encode(authEnv)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}

	// Send well past the burst.
	for i := 0; i < 10; i++ {
		if _, werr := conn.Write(frame); werr != nil {
			break // gateway already closed the socket, which is the point
		}
	}

	// Somewhere in the replies there must be a "rate limited" error, and the
	// stream must then end with an orderly EOF.
	_ = conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	sawLimit := false
	var finalErr error
	for {
		env, derr := messages.Decode(conn)
		if derr != nil {
			finalErr = derr
			break // EOF / closed — expected terminal state
		}
		var resp messages.AuthResponse
		if uerr := messages.UnmarshalPayload(env.Payload, &resp); uerr != nil {
			continue
		}
		if !resp.OK && strings.Contains(resp.Error, "rate limited") {
			sawLimit = true
		}
	}

	if !sawLimit {
		t.Error("client should receive an explicit \"rate limited\" error frame before the close")
	}

	// The stream must end with a clean EOF, never a reset.
	//
	// This is the deterministic half of the assertion above. A hard Close() on
	// a socket whose receive queue still holds the flood makes the kernel send
	// RST instead of FIN, and RST discards the unsent send buffer — so the
	// "rate limited" frame is silently dropped and the client learns nothing.
	// The `sawLimit` check catches that only when the timing happens to be
	// unlucky (it flaked exactly this way under a loaded `go test ./...`);
	// this check catches the underlying condition every run, on any machine.
	if finalErr != nil && errors.Is(finalErr, syscall.ECONNRESET) {
		t.Errorf("connection ended with RST (%v) — the gateway must half-close so the "+
			"error frame survives; see ClientConn.CloseGracefully", finalErr)
	}
	if got := counterValue(t, met.RateLimitedTotal, metrics.RateLimitReasonMessage); got < 1 {
		t.Errorf("gateway_rate_limited_total{reason=message} = %v, want >= 1", got)
	}
}

// TestMsgRateLimitAllowsNormalHandshake is the false-positive guard: the real
// three-frame protocol must never trip the default-shaped limiter.
func TestMsgRateLimitAllowsNormalHandshake(t *testing.T) {
	gw, met := startGatewayWithOptions(t, WithMsgRateLimit(60, 120))

	conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	token, err := jwt.Sign("user-normal", testSecret, time.Hour)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	send(t, conn, messages.MsgAuth, messages.AuthRequest{Token: token})
	if resp := readAuthResp(t, conn); !resp.OK {
		t.Fatalf("auth failed: %s", resp.Error)
	}
	send(t, conn, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_forest"})

	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	env, err := messages.Decode(conn)
	if err != nil {
		t.Fatalf("decode enter world resp: %v", err)
	}
	var ew messages.EnterWorldResponse
	if err := messages.UnmarshalPayload(env.Payload, &ew); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if ew.Error != "" {
		t.Fatalf("enter world failed: %s", ew.Error)
	}
	if got := counterValue(t, met.RateLimitedTotal, metrics.RateLimitReasonMessage); got != 0 {
		t.Errorf("a normal handshake must not be rate limited, got %v", got)
	}
}

// send encodes and writes one envelope.
func send(t *testing.T, conn net.Conn, mt messages.MsgType, payload any) {
	t.Helper()
	env, err := messages.NewEnvelope(mt, payload)
	if err != nil {
		t.Fatalf("envelope: %v", err)
	}
	data, err := messages.Encode(env)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	if _, err := conn.Write(data); err != nil {
		t.Fatalf("write: %v", err)
	}
}

// readAuthResp reads one MsgAuthResp.
func readAuthResp(t *testing.T, conn net.Conn) messages.AuthResponse {
	t.Helper()
	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	env, err := messages.Decode(conn)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}
	var resp messages.AuthResponse
	if err := messages.UnmarshalPayload(env.Payload, &resp); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	return resp
}
