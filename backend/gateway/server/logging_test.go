package server

import (
	"bytes"
	"encoding/json"
	"log/slog"
	"net"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// The gateway's observability contract is a volume contract as much as a
// content one: at 200 concurrent clients a line on a per-message path is a
// self-inflicted denial of service against the disk. These tests pin both
// halves — that the once-per-session events are logged, and that the paths a
// client can drive at will are not.

// logSink is a concurrency-safe buffer of JSON log records. The gateway writes
// to it from several goroutines (the accept loop, each connection's read loop,
// the heartbeat loop), so the writes must be serialised.
type logSink struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (s *logSink) Write(p []byte) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.buf.Write(p)
}

func (s *logSink) raw() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.buf.String()
}

// records decodes what has been logged so far. Records are returned in the
// order they were emitted.
func (s *logSink) records(t *testing.T) []map[string]any {
	t.Helper()
	var out []map[string]any
	for _, line := range strings.Split(strings.TrimSpace(s.raw()), "\n") {
		if line == "" {
			continue
		}
		var rec map[string]any
		if err := json.Unmarshal([]byte(line), &rec); err != nil {
			t.Fatalf("log line is not JSON (%v): %s", err, line)
		}
		out = append(out, rec)
	}
	return out
}

// find returns every record with the given msg, optionally filtered to records
// at or above minLevel.
func (s *logSink) find(t *testing.T, msg string) []map[string]any {
	t.Helper()
	var out []map[string]any
	for _, rec := range s.records(t) {
		if rec["msg"] == msg {
			out = append(out, rec)
		}
	}
	return out
}

// atOrAbove counts records at info level or higher — the volume that actually
// reaches a production log, since the default LOG_LEVEL is info.
func (s *logSink) atOrAbove(t *testing.T, level string) int {
	t.Helper()
	want := map[string]int{"DEBUG": 0, "INFO": 1, "WARN": 2, "ERROR": 3}
	n := 0
	for _, rec := range s.records(t) {
		lvl, _ := rec["level"].(string)
		if want[lvl] >= want[level] {
			n++
		}
	}
	return n
}

// startLoggingGateway starts a gateway whose logger writes JSON into the
// returned sink at debug level, so a test can assert on both what is logged and
// what is only logged at debug.
func startLoggingGateway(t *testing.T) (*Gateway, *logSink) {
	t.Helper()

	sessionStore := storage.NewMemorySessionStore()
	serverRegistry := storage.NewMemoryServerRegistry()
	serverRegistry.Register(nil, storage.ServerInfo{
		ServerID:    "srv1",
		MapID:       "map_forest",
		Addr:        "10.0.0.1:9000",
		Capacity:    100,
		PlayerCount: 10,
	})

	sink := &logSink{}
	log := slog.New(slog.NewJSONHandler(sink, &slog.HandlerOptions{Level: slog.LevelDebug}))

	gw := New(session.NewSessionManager(sessionStore), registry.NewRegistryService(serverRegistry),
		testSecret, log, WithJoinTokenSecret(testSecret))

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
			t.Cleanup(gw.Shutdown)
			return gw, sink
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("gateway did not start in time")
	return nil, nil
}

// handshake runs one full client session — auth, enter world, explicit
// disconnect — and returns the auth token and the join token it was issued.
func handshake(t *testing.T, gw *Gateway) (authToken, joinToken string) {
	t.Helper()

	conn := dialGateway(t, gw)
	defer conn.Close()

	authToken, err := jwt.Sign("user-obs", testSecret, time.Hour)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: authToken})
	sendEnvelope(t, conn, authEnv)
	readEnvelope(t, conn)

	enterEnv, _ := messages.NewEnvelope(messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: "map_forest"})
	sendEnvelope(t, conn, enterEnv)
	resp := readEnvelope(t, conn)
	var ew messages.EnterWorldResponse
	if err := resp.UnmarshalPayload(&ew); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if ew.JoinToken == "" {
		t.Fatal("no join token issued")
	}

	discEnv, _ := messages.NewEnvelope(messages.MsgDisconnect, messages.DisconnectMessage{Reason: "done"})
	sendEnvelope(t, conn, discEnv)
	// Let the server-side handler run before the test inspects the log.
	time.Sleep(200 * time.Millisecond)
	return authToken, ew.JoinToken
}

// TestHandshakeIsDiagnosableFromTheGatewayLog is the whole point of the change:
// a completed client handshake must be reconstructable from the gateway's own
// output, without correlating against the game server.
func TestHandshakeIsDiagnosableFromTheGatewayLog(t *testing.T) {
	gw, sink := startLoggingGateway(t)
	handshake(t, gw)

	for _, msg := range []string{"auth ok", "enter world assigned", "client disconnect"} {
		recs := sink.find(t, msg)
		if len(recs) != 1 {
			t.Fatalf("want exactly 1 %q line per session, got %d", msg, len(recs))
		}
		if recs[0]["user"] != "user-obs" {
			t.Errorf("%q: user = %v, want user-obs", msg, recs[0]["user"])
		}
	}

	// The assignment line must name the destination, or the log still cannot
	// answer "where was this client sent?".
	assigned := sink.find(t, "enter world assigned")[0]
	if assigned["map"] != "map_forest" {
		t.Errorf("map = %v, want map_forest", assigned["map"])
	}
	if assigned["server"] != "srv1" || assigned["server_addr"] != "10.0.0.1:9000" {
		t.Errorf("server = %v @ %v, want srv1 @ 10.0.0.1:9000", assigned["server"], assigned["server_addr"])
	}

	// One conn id ties the session's lines together.
	authConn := sink.find(t, "auth ok")[0]["conn"]
	if authConn == nil {
		t.Fatal("auth ok has no conn correlation id")
	}
	if assigned["conn"] != authConn {
		t.Errorf("conn ids differ across a session: %v vs %v", authConn, assigned["conn"])
	}
}

// TestLogNeverContainsCredentials guards the one thing that makes logging this
// path dangerous. The auth JWT and the issued join token are both bearer
// credentials: a log holding either is a log that can be replayed into a
// session.
func TestLogNeverContainsCredentials(t *testing.T) {
	gw, sink := startLoggingGateway(t)
	authToken, joinToken := handshake(t, gw)

	raw := sink.raw()
	for name, secret := range map[string]string{
		"auth token": authToken,
		"join token": joinToken,
		"jwt secret": testSecret,
	} {
		if strings.Contains(raw, secret) {
			t.Errorf("%s leaked into the gateway log", name)
		}
		// A JWT signature alone is enough to matter, so check the parts too.
		if parts := strings.Split(secret, "."); len(parts) == 3 {
			for _, part := range parts {
				if len(part) > 8 && strings.Contains(raw, part) {
					t.Errorf("%s: a component leaked into the gateway log", name)
				}
			}
		}
	}
}

// TestSessionVolumeIsBoundedPerSession pins the number of production-visible
// lines one healthy session costs. If a future change puts a line on a
// per-message path, this is what catches it before a load test does.
func TestSessionVolumeIsBoundedPerSession(t *testing.T) {
	gw, sink := startLoggingGateway(t)
	// Baseline excludes the one-off startup lines ("gateway listening"), which
	// are not per-session and would otherwise be counted against the session.
	before := sink.atOrAbove(t, "INFO")
	handshake(t, gw)

	const wantMax = 3 // auth ok, enter world assigned, client disconnect
	if got := sink.atOrAbove(t, "INFO") - before; got > wantMax {
		t.Errorf("a single healthy session logged %d lines at info+, want <= %d:\n%s",
			got, wantMax, sink.raw())
	}
}

// TestHeartbeatDoesNotLog covers the per-message path the gateway actually has.
// Ping/pong is the only frame a connected client repeats, and at 200 clients on
// a 10s interval it is 20 frames per second; a line each would be 1.7M lines a
// day from heartbeats alone.
func TestHeartbeatDoesNotLog(t *testing.T) {
	gw, sink := startLoggingGateway(t)

	conn := dialGateway(t, gw)
	defer conn.Close()

	token, _ := jwt.Sign("user-hb", testSecret, time.Hour)
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn, authEnv)
	readEnvelope(t, conn)

	before := sink.atOrAbove(t, "INFO")

	const beats = 50
	for i := 0; i < beats; i++ {
		pingEnv, _ := messages.NewEnvelope(messages.MsgPing, messages.PingMessage{
			Timestamp: time.Now().UnixMilli(),
		})
		sendEnvelope(t, conn, pingEnv)
		readEnvelope(t, conn) // the pong
		pongEnv, _ := messages.NewEnvelope(messages.MsgPong, messages.PongMessage{})
		sendEnvelope(t, conn, pongEnv)
	}
	time.Sleep(100 * time.Millisecond)

	if after := sink.atOrAbove(t, "INFO"); after != before {
		t.Errorf("%d heartbeat exchanges produced %d log lines at info+, want 0:\n%s",
			beats, after-before, sink.raw())
	}
}

// TestRepeatedAuthFailureLogsOnce: MsgAuth is client-driven, and the
// per-connection message limiter (60 frames/s by default) bounds the rate
// without making it safe, so a socket looping bad tokens must not be able to
// drive log volume on its own. The first failure is reported —
// that is the one an operator needs — and the rest are latched to debug.
func TestRepeatedAuthFailureLogsOnce(t *testing.T) {
	gw, sink := startLoggingGateway(t)

	conn := dialGateway(t, gw)
	defer conn.Close()

	const attempts = 20
	for i := 0; i < attempts; i++ {
		env, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: "not-a-token"})
		sendEnvelope(t, conn, env)
		readEnvelope(t, conn) // the rejection
	}
	time.Sleep(100 * time.Millisecond)

	var visible int
	for _, rec := range sink.find(t, "auth failed") {
		if rec["level"] != "DEBUG" {
			visible++
			if rec["reason"] != "invalid_token" {
				t.Errorf("reason = %v, want invalid_token", rec["reason"])
			}
		}
	}
	if visible != 1 {
		t.Errorf("%d failed auth attempts on one connection logged %d visible lines, want 1",
			attempts, visible)
	}
}

// TestUnexpectedMessageTypeLogsOnce covers the same hazard for a client that
// speaks the wrong protocol — e.g. one that sends gameplay frames to the
// gateway, which is exactly the ADR-3 mistake and would otherwise warn once per
// tick, forever.
func TestUnexpectedMessageTypeLogsOnce(t *testing.T) {
	gw, sink := startLoggingGateway(t)

	conn := dialGateway(t, gw)
	defer conn.Close()

	token, _ := jwt.Sign("user-wrong", testSecret, time.Hour)
	authEnv, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn, authEnv)
	readEnvelope(t, conn)

	const frames = 30
	for i := 0; i < frames; i++ {
		env, _ := messages.NewEnvelope(messages.MsgInput, map[string]any{"move_x": 1})
		sendEnvelope(t, conn, env)
	}
	time.Sleep(200 * time.Millisecond)

	var visible int
	for _, rec := range sink.find(t, "unexpected message type") {
		if rec["level"] != "DEBUG" {
			visible++
		}
	}
	if visible != 1 {
		t.Errorf("%d unroutable frames logged %d visible lines, want 1", frames, visible)
	}
}

// TestUnauthenticatedConnectionStaysQuiet: an accept is the one event anyone
// who can open a socket may mint, so a connect-and-say-nothing peer must not
// reach the production log at all.
func TestUnauthenticatedConnectionStaysQuiet(t *testing.T) {
	gw, sink := startLoggingGateway(t)
	before := sink.atOrAbove(t, "INFO") // discount the startup lines

	for i := 0; i < 10; i++ {
		conn, err := net.DialTimeout("tcp", gw.Addr(), 2*time.Second)
		if err != nil {
			t.Fatalf("dial: %v", err)
		}
		conn.Close()
	}
	time.Sleep(200 * time.Millisecond)

	if got := sink.atOrAbove(t, "INFO") - before; got != 0 {
		t.Errorf("10 silent connections produced %d info+ lines, want 0:\n%s", got, sink.raw())
	}
	// They are still visible when debugging.
	if len(sink.find(t, "client connected")) == 0 {
		t.Error("connections should still be traceable at debug level")
	}
}
