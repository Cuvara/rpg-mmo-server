// Package transport is the pluggable listen/dial layer for the realtime path
// (client <-> gateway, client <-> game server).
//
// The wire codec in shared/messages is a 4-byte length prefix over an
// io.Reader/io.Writer, so it works unchanged on any net.Conn. This package
// therefore only decides *which* net.Conn the servers get:
//
//	tcp — the default. Reliable, ordered, kernel congestion control.
//	kcp — KCP over UDP (github.com/xtaci/kcp-go/v5). Reliable + ordered like
//	      TCP, but with an ARQ tuned for low latency instead of throughput,
//	      which is what a 10-15Hz authoritative tick loop wants on mobile
//	      networks (head-of-line blocking recovers in ~1 RTT instead of an
//	      RTO backoff).
//
// Business logic never sees the difference: both kinds return a net.Conn and
// a net.Listener, and handlers stay byte-identical.
package transport

import (
	"fmt"
	"net"
	"strings"
	"time"

	kcp "github.com/xtaci/kcp-go/v5"
)

// Supported transport kinds.
const (
	// KindTCP is the default transport.
	KindTCP = "tcp"
	// KindKCP is KCP over UDP.
	KindKCP = "kcp"
)

// KCP tuning constants.
//
// Rationale (see shared/docs/DESIGN.md, 2026-08-04):
//
//   - NoDelay/Interval/Resend/NoCongestion = 1/10/2/1 is kcp-go's documented
//     "turbo" profile: nodelay ARQ on, 10ms internal update tick (matches a
//     10-15Hz server tick — a slower interval would add up to a full tick of
//     jitter), fast retransmit after 2 duplicate ACKs, and congestion control
//     off. Congestion control off is deliberate: the realtime path carries a
//     small, near-constant bitrate (inputs up, AOI snapshots down), so a
//     TCP-style congestion window only ever delays state the client already
//     needs, it never protects the link.
//   - Window 128/128 packets ~= 128 * 1350B ~= 170KB in flight, several
//     seconds of snapshots at MVP sizes. Big enough that the window is never
//     the limit on a bad mobile link, small enough to bound memory per session
//     (~350KB for both directions) at a few thousand CCU per pod.
//   - MTU 1350 is kcp-go's default and stays under the common 1400-1500B path
//     MTU (PPPoE/VPN/mobile carriers) so KCP segments are never IP-fragmented.
//   - FEC off (0 data / 0 parity shards). FEC trades bandwidth for latency on
//     lossy links, but it needs per-game measurement to tune; enabling it
//     blind costs bandwidth for nothing. Revisit with real client telemetry.
//   - No encryption for the MVP. TODO(production): the realtime path must be
//     encrypted before public launch — kcp-go supports pluggable BlockCrypt
//     (e.g. kcp.NewAESBlockCrypt with a per-session key handed out by the
//     gateway in EnterWorldResponse). Until then the join token (short-TTL
//     signed JWT) is the only thing protecting a session.
//   - StreamMode(true) makes a session behave like a byte stream, which is
//     exactly what the length-prefixed codec expects; in message mode a write
//     larger than the MTU would not reassemble the way Decode assumes.
//   - WriteDelay(false) flushes on the next KCP update instead of batching an
//     extra interval — one less tick of added latency per frame.
const (
	KCPNoDelay      = 1    // nodelay ARQ enabled
	KCPInterval     = 10   // internal update interval, ms
	KCPResend       = 2    // fast retransmit after N dup ACKs
	KCPNoCongestion = 1    // 1 = congestion control disabled
	KCPSendWindow   = 128  // send window, packets
	KCPRecvWindow   = 128  // receive window, packets
	KCPMTU          = 1350 // kcp-go default; stays under common path MTUs
	KCPDataShards   = 0    // FEC disabled
	KCPParityShards = 0    // FEC disabled

	// Socket buffers for the shared UDP socket. A single UDP socket multiplexes
	// every KCP session on a listener, so it needs far more room than a
	// per-connection TCP socket or bursts of snapshots get dropped by the kernel.
	KCPSocketBuffer = 4 * 1024 * 1024
)

// Kinds returns every supported transport kind, in preference order.
func Kinds() []string { return []string{KindTCP, KindKCP} }

// Normalize lowercases a transport kind and maps the empty string to KindTCP.
// The empty string is the backward-compatible "unset" value used on the wire
// (EnterWorldResponse.Transport) and in the registry (storage.ServerInfo).
func Normalize(kind string) string {
	k := strings.ToLower(strings.TrimSpace(kind))
	if k == "" {
		return KindTCP
	}
	return k
}

// Validate reports whether kind names a supported transport. The empty string
// is valid and means TCP.
func Validate(kind string) error {
	switch Normalize(kind) {
	case KindTCP, KindKCP:
		return nil
	default:
		return fmt.Errorf("unknown transport %q (want %q or %q)", kind, KindTCP, KindKCP)
	}
}

// Listen starts a listener of the given kind on addr.
//
// Both kinds return a net.Listener whose Accept yields a reliable, ordered
// net.Conn, so callers need no transport-specific code.
func Listen(kind, addr string) (net.Listener, error) {
	switch Normalize(kind) {
	case KindTCP:
		ln, err := net.Listen("tcp", addr)
		if err != nil {
			return nil, fmt.Errorf("listen tcp %s: %w", addr, err)
		}
		return ln, nil
	case KindKCP:
		ln, err := kcp.ListenWithOptions(addr, nil, KCPDataShards, KCPParityShards)
		if err != nil {
			return nil, fmt.Errorf("listen kcp %s: %w", addr, err)
		}
		// Best effort: an undersized socket buffer only costs throughput, and
		// some sandboxes cap SO_RCVBUF/SO_SNDBUF below the request.
		_ = ln.SetReadBuffer(KCPSocketBuffer)
		_ = ln.SetWriteBuffer(KCPSocketBuffer)
		return &kcpListener{Listener: ln}, nil
	default:
		return nil, fmt.Errorf("listen: %w", Validate(kind))
	}
}

// Dial connects to addr over the given transport kind.
//
// timeout bounds the TCP handshake. KCP runs over UDP and has no connection
// handshake, so a KCP dial only fails on a bad address — dialing a dead port
// succeeds and the failure surfaces as a read timeout on the first frame.
// Callers that need liveness must rely on an application-level reply
// (MsgAuthResp / MsgJoinTokenResp) with a read deadline.
func Dial(kind, addr string, timeout time.Duration) (net.Conn, error) {
	switch Normalize(kind) {
	case KindTCP:
		conn, err := net.DialTimeout("tcp", addr, timeout)
		if err != nil {
			return nil, fmt.Errorf("dial tcp %s: %w", addr, err)
		}
		return conn, nil
	case KindKCP:
		sess, err := kcp.DialWithOptions(addr, nil, KCPDataShards, KCPParityShards)
		if err != nil {
			return nil, fmt.Errorf("dial kcp %s: %w", addr, err)
		}
		tuneSession(sess)
		return sess, nil
	default:
		return nil, fmt.Errorf("dial: %w", Validate(kind))
	}
}

// kcpListener adapts *kcp.Listener so every accepted session is tuned with the
// game profile before the caller sees it. kcp.Listener already satisfies
// net.Listener, but its Accept returns an untuned session.
type kcpListener struct {
	*kcp.Listener
}

// Accept returns the next tuned KCP session as a net.Conn.
func (l *kcpListener) Accept() (net.Conn, error) {
	sess, err := l.Listener.AcceptKCP()
	if err != nil {
		return nil, fmt.Errorf("accept kcp: %w", err)
	}
	tuneSession(sess)
	return sess, nil
}

// tuneSession applies the game profile documented on the KCP* constants.
func tuneSession(s *kcp.UDPSession) {
	s.SetStreamMode(true)
	s.SetWriteDelay(false)
	s.SetNoDelay(KCPNoDelay, KCPInterval, KCPResend, KCPNoCongestion)
	s.SetWindowSize(KCPSendWindow, KCPRecvWindow)
	s.SetMtu(KCPMTU)
	s.SetACKNoDelay(true)
}
