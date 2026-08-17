package load

import (
	"bufio"
	"bytes"
	"context"
	"io"
	"net"
	"sync/atomic"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
)

// scriptConn is a net.Conn whose reads come from a pre-recorded frame script and
// whose writes land in a buffer. It makes the heartbeat tests fully
// deterministic: no goroutines, no wall-clock waits, and a run that "lasts"
// several minutes costs nothing.
type scriptConn struct {
	in  *bytes.Reader
	out bytes.Buffer
}

func (c *scriptConn) Read(b []byte) (int, error)       { return c.in.Read(b) }
func (c *scriptConn) Write(b []byte) (int, error)      { return c.out.Write(b) }
func (c *scriptConn) Close() error                     { return nil }
func (c *scriptConn) LocalAddr() net.Addr              { return dummyAddr{} }
func (c *scriptConn) RemoteAddr() net.Addr             { return dummyAddr{} }
func (c *scriptConn) SetDeadline(time.Time) error      { return nil }
func (c *scriptConn) SetReadDeadline(time.Time) error  { return nil }
func (c *scriptConn) SetWriteDeadline(time.Time) error { return nil }

type dummyAddr struct{}

func (dummyAddr) Network() string { return "script" }
func (dummyAddr) String() string  { return "script" }

// frames encodes a sequence of envelopes into one wire-framed byte stream.
func frames(t *testing.T, enc messages.Encoding, msgs ...struct {
	typ     messages.MsgType
	payload any
}) []byte {
	t.Helper()
	var buf bytes.Buffer
	for _, m := range msgs {
		env, err := messages.NewEnvelopeAs(enc, m.typ, m.payload)
		if err != nil {
			t.Fatalf("encode %d: %v", m.typ, err)
		}
		raw, err := messages.Encode(env)
		if err != nil {
			t.Fatalf("frame %d: %v", m.typ, err)
		}
		buf.Write(raw)
	}
	return buf.Bytes()
}

type frame = struct {
	typ     messages.MsgType
	payload any
}

// newScriptedPlayer wires a player to read the given frame stream and write into
// a capture buffer.
func newScriptedPlayer(enc messages.Encoding, in []byte) (*player, *scriptConn) {
	conn := &scriptConn{in: bytes.NewReader(in)}
	p := &player{
		cfg:       Config{Encoding: enc, Timeout: time.Second, Players: 1},
		stats:     newPlayerStats(8),
		measuring: &atomic.Bool{},
		gsConn:    conn,
		gsRead:    bufio.NewReaderSize(conn, 64*1024),
	}
	return p, conn
}

// decodeAll pulls every frame out of a captured write buffer.
func decodeAll(t *testing.T, raw []byte) []messages.Envelope {
	t.Helper()
	var out []messages.Envelope
	r := bufio.NewReader(bytes.NewReader(raw))
	for {
		env, _, err := decodeCounted(r)
		if err == io.EOF {
			return out
		}
		if err != nil {
			t.Fatalf("decode captured frame: %v", err)
		}
		out = append(out, env)
	}
}

// A MsgPing must be answered with a MsgPong echoing the probe's own timestamp,
// in the encoding the probe arrived in. Echoing matters: the peer measures RTT
// against the value it sent, so a regenerated timestamp corrupts its numbers.
func TestAnswerPingEchoesTimestampInBothEncodings(t *testing.T) {
	tests := []struct {
		name string
		enc  messages.Encoding
		ts   int64
	}{
		{"json", messages.EncodingJSON, 1700000000123},
		{"proto", messages.EncodingProto, 1700000000456},
		{"json zero timestamp", messages.EncodingJSON, 0},
		{"proto negative timestamp", messages.EncodingProto, -7},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p, conn := newScriptedPlayer(tt.enc, nil)
			ping, err := messages.NewEnvelopeAs(tt.enc, messages.MsgPing,
				messages.PingMessage{Timestamp: tt.ts})
			if err != nil {
				t.Fatal(err)
			}
			if err := p.answerPing(conn, ping); err != nil {
				t.Fatalf("answerPing: %v", err)
			}

			got := decodeAll(t, conn.out.Bytes())
			if len(got) != 1 {
				t.Fatalf("wrote %d frames, want exactly 1 pong", len(got))
			}
			if got[0].Type != messages.MsgPong {
				t.Errorf("reply type = %d, want MsgPong (%d)", got[0].Type, messages.MsgPong)
			}
			if got[0].Enc != tt.enc {
				t.Errorf("reply encoding = %v, want %v (the connection's own encoding)",
					got[0].Enc, tt.enc)
			}
			var pong messages.PongMessage
			if err := got[0].UnmarshalPayload(&pong); err != nil {
				t.Fatalf("decode pong payload: %v", err)
			}
			if pong.Timestamp != tt.ts {
				t.Errorf("pong.Timestamp = %d, want %d echoed back unchanged", pong.Timestamp, tt.ts)
			}
		})
	}
}

// The read loop must answer a ping arriving on the game-server socket. This is
// the regression test for issue #142: before the fix readLoop skipped every
// non-snapshot frame, so the server saw no pong and closed the connection after
// its 30s timeout.
func TestReadLoopAnswersGameServerPing(t *testing.T) {
	for _, enc := range []messages.Encoding{messages.EncodingJSON, messages.EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) {
			in := frames(t, enc,
				frame{messages.MsgPing, messages.PingMessage{Timestamp: 42}},
				frame{messages.MsgSnapshot, messages.SnapshotMessage{Tick: 1, Full: true}},
				frame{messages.MsgPing, messages.PingMessage{Timestamp: 99}},
			)
			p, conn := newScriptedPlayer(enc, in)
			p.measuring.Store(true)

			// The script runs out, which the loop reports as a closed connection.
			_ = p.readLoop(context.Background(), make(chan pendingInput))

			if p.stats.Heartbeats != 2 {
				t.Errorf("Heartbeats = %d, want 2 (one per MsgPing received)", p.stats.Heartbeats)
			}
			got := decodeAll(t, conn.out.Bytes())
			var pongs []int64
			for _, env := range got {
				if env.Type != messages.MsgPong {
					continue
				}
				var pong messages.PongMessage
				if err := env.UnmarshalPayload(&pong); err != nil {
					t.Fatalf("decode pong: %v", err)
				}
				pongs = append(pongs, pong.Timestamp)
			}
			if len(pongs) != 2 || pongs[0] != 42 || pongs[1] != 99 {
				t.Errorf("pong timestamps = %v, want [42 99]", pongs)
			}
		})
	}
}

// Heartbeat frames must not leak into any gameplay measurement. If they did, the
// fix would bias the throughput and recv% numbers it exists to make trustworthy.
func TestHeartbeatIsExcludedFromMeasurements(t *testing.T) {
	enc := messages.EncodingJSON

	// Baseline: two snapshots and nothing else.
	clean := frames(t, enc,
		frame{messages.MsgSnapshot, messages.SnapshotMessage{Tick: 1, Full: true}},
		frame{messages.MsgSnapshot, messages.SnapshotMessage{Tick: 2}},
	)
	// Same gameplay stream, with heartbeat traffic interleaved.
	noisy := frames(t, enc,
		frame{messages.MsgPing, messages.PingMessage{Timestamp: 1}},
		frame{messages.MsgSnapshot, messages.SnapshotMessage{Tick: 1, Full: true}},
		frame{messages.MsgPong, messages.PongMessage{Timestamp: 1, ServerTime: 2}},
		frame{messages.MsgSnapshot, messages.SnapshotMessage{Tick: 2}},
		frame{messages.MsgPing, messages.PingMessage{Timestamp: 3}},
	)

	run := func(in []byte) *PlayerStats {
		p, _ := newScriptedPlayer(enc, in)
		p.measuring.Store(true)
		_ = p.readLoop(context.Background(), make(chan pendingInput))
		return p.stats
	}
	base, noise := run(clean), run(noisy)

	if noise.Snapshots != base.Snapshots {
		t.Errorf("Snapshots = %d with heartbeats, %d without: a MsgPing/MsgPong was counted as a snapshot",
			noise.Snapshots, base.Snapshots)
	}
	if noise.Snapshots != 2 {
		t.Errorf("Snapshots = %d, want 2", noise.Snapshots)
	}
	if got, want := noise.SnapInterval.Len(), base.SnapInterval.Len(); got != want {
		t.Errorf("snapshot-interval samples = %d with heartbeats, %d without", got, want)
	}
	if rx, want := atomic.LoadUint64(&noise.BytesRx), atomic.LoadUint64(&base.BytesRx); rx != want {
		t.Errorf("BytesRx = %d with heartbeats, %d without: heartbeat bytes were attributed to gameplay",
			rx, want)
	}
	if tx := atomic.LoadUint64(&noise.BytesTx); tx != 0 {
		t.Errorf("BytesTx = %d, want 0: the pong the harness sent must not count as gameplay traffic", tx)
	}
	if noise.Heartbeats != 2 {
		t.Errorf("Heartbeats = %d, want 2", noise.Heartbeats)
	}
}

// A run that outlives the server's 30s pong timeout must keep its player. The
// timing is simulated rather than slept: the script carries the pings a real
// server would emit over the modelled duration, one per 10s PingInterval, and
// the player must answer every one of them without ever going silent for longer
// than the 30s PongTimeout.
func TestPlayerSurvivesRunsLongerThanPongTimeout(t *testing.T) {
	const (
		pingInterval = 10 * time.Second
		pongTimeout  = 30 * time.Second
	)
	tests := []struct {
		name     string
		duration time.Duration
	}{
		{"20s run, inside the timeout", 20 * time.Second},
		{"47s run, the 80 players at ramp 3/s case", 47 * time.Second},
		{"5m soak", 5 * time.Minute},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			pings := int(tt.duration / pingInterval)
			var script []frame
			for i := 1; i <= pings; i++ {
				script = append(script,
					frame{messages.MsgSnapshot, messages.SnapshotMessage{Tick: uint64(i), Full: i == 1}},
					frame{messages.MsgPing, messages.PingMessage{Timestamp: int64(i) * pingInterval.Milliseconds()}},
				)
			}
			p, conn := newScriptedPlayer(messages.EncodingProto, frames(t, messages.EncodingProto, script...))
			p.measuring.Store(true)
			_ = p.readLoop(context.Background(), make(chan pendingInput))

			if p.stats.Heartbeats != pings {
				t.Fatalf("answered %d of %d pings", p.stats.Heartbeats, pings)
			}
			// Walk the answers in modelled time: the peer declares the connection
			// dead if the gap between two consecutive pongs exceeds PongTimeout.
			var last time.Duration
			answered := 0
			for _, env := range decodeAll(t, conn.out.Bytes()) {
				if env.Type != messages.MsgPong {
					continue
				}
				var pong messages.PongMessage
				if err := env.UnmarshalPayload(&pong); err != nil {
					t.Fatal(err)
				}
				at := time.Duration(pong.Timestamp) * time.Millisecond
				if at-last > pongTimeout {
					t.Fatalf("gap of %s between pongs at %s exceeds the %s timeout: the peer would have closed us",
						at-last, at, pongTimeout)
				}
				last = at
				answered++
			}
			if answered != pings {
				t.Errorf("captured %d pongs, want %d", answered, pings)
			}
			if tt.duration-last > pongTimeout {
				t.Errorf("last pong at %s leaves %s of silence before the run ends at %s",
					last, tt.duration-last, tt.duration)
			}
		})
	}
}

// The held gateway socket runs the same heartbeat (gateway pingInterval 10s /
// pongTimeout 30s) and nothing was reading it, so -hold-gateway silently stopped
// holding anything after 30s.
func TestHoldGatewayLoopAnswersPings(t *testing.T) {
	for _, enc := range []messages.Encoding{messages.EncodingJSON, messages.EncodingProto} {
		t.Run(enc.String(), func(t *testing.T) {
			in := frames(t, enc,
				frame{messages.MsgPing, messages.PingMessage{Timestamp: 10}},
				frame{messages.MsgPing, messages.PingMessage{Timestamp: 20}},
			)
			conn := &scriptConn{in: bytes.NewReader(in)}
			p := &player{
				cfg:       Config{Encoding: enc, Timeout: time.Second},
				stats:     newPlayerStats(4),
				measuring: &atomic.Bool{},
			}
			p.holdGatewayLoop(context.Background(), conn, bufio.NewReader(conn))

			if got := atomic.LoadUint64(&p.stats.GatewayHeartbeats); got != 2 {
				t.Errorf("GatewayHeartbeats = %d, want 2", got)
			}
			got := decodeAll(t, conn.out.Bytes())
			if len(got) != 2 {
				t.Fatalf("wrote %d frames, want 2 pongs", len(got))
			}
			for i, want := range []int64{10, 20} {
				if got[i].Type != messages.MsgPong {
					t.Fatalf("frame %d type = %d, want MsgPong", i, got[i].Type)
				}
				var pong messages.PongMessage
				if err := got[i].UnmarshalPayload(&pong); err != nil {
					t.Fatal(err)
				}
				if pong.Timestamp != want {
					t.Errorf("pong %d timestamp = %d, want %d", i, pong.Timestamp, want)
				}
			}
		})
	}
}

// A ping arriving mid-handshake must be answered too, not skipped: the peer's
// ping timer does not wait for EnterWorld to return, and a slow allocation could
// otherwise burn the whole pong budget before the run starts.
func TestRoundTripAnswersPingWhileWaiting(t *testing.T) {
	enc := messages.EncodingProto
	in := frames(t, enc,
		frame{messages.MsgPing, messages.PingMessage{Timestamp: 5}},
		frame{messages.MsgJoinTokenResp, messages.JoinTokenResponse{OK: true}},
	)
	conn := &scriptConn{in: bytes.NewReader(in)}
	p := &player{cfg: Config{Encoding: enc, Timeout: time.Second}, stats: newPlayerStats(4)}

	var resp messages.JoinTokenResponse
	err := p.roundTrip(conn, bufio.NewReader(conn), messages.MsgJoinToken,
		messages.JoinTokenRequest{Token: "t"}, messages.MsgJoinTokenResp, &resp)
	if err != nil {
		t.Fatalf("roundTrip: %v", err)
	}
	if !resp.OK {
		t.Error("join response not OK")
	}
	var sawPong bool
	for _, env := range decodeAll(t, conn.out.Bytes()) {
		if env.Type == messages.MsgPong {
			sawPong = true
		}
	}
	if !sawPong {
		t.Error("no MsgPong written: a heartbeat during the handshake was skipped")
	}
}
