package server

import (
	"net"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/prometheus/client_golang/prometheus"
	dto "github.com/prometheus/client_model/go"
)

// The promise of the version handshake is that a mismatched client is refused
// with a NAMED REASON rather than a parse error, a silent close, or — worst — a
// successful connection that is confidently wrong. These tests exercise that
// promise over a real socket, because the failure being prevented is a wire
// failure and a unit test of the decision function cannot see it.

// authExchange sends one MsgAuth carrying protocolVersion and returns the
// gateway's AuthResponse.
func authExchange(t *testing.T, conn net.Conn, token string, protocolVersion uint32) messages.AuthResponse {
	t.Helper()

	env, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{
		Token:           token,
		ProtocolVersion: protocolVersion,
	})
	if err != nil {
		t.Fatalf("NewEnvelope: %v", err)
	}
	sendEnvelope(t, conn, env)

	resp := readEnvelope(t, conn)
	if resp.Type != messages.MsgAuthResp {
		t.Fatalf("expected MsgAuthResp, got %d", resp.Type)
	}
	var authResp messages.AuthResponse
	if err := resp.UnmarshalPayload(&authResp); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	return authResp
}

// A client speaking this build's version connects normally.
func TestMatchingProtocolVersionIsAdmitted(t *testing.T) {
	gw, met := startGatewayWithOptions(t)

	conn := dialGateway(t, gw)
	defer conn.Close()

	token, _ := jwt.Sign("user1", testSecret, 1*time.Hour)
	resp := authExchange(t, conn, token, messages.WireProtocolVersion)

	if !resp.OK {
		t.Fatalf("auth should succeed for a matching version, got error %q", resp.Error)
	}
	if resp.ProtocolVersion != messages.WireProtocolVersion {
		t.Errorf("gateway must echo its version, got %d want %d",
			resp.ProtocolVersion, messages.WireProtocolVersion)
	}
	if got := counterValue2(t, met.UnversionedHandshakesTotal); got != 0 {
		t.Errorf("a versioned client must not count as unversioned, got %v", got)
	}
}

// The central case: a mismatched client is refused with the named reason, and
// the refusal is a legible AuthResponse rather than a dropped connection.
func TestMismatchedProtocolVersionIsRefusedWithANamedReason(t *testing.T) {
	gw, met := startGatewayWithOptions(t)

	conn := dialGateway(t, gw)
	defer conn.Close()

	// A VALID token deliberately: the refusal must be about the schema, not the
	// credential. If this returned "invalid token" the operator would debug the
	// wrong layer, which is the whole failure mode being closed.
	token, _ := jwt.Sign("user1", testSecret, 1*time.Hour)
	resp := authExchange(t, conn, token, messages.WireProtocolVersion+1)

	if resp.OK {
		t.Fatal("a client speaking a different protocol version must not be admitted")
	}
	if resp.Error != messages.ReasonProtocolVersionMismatch {
		t.Fatalf("reason = %q, want %q — clients branch on this exact string",
			resp.Error, messages.ReasonProtocolVersionMismatch)
	}
	if resp.ProtocolVersion != messages.WireProtocolVersion {
		t.Errorf("a refusal must say which version the gateway speaks, got %d want %d",
			resp.ProtocolVersion, messages.WireProtocolVersion)
	}
	if got := counterValue2(t, met.ProtocolVersionRefusedTotal); got != 1 {
		t.Errorf("gateway_protocol_version_refused_total = %v, want 1", got)
	}

	// The socket is closed after the refusal: nothing the client can do on this
	// connection changes the answer, so leaving it open would only invite a
	// retry loop into the same wall.
	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 1)
	if _, err := conn.Read(buf); err == nil {
		t.Error("the gateway should close the connection after a version refusal")
	}
}

// An OLD client sends no version field at all. Under the shipping default it is
// admitted, and — critically — counted, because that counter is the only thing
// that will ever say the fleet is ready to have the default flipped.
func TestUnversionedClientIsAdmittedAndCounted(t *testing.T) {
	gw, met := startGatewayWithOptions(t)

	conn := dialGateway(t, gw)
	defer conn.Close()

	token, _ := jwt.Sign("user1", testSecret, 1*time.Hour)
	resp := authExchange(t, conn, token, messages.ProtocolVersionUnversioned)

	if !resp.OK {
		t.Fatalf("an unversioned client must be admitted by default, got %q", resp.Error)
	}
	if got := counterValue2(t, met.UnversionedHandshakesTotal); got != 1 {
		t.Fatalf("gateway_unversioned_handshakes_total = %v, want 1 — "+
			"an admission nobody can see is the silent fallback the rule forbids", got)
	}
	if got := counterValue2(t, met.ProtocolVersionRefusedTotal); got != 0 {
		t.Errorf("an admitted client must not also count as refused, got %v", got)
	}
}

// Flipping the minimum to 1 is the migration step. The same unversioned client
// is then refused through exactly the same named path as a mismatched one.
func TestUnversionedClientIsRefusedOnceAdvertisementIsRequired(t *testing.T) {
	gw, met := startGatewayWithOptions(t, WithMinProtocolVersion(1))

	conn := dialGateway(t, gw)
	defer conn.Close()

	token, _ := jwt.Sign("user1", testSecret, 1*time.Hour)
	resp := authExchange(t, conn, token, messages.ProtocolVersionUnversioned)

	if resp.OK {
		t.Fatal("min-protocol-version=1 must refuse a client that advertises nothing")
	}
	if resp.Error != messages.ReasonProtocolVersionMismatch {
		t.Fatalf("reason = %q, want %q — the same named path as a mismatch, not a second convention",
			resp.Error, messages.ReasonProtocolVersionMismatch)
	}
	if got := counterValue2(t, met.ProtocolVersionRefusedTotal); got != 1 {
		t.Errorf("gateway_protocol_version_refused_total = %v, want 1", got)
	}
	if got := counterValue2(t, met.UnversionedHandshakesTotal); got != 0 {
		t.Errorf("a refused client is not an admitted one, got %v", got)
	}
}

// A matching client is still admitted when advertisement is required — the flip
// must not become a blanket refusal.
func TestMatchingVersionSurvivesTheRequiredFlip(t *testing.T) {
	gw, _ := startGatewayWithOptions(t, WithMinProtocolVersion(1))

	conn := dialGateway(t, gw)
	defer conn.Close()

	token, _ := jwt.Sign("user1", testSecret, 1*time.Hour)
	resp := authExchange(t, conn, token, messages.WireProtocolVersion)

	if !resp.OK {
		t.Fatalf("a conforming client must survive min-protocol-version=1, got %q", resp.Error)
	}
}

// The version check runs before the token is verified, so a bad token AND a bad
// version reports the version. Debugging the credential when the real fault is
// the build is the wrong-layer chase this ordering avoids.
func TestVersionRefusalOutranksAnInvalidToken(t *testing.T) {
	gw, _ := startGatewayWithOptions(t)

	conn := dialGateway(t, gw)
	defer conn.Close()

	resp := authExchange(t, conn, "definitely-not-a-jwt", messages.WireProtocolVersion+1)

	if resp.OK {
		t.Fatal("must not be admitted")
	}
	if resp.Error != messages.ReasonProtocolVersionMismatch {
		t.Fatalf("reason = %q, want %q: the schema fault is the actionable one",
			resp.Error, messages.ReasonProtocolVersionMismatch)
	}
}

// counterValue2 reads a plain (unlabelled) Counter.
func counterValue2(t *testing.T, c prometheus.Counter) float64 {
	t.Helper()
	m := &dto.Metric{}
	if err := c.Write(m); err != nil {
		t.Fatalf("write metric: %v", err)
	}
	return m.GetCounter().GetValue()
}
