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
	// Encrypted reports that packets leave this process as ciphertext.
	Encrypted bool
	// Authenticated reports that tampering in flight is detectable. False on
	// every configuration currently reachable — see the type comment.
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
func (p TransportPosture) KeyIgnored() bool { return p.KeyConfigured && !p.Encrypted }

// Posture derives the confidentiality posture from configuration as given.
func Posture(kind, key, addr string) TransportPosture {
	k := Normalize(kind)
	keyConfigured := strings.TrimSpace(key) != ""

	// Only the KCP path has a packet-crypt layer. A key on TCP is accepted and
	// then does nothing, which is the case worth naming rather than leaving to
	// be discovered.
	encrypted := k == KindKCP && keyConfigured
	cipher := CipherNone
	if encrypted {
		cipher = CipherAESCFB
	}

	beyond := bindsBeyondLoopback(addr)

	var summary string
	switch {
	case encrypted:
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
		Encrypted:           encrypted,
		Authenticated:       false, // see the type comment; hard-coded so a change is visible in a diff
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
