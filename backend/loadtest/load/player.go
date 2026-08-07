package load

import (
	"bufio"
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net"
	"sync/atomic"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// maxFrame mirrors the 1MB cap in shared/messages.Decode.
const maxFrame = 1 << 20

// joinTokenTTL mirrors shared/constants.JoinTokenTTL (30s). Only used by
// JoinDirect, where this tool mints the token itself.
const joinTokenTTL = 30 * time.Second

// PlayerStats is one virtual player's client-observed measurements. Only
// observations made inside the measurement window are recorded; connection
// counters cover the whole run.
type PlayerStats struct {
	SnapInterval *Samples // gap between consecutive MsgSnapshot arrivals (ms)
	AckLatency   *Samples // MsgInput send -> first snapshot whose ack_tick covers it (ms)

	BytesRx uint64
	BytesTx uint64

	Snapshots int
	Keyframes int
	Deltas    int
	Inputs    int

	JoinLatency time.Duration
	Joined      bool
	Err         error
	// ErrPhase names the phase that failed: auth, gateway, join or run.
	ErrPhase string
}

func newPlayerStats(hint int) *PlayerStats {
	return &PlayerStats{
		SnapInterval: NewSamples(hint),
		AckLatency:   NewSamples(hint),
	}
}

// player is a single virtual client walking the full connection flow.
type player struct {
	cfg   Config
	idx   int
	runID string
	auth  *authProvider

	userID    string
	gwJWT     string
	joinToken string
	srvAddr   string
	srvTrans  string

	gwConn net.Conn
	gsConn net.Conn
	gsRead *bufio.Reader

	stats     *PlayerStats
	measuring *atomic.Bool
}

// Run executes the whole lifecycle for one virtual player and returns its
// stats. It never panics on a network failure — the failure is the measurement.
func (p *player) Run(ctx context.Context, joined chan<- struct{}) *PlayerStats {
	p.stats = newPlayerStats(int(p.cfg.Duration.Seconds()) * p.cfg.TickRate)
	defer p.closeAll()

	if err := p.acquireToken(ctx); err != nil {
		p.fail("auth", err)
		return p.stats
	}
	if err := p.gatewayHandshake(); err != nil {
		p.fail("gateway", err)
		return p.stats
	}
	start := time.Now()
	if err := p.gameServerJoin(); err != nil {
		p.fail("join", err)
		return p.stats
	}
	p.stats.JoinLatency = time.Since(start)
	p.stats.Joined = true
	select {
	case joined <- struct{}{}:
	default:
	}

	if err := p.loop(ctx); err != nil {
		p.fail("run", err)
	}
	return p.stats
}

func (p *player) fail(phase string, err error) {
	p.stats.ErrPhase = phase
	p.stats.Err = err
}

func (p *player) closeAll() {
	if p.gsConn != nil {
		// Polite disconnect so the server frees the slot immediately instead of
		// holding the entity for the reconnect window. Without it, back-to-back
		// sweep levels would inherit the previous level's ghosts.
		if env, err := messages.NewEnvelope(messages.MsgDisconnect, struct{}{}); err == nil {
			_ = p.send(p.gsConn, env)
		}
		_ = p.gsConn.Close()
	}
	if p.gwConn != nil {
		_ = p.gwConn.Close()
	}
}

// ---------------------------------------------------------------- auth

func (p *player) acquireToken(ctx context.Context) error {
	if p.cfg.AuthMode == AuthNakama {
		uid, tok, err := p.auth.nakamaToken(ctx)
		if err != nil {
			return err
		}
		p.userID, p.gwJWT = uid, tok
		return nil
	}
	// Pre-signed: mint exactly what Nakama's gateway_token RPC would mint. The
	// gateway verifies it locally against the shared secret, so no Nakama
	// round-trip lands in the measurement. See AuthPresigned for why.
	p.userID = fmt.Sprintf("lt-%s-%05d", p.runID, p.idx)
	tok, err := jwt.Sign(p.userID, p.cfg.JWTSecret, time.Hour)
	if err != nil {
		return fmt.Errorf("sign gateway jwt: %w", err)
	}
	p.gwJWT = tok
	return nil
}

// ---------------------------------------------------------------- gateway

func (p *player) gatewayHandshake() error {
	if p.cfg.JoinMode == JoinDirect {
		// Mint the same join token the gateway would have minted: HS256 over the
		// user id with the target server's id in the `sid` claim, which is what
		// GameServer.cs verifies. No gateway socket is opened at all.
		tok, err := jwt.SignWithServer(p.userID, p.cfg.ServerID, p.cfg.JoinTokenSecret, joinTokenTTL)
		if err != nil {
			return fmt.Errorf("sign join token: %w", err)
		}
		p.joinToken = tok
		p.srvAddr = p.cfg.GameServerAddr
		p.srvTrans = transport.Normalize(p.cfg.Transport)
		return nil
	}

	conn, err := p.dial(p.cfg.Transport, p.cfg.GatewayAddr)
	if err != nil {
		return fmt.Errorf("dial gateway: %w", err)
	}
	p.gwConn = conn
	r := bufio.NewReaderSize(conn, 8192)

	var authResp messages.AuthResponse
	if err := p.roundTrip(conn, r, messages.MsgAuth,
		messages.AuthRequest{Token: p.gwJWT}, messages.MsgAuthResp, &authResp); err != nil {
		return fmt.Errorf("gateway auth: %w", err)
	}
	if !authResp.OK {
		return fmt.Errorf("gateway auth rejected: %s", authResp.Error)
	}

	var enterResp messages.EnterWorldResponse
	if err := p.roundTrip(conn, r, messages.MsgEnterWorld,
		messages.EnterWorldRequest{MapID: p.cfg.MapID}, messages.MsgEnterWorldResp, &enterResp); err != nil {
		return fmt.Errorf("enter world: %w", err)
	}
	if enterResp.Error != "" {
		return fmt.Errorf("enter world rejected: %s", enterResp.Error)
	}
	if enterResp.ServerAddr == "" || enterResp.JoinToken == "" {
		return fmt.Errorf("enter world: missing server_addr/join_token")
	}
	p.srvAddr = enterResp.ServerAddr
	p.joinToken = enterResp.JoinToken
	p.srvTrans = transport.Normalize(enterResp.Transport)

	if !p.cfg.HoldGateway {
		_ = conn.Close()
		p.gwConn = nil
	}
	return nil
}

// ---------------------------------------------------------------- game server

func (p *player) gameServerJoin() error {
	conn, err := p.dial(p.srvTrans, p.srvAddr)
	if err != nil {
		return fmt.Errorf("dial gameserver: %w", err)
	}
	p.gsConn = conn
	p.gsRead = bufio.NewReaderSize(conn, 64*1024)

	var joinResp messages.JoinTokenResponse
	if err := p.roundTrip(conn, p.gsRead, messages.MsgJoinToken,
		messages.JoinTokenRequest{Token: p.joinToken}, messages.MsgJoinTokenResp, &joinResp); err != nil {
		return fmt.Errorf("join: %w", err)
	}
	if !joinResp.OK {
		return fmt.Errorf("join rejected: %s", joinResp.Error)
	}
	return nil
}

// pendingInput pairs a client input tick with the instant it left the socket.
type pendingInput struct {
	tick uint64
	sent time.Time
}

// loop runs the steady-state input/snapshot exchange until ctx is done.
//
// The reader owns all measurement: snapshot arrival intervals and ack latency
// are both observed on receipt. The writer only records send timestamps into a
// channel the reader drains, so the two goroutines never share mutable state.
func (p *player) loop(ctx context.Context) error {
	sentCh := make(chan pendingInput, 1024)
	readErr := make(chan error, 1)

	go func() { readErr <- p.readLoop(ctx, sentCh) }()

	ticker := time.NewTicker(p.cfg.InputInterval())
	defer ticker.Stop()

	moveX, moveY := p.movementVector()
	var tick uint64
	for {
		select {
		case <-ctx.Done():
			// Give the reader a moment to drain in-flight snapshots, then stop.
			_ = p.gsConn.SetReadDeadline(time.Now().Add(200 * time.Millisecond))
			<-readErr
			return nil
		case err := <-readErr:
			if err != nil {
				return fmt.Errorf("read: %w", err)
			}
			return nil
		case <-ticker.C:
			tick++
			env, err := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
				Tick: tick, MoveX: moveX, MoveY: moveY,
			})
			if err != nil {
				return fmt.Errorf("encode input: %w", err)
			}
			now := time.Now()
			n, err := p.sendCounted(p.gsConn, env)
			if err != nil {
				return fmt.Errorf("send input %d: %w", tick, err)
			}
			if p.measuring.Load() {
				atomic.AddUint64(&p.stats.BytesTx, uint64(n))
				p.stats.Inputs++
				select {
				case sentCh <- pendingInput{tick: tick, sent: now}:
				default: // reader is behind; drop the pairing rather than block the tick
				}
			}
		}
	}
}

// readLoop consumes frames until the socket errors or ctx is done, recording
// snapshot intervals, ack latency and received bytes.
func (p *player) readLoop(ctx context.Context, sentCh <-chan pendingInput) error {
	var (
		lastSnap time.Time
		pending  []pendingInput
	)
	for {
		if ctx.Err() != nil {
			return nil
		}
		// A generous deadline: it exists to unblock shutdown, not to police the
		// server. A real stall shows up as a huge snapshot interval, which is the
		// measurement we want, not as a timeout error.
		if err := p.gsConn.SetReadDeadline(time.Now().Add(p.cfg.Timeout)); err != nil {
			return err
		}
		env, n, err := decodeCounted(p.gsRead)
		if err != nil {
			if ctx.Err() != nil {
				return nil
			}
			if ne, ok := err.(net.Error); ok && ne.Timeout() {
				return fmt.Errorf("no frame for %s (server stalled or dropped us)", p.cfg.Timeout)
			}
			if err == io.EOF {
				return fmt.Errorf("server closed the connection")
			}
			return err
		}
		now := time.Now()
		measuring := p.measuring.Load()
		if measuring {
			atomic.AddUint64(&p.stats.BytesRx, uint64(n))
		}
		if env.Type != messages.MsgSnapshot {
			continue
		}
		var snap messages.SnapshotMessage
		if err := json.Unmarshal(env.Payload, &snap); err != nil {
			continue
		}

		if !measuring {
			// Outside the window: keep the interval anchor fresh so the first
			// in-window sample is a real gap, not a gap that spans the warmup.
			lastSnap = now
			// Drain send timestamps so they do not age into the window.
			pending = drainPending(pending, sentCh)
			pending = pending[:0]
			continue
		}

		p.stats.Snapshots++
		if snap.Full {
			p.stats.Keyframes++
		} else {
			p.stats.Deltas++
		}
		if !lastSnap.IsZero() {
			p.stats.SnapInterval.AddDuration(now.Sub(lastSnap))
		}
		lastSnap = now

		pending = drainPending(pending, sentCh)
		if snap.AckTick > 0 {
			// Every input whose tick the server has now acknowledged gets exactly
			// one latency sample, taken against the first snapshot that covers it.
			cut := 0
			for _, pi := range pending {
				if pi.tick <= snap.AckTick {
					p.stats.AckLatency.AddDuration(now.Sub(pi.sent))
					cut++
					continue
				}
				break
			}
			pending = pending[cut:]
		}
	}
}

// drainPending moves everything currently buffered in ch onto the tail of dst.
func drainPending(dst []pendingInput, ch <-chan pendingInput) []pendingInput {
	for {
		select {
		case pi := <-ch:
			dst = append(dst, pi)
		default:
			return dst
		}
	}
}

// movementVector returns the input direction this player drives every tick.
func (p *player) movementVector() (float32, float32) {
	switch p.cfg.Movement {
	case MovementStill:
		return 0, 0
	case MovementSpread:
		// Distinct heading per player inside the +X/+Y quadrant, so nobody is
		// clamped against the map's zero edge.
		angle := (math.Pi / 2) * float64(p.idx) / float64(maxInt(p.cfg.Players-1, 1))
		return float32(math.Cos(angle)), float32(math.Sin(angle))
	default: // MovementCluster
		return 1, 0
	}
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}

// ---------------------------------------------------------------- wire utils

func (p *player) dial(kind, addr string) (net.Conn, error) {
	target := NormalizeDialAddr(addr)
	conn, err := transport.Dial(kind, target, p.cfg.Timeout)
	if err != nil {
		return nil, fmt.Errorf("dial %s over %s: %w", target, transport.Normalize(kind), err)
	}
	return conn, nil
}

func (p *player) send(conn net.Conn, env messages.Envelope) error {
	_, err := p.sendCounted(conn, env)
	return err
}

func (p *player) sendCounted(conn net.Conn, env messages.Envelope) (int, error) {
	data, err := messages.Encode(env)
	if err != nil {
		return 0, err
	}
	if err := conn.SetWriteDeadline(time.Now().Add(p.cfg.Timeout)); err != nil {
		return 0, err
	}
	return conn.Write(data)
}

// decodeCounted reads one length-prefixed Envelope and reports the total wire
// bytes consumed (4-byte prefix + payload). shared/messages.Decode does the same
// framing but discards the size, which is exactly what a throughput measurement
// needs.
func decodeCounted(r io.Reader) (messages.Envelope, int, error) {
	var env messages.Envelope
	var lenBuf [4]byte
	if _, err := io.ReadFull(r, lenBuf[:]); err != nil {
		return env, 0, err
	}
	length := binary.BigEndian.Uint32(lenBuf[:])
	if length > maxFrame {
		return env, 4, fmt.Errorf("message too large: %d bytes", length)
	}
	data := make([]byte, length)
	if _, err := io.ReadFull(r, data); err != nil {
		return env, 4, err
	}
	if err := json.Unmarshal(data, &env); err != nil {
		return env, 4 + int(length), fmt.Errorf("unmarshal envelope: %w", err)
	}
	return env, 4 + int(length), nil
}

// roundTrip sends one request and waits for a response of wantType, skipping
// interleaved frames (an early snapshot, for instance).
func (p *player) roundTrip(conn net.Conn, r io.Reader, reqType messages.MsgType,
	reqPayload any, wantType messages.MsgType, out any) error {
	env, err := messages.NewEnvelope(reqType, reqPayload)
	if err != nil {
		return fmt.Errorf("encode: %w", err)
	}
	if err := p.send(conn, env); err != nil {
		return fmt.Errorf("send: %w", err)
	}
	for i := 0; i < 32; i++ {
		if err := conn.SetReadDeadline(time.Now().Add(p.cfg.Timeout)); err != nil {
			return err
		}
		resp, _, err := decodeCounted(r)
		if err != nil {
			return fmt.Errorf("recv: %w", err)
		}
		if resp.Type != wantType {
			continue
		}
		return messages.UnmarshalPayload(resp.Payload, out)
	}
	return fmt.Errorf("no frame of type %d received", wantType)
}
