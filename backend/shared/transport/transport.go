// Package transport is the listen/dial layer for the two framed hops a client
// opens (client <-> gateway, client <-> game server).
//
// The wire codec in shared/messages is a 4-byte length prefix over an
// io.Reader/io.Writer, so it works unchanged on any net.Conn. This package
// only decides *which* net.Conn each hop gets, and the answer is fixed per hop:
//
//	kcp — KCP over UDP (github.com/xtaci/kcp-go/v5). The ONLY realtime
//	      gameplay transport (client <-> game server). Reliable + ordered, with
//	      an ARQ tuned for low latency instead of throughput, which is what a
//	      10-15Hz authoritative tick loop wants on mobile networks (head-of-line
//	      blocking recovers in ~1 RTT instead of an RTO backoff).
//	tcp — the gateway hop only (auth + redirect, optionally TLS, ADR-23). It is
//	      never a gameplay transport and there is no fallback from KCP to it.
//
// Gameplay callers go through ValidateGameplay / DialGameplay, which refuse
// anything but kcp — including the empty string, which used to mean TCP.
// Business logic never sees the transport: both kinds return a net.Conn and a
// net.Listener, and handlers stay byte-identical.
package transport

import (
	"errors"
	"fmt"
	"log/slog"
	"net"
	"strings"
	"time"

	kcp "github.com/xtaci/kcp-go/v5"
)

// Supported transport kinds.
const (
	// KindTCP is the gateway hop's transport. Never a gameplay transport.
	KindTCP = "tcp"
	// KindKCP is KCP over UDP, the only realtime gameplay transport.
	KindKCP = "kcp"

	// Gameplay is the transport every client <-> game server connection uses.
	// It is what the game server registers, what the gateway hands out in
	// EnterWorldResponse.Transport, and the only value a gameplay dialer accepts.
	Gameplay = KindKCP
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
//   - Encryption is opt-in via a pre-shared key (WithKey / TRANSPORT_KEY),
//     applied as kcp-go's AES-256 BlockCrypt. Empty key = plaintext, which is
//     the dev default and logs a warning on every KCP listener. See crypto.go
//     for key derivation and shared/docs/DESIGN.md for the PSK-vs-per-session
//     tradeoff.
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

// Kinds returns every kind Listen and Dial accept. KindTCP is in the list for
// the gateway hop only; a gameplay hop accepts Gameplay and nothing else.
func Kinds() []string { return []string{KindKCP, KindTCP} }

// Normalize lowercases and trims a transport kind. It no longer maps the empty
// string to anything: an unset transport is an error for every caller, never
// a silent default.
func Normalize(kind string) string {
	return strings.ToLower(strings.TrimSpace(kind))
}

// Validate reports whether kind names a transport Listen and Dial support.
// The empty string is invalid.
func Validate(kind string) error {
	switch Normalize(kind) {
	case KindTCP, KindKCP:
		return nil
	default:
		return fmt.Errorf("unknown transport %q (want %q or %q)", kind, KindKCP, KindTCP)
	}
}

// ErrNotGameplayTransport is wrapped by ValidateGameplay for any transport
// other than Gameplay.
var ErrNotGameplayTransport = errors.New("realtime gameplay transport must be " + Gameplay)

// ValidateGameplay reports whether kind is the gameplay transport. Only "kcp"
// (any case) passes: an empty value — a registry entry or EnterWorldResponse
// with no transport — and "tcp" are errors, so a misconfigured server or
// gateway surfaces as a failed join that names the problem instead of a
// connection over the wrong protocol.
func ValidateGameplay(kind string) error {
	switch k := Normalize(kind); k {
	case Gameplay:
		return nil
	case "":
		return fmt.Errorf("%w: no transport advertised", ErrNotGameplayTransport)
	default:
		return fmt.Errorf("%w: got %q", ErrNotGameplayTransport, k)
	}
}

// DialGameplay dials a game server. advertised is the transport the gateway
// returned in EnterWorldResponse.Transport; anything but Gameplay is refused
// before a packet is sent, and there is no fallback to another transport.
//
// A KCP dial has no handshake (see Dial), so a dead or filtered UDP port is
// only detected by the caller's read deadline on the first reply
// (MsgJoinTokenResp).
func DialGameplay(advertised, addr string, timeout time.Duration, opts ...Option) (net.Conn, error) {
	if err := ValidateGameplay(advertised); err != nil {
		return nil, fmt.Errorf("dial game server %s: %w", addr, err)
	}
	return Dial(Gameplay, addr, timeout, opts...)
}

// options is the resolved set of Option values for one Listen/Dial call.
type options struct {
	key    string
	logger *slog.Logger
}

// Option customises a Listen or Dial call. Options that do not apply to the
// chosen kind are ignored (a transport key is meaningless for TCP, which is
// expected to be wrapped in TLS or run inside the cluster network instead).
type Option func(*options)

// WithKey sets the pre-shared key that encrypts a KCP listener or dial.
//
// Both peers must be given the same key: KCP block crypto has no negotiation,
// so a mismatch (including one side unset) is a hard failure — the receiver
// simply never assembles a valid segment. The empty string keeps the connection
// in plaintext.
func WithKey(key string) Option {
	return func(o *options) { o.key = strings.TrimSpace(key) }
}

// WithLogger overrides the logger used for the unencrypted-listener warning.
// Defaults to slog.Default().
func WithLogger(l *slog.Logger) Option {
	return func(o *options) { o.logger = l }
}

func resolveOptions(opts []Option) options {
	o := options{logger: slog.Default()}
	for _, fn := range opts {
		if fn != nil {
			fn(&o)
		}
	}
	if o.logger == nil {
		o.logger = slog.Default()
	}
	return o
}

// Encrypted reports whether a set of options turns on transport encryption.
func Encrypted(opts ...Option) bool { return resolveOptions(opts).key != "" }

// Listen starts a listener of the given kind on addr.
//
// Both kinds return a net.Listener whose Accept yields a reliable, ordered
// net.Conn, so callers need no transport-specific code.
//
// With WithKey set and kind=kcp, every datagram is AES-256 encrypted. Without
// it a KCP listener logs a WARN: the realtime path carries the join token and
// gameplay state in cleartext over UDP, which is acceptable for local dev and
// not for anything reachable from the internet.
func Listen(kind, addr string, opts ...Option) (net.Listener, error) {
	o := resolveOptions(opts)
	switch Normalize(kind) {
	case KindTCP:
		ln, err := net.Listen("tcp", addr)
		if err != nil {
			return nil, fmt.Errorf("listen tcp %s: %w", addr, err)
		}
		return &tcpNoDelayListener{Listener: ln}, nil
	case KindKCP:
		bc, err := blockCrypt(o.key)
		if err != nil {
			return nil, fmt.Errorf("listen kcp %s: %w", addr, err)
		}
		if bc == nil {
			o.logger.Warn("KCP listener is UNENCRYPTED — join tokens and gameplay traffic are in cleartext; set "+KeyEnvVar+" (32-byte hex) before exposing this port",
				"addr", addr, "transport", KindKCP)
		}
		ln, err := kcp.ListenWithOptions(addr, bc, KCPDataShards, KCPParityShards)
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
//
// A KCP dial with WithKey encrypts every datagram; the key must match the
// listener's or nothing the dialer sends is ever assembled into a session.
func Dial(kind, addr string, timeout time.Duration, opts ...Option) (net.Conn, error) {
	o := resolveOptions(opts)
	switch Normalize(kind) {
	case KindTCP:
		conn, err := net.DialTimeout("tcp", addr, timeout)
		if err != nil {
			return nil, fmt.Errorf("dial tcp %s: %w", addr, err)
		}
		// Explicit NoDelay — Go defaults to true, but state it for parity
		// with the C# game server and to guard against future changes.
		if tc, ok := conn.(*net.TCPConn); ok {
			_ = tc.SetNoDelay(true)
		}
		return conn, nil
	case KindKCP:
		bc, err := blockCrypt(o.key)
		if err != nil {
			return nil, fmt.Errorf("dial kcp %s: %w", addr, err)
		}
		sess, err := kcp.DialWithOptions(addr, bc, KCPDataShards, KCPParityShards)
		if err != nil {
			return nil, fmt.Errorf("dial kcp %s: %w", addr, err)
		}
		tuneSession(sess)
		return sess, nil
	default:
		return nil, fmt.Errorf("dial: %w", Validate(kind))
	}
}

// tcpNoDelayListener wraps a TCP listener to set NoDelay on every accepted
// connection. Go defaults to NoDelay=true, but stating it explicitly documents
// the intent, matches the C# game server, and guards against future changes.
type tcpNoDelayListener struct {
	net.Listener
}

// Accept returns the next TCP connection with Nagle disabled.
func (l *tcpNoDelayListener) Accept() (net.Conn, error) {
	conn, err := l.Listener.Accept()
	if err != nil {
		return nil, err
	}
	if tc, ok := conn.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}
	return conn, nil
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
