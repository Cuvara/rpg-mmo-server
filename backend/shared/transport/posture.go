package transport

import (
	"net"
	"strings"
)

// Cipher names reported by Posture.
const (
	// CipherNone is reported when nothing encrypts the traffic.
	CipherNone = "none"
	// CipherAESCFB is the kcp-go packet-crypt path (see crypto.go).
	CipherAESCFB = "aes-256-cfb"
	// CipherTLS is reported when the listener terminates TLS itself. The exact
	// suite is negotiated per connection and is not knowable at listen time, so
	// this names the layer rather than a cipher — reporting a specific suite
	// here would be a guess, and a confidently wrong security field is the
	// failure this whole type exists to prevent.
	CipherTLS = "tls"
)

// TransportPosture is what a listener actually does to the bytes on the wire,
// as one computed fact rather than as something a reader has to infer from two
// separate configuration values.
//
// Mirrors the C# GameServer.Net.Transport.TransportPosture field for field, so
// the two halves of the backend describe themselves the same way.
//
// # Why this exists
//
// Encryption is off by default twice: the transport defaults to TCP, which has
// no packet-crypt layer at all, and the key defaults to empty. So "is this
// deployment encrypted" is not answerable from either value alone, and the
// combination that answers "no" most emphatically — TCP with no key — is the
// default.
//
// # Encrypted and Authenticated are separate on purpose
//
// The KCP path is AES-256-CFB with a CRC32 (crypto.go). CFB gives
// confidentiality; the CRC32 is a linear checksum and is NOT a MAC, so an
// attacker who can modify datagrams can make controlled changes to the
// plaintext and repair the checksum. Folding both into one "secure" boolean
// would let an operator read "encrypted" as "safe from tampering", which it is
// not. Authenticated is therefore false on every configuration this code can
// currently produce — and it is reported anyway, so that its becoming true is a
// visible event rather than an assumption.
type TransportPosture struct {
	// Transport is the normalised kind: tcp or kcp.
	Transport string
	// KeyConfigured reports that a key was supplied, whether or not it is used.
	KeyConfigured bool
	// TLS reports that this listener terminates TLS in-process (ADR-23).
	//
	// In-process is the only placement this field can honestly describe. A TLS
	// terminator in FRONT of the listener makes the traffic confidential to the
	// terminator and plaintext from there on, and this process cannot see the
	// difference — so an edge-terminated deployment leaves this false, which is
	// the conservative and correct answer for what THIS process does to the
	// bytes.
	TLS bool
	// Encrypted reports that packets leave this process as ciphertext.
	Encrypted bool
	// Authenticated reports that tampering in flight is detectable.
	//
	// It was hard-coded false until ADR-23, with a comment saying so, precisely
	// so that its becoming true would be visible in a diff rather than assumed.
	// TLS is the configuration that makes it true: an AEAD suite with a MAC
	// over every record, unlike the KCP path's CFB-plus-CRC32.
	Authenticated bool
	// Cipher actually in force, or CipherNone.
	Cipher string
	// BindsBeyondLoopback reports that unencrypted traffic would be readable
	// off-host. Reported, never enforced: refusing to start is a separate
	// operational decision.
	BindsBeyondLoopback bool
	// Summary is one line an operator can act on.
	Summary string
}

// KeyIgnored reports the configuration most easily mistaken for working
// encryption: a key is set, and the selected transport cannot use it.
//
// The condition is that the KCP packet-crypt layer is not in force — NOT that
// nothing is encrypted. Since ADR-23 those differ: TCP + TLS + TRANSPORT_KEY is
// encrypted by TLS while the key is still doing nothing, and testing
// `!Encrypted` would have quietly started answering false there. The key being
// ignored is a fact about the key, not about the connection.
func (p TransportPosture) KeyIgnored() bool {
	return p.KeyConfigured && p.Cipher != CipherAESCFB
}

// Posture derives the confidentiality posture from configuration as given, for
// a listener that does NOT terminate TLS. Equivalent to PostureTLS(..., false).
func Posture(kind, key, addr string) TransportPosture {
	return PostureTLS(kind, key, addr, false)
}

// PostureTLS derives the confidentiality posture, including whether this
// listener terminates TLS in-process (ADR-23).
//
// # Why TLS is not just another cipher value
//
// TLS sits ABOVE the transport rather than inside it, so it composes with the
// transport kind instead of replacing it, and it is the only configuration
// here that is authenticated. The KCP packet-crypt path is AES-256-CFB with a
// CRC32 and is explicitly not authenticated (see the type comment); TLS
// records carry a real MAC. Collapsing the two into one "encrypted" boolean
// would lose exactly the distinction this type was created to preserve.
//
// TLS also does not turn a KCP listener into a TLS listener: TLS needs a
// reliable ordered byte stream, so it is only meaningful over TCP. A caller
// that asks for both gets `tlsActive == false` and a summary saying why,
// rather than a silent downgrade — the same treatment a key on TCP already
// gets, and for the same reason.
func PostureTLS(kind, key, addr string, tlsConfigured bool) TransportPosture {
	k := Normalize(kind)
	keyConfigured := strings.TrimSpace(key) != ""

	// TLS is a TCP-only layer. Asking for it on KCP is a configuration error
	// worth naming, not something to silently honour or silently drop.
	tlsActive := tlsConfigured && k == KindTCP

	// Only the KCP path has a packet-crypt layer. A key on TCP is accepted and
	// then does nothing, which is the case worth naming rather than leaving to
	// be discovered.
	kcpEncrypted := k == KindKCP && keyConfigured

	encrypted := tlsActive || kcpEncrypted
	cipher := CipherNone
	switch {
	case tlsActive:
		cipher = CipherTLS
	case kcpEncrypted:
		cipher = CipherAESCFB
	}

	beyond := bindsBeyondLoopback(addr)

	var summary string
	switch {
	case tlsActive:
		// Deliberately says what it does NOT cover. The gateway hop being
		// confidential says nothing about the hop that MINTS the credential it
		// carries, and ADR-23 measured that hop to be plaintext HTTP. An
		// operator reading "ENCRYPTED" here and concluding the client's
		// credentials are protected end to end would be wrong, so the line
		// that tells them they are encrypted also tells them what is left.
		summary = "ENCRYPTED and AUTHENTICATED (TLS terminated in this process) — " +
			"the client<->gateway hop is confidential. This does NOT cover the client<->Nakama meta hop, " +
			"which mints the auth token and is plain HTTP unless separately fronted with TLS (ADR-23)"
	case tlsConfigured && k == KindKCP:
		summary = "PLAINTEXT — TLS was configured but the transport is KCP, which is not a reliable ordered stream; TLS is IGNORED"
	case kcpEncrypted:
		summary = "ENCRYPTED (" + CipherAESCFB + ") but NOT AUTHENTICATED — CFB with a CRC32 is not a MAC, so a modified packet is not detectable"
	case k == KindTCP && keyConfigured:
		summary = "PLAINTEXT — transport is TCP, which has no packet encryption; " + KeyEnvVar + " is set but IGNORED"
	case k == KindTCP:
		summary = "PLAINTEXT — transport is TCP, which has no packet encryption"
	default:
		summary = "PLAINTEXT — transport is KCP but " + KeyEnvVar + " is not set"
	}
	if !encrypted && beyond {
		summary += "; the listener is bound beyond loopback, so this traffic is readable off-host"
	}

	return TransportPosture{
		Transport:           k,
		KeyConfigured:       keyConfigured,
		TLS:                 tlsActive,
		Encrypted:           encrypted,
		Authenticated:       tlsActive, // TLS records carry a MAC; the KCP CFB+CRC32 path does not
		Cipher:              cipher,
		BindsBeyondLoopback: beyond,
		Summary:             summary,
	}
}

// bindsBeyondLoopback reports whether addr puts the listener anywhere other
// than loopback.
//
// A wildcard bind — ":9000", "0.0.0.0:9000", "[::]:9000" — counts as beyond
// loopback because it accepts on every interface. That is the shape the
// container images use, so treating "no host part" as safe would exempt exactly
// the deployments this reporting exists for. Anything unparseable also counts:
// the honest default for a security posture is the pessimistic one.
func bindsBeyondLoopback(addr string) bool {
	a := strings.TrimSpace(addr)
	if a == "" {
		return true
	}

	host := a
	if h, _, err := net.SplitHostPort(a); err == nil {
		host = h
	}
	host = strings.Trim(strings.TrimSpace(host), "[]")

	switch host {
	case "", "*", "0.0.0.0", "::":
		return true
	}
	if strings.EqualFold(host, "localhost") {
		return false
	}
	ip := net.ParseIP(host)
	return ip == nil || !ip.IsLoopback()
}
