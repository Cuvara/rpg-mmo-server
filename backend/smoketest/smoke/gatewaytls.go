package smoke

import (
	"crypto/tls"
	"crypto/x509"
	"encoding/pem"
	"fmt"
	"net"
	"os"
	"time"
)

// wrapGatewayTLS upgrades an already-connected gateway socket to TLS, verifying
// the server's certificate against a PINNED copy (ADR-23).
//
// Why a pin and not the trust store: the dev and staging gateways present a
// self-signed certificate, so chain validation cannot succeed. The choice is
// therefore between pinning and skipping, and skipping is the thing that makes a
// misconfigured hop indistinguishable from a working one. A byte-for-byte
// comparison is STRICTER than the trust store -- a certificate signed by any CA
// on earth is refused unless it is this exact one.
//
// InsecureSkipVerify is true here and that is not what it sounds like: it
// disables the DEFAULT verifier so VerifyPeerCertificate below can be the only
// one that decides, which is how Go expresses "replace verification", not
// "remove it". The C# client does the same thing with its PinValidator, which
// also ignores the platform's own chain result.
func wrapGatewayTLS(conn net.Conn, pemPath, serverName string, timeout time.Duration) (net.Conn, error) {
	pinned, err := loadPinnedCertificate(pemPath)
	if err != nil {
		return nil, err
	}

	tlsConn := tls.Client(conn, &tls.Config{
		ServerName:         serverName,
		MinVersion:         tls.VersionTLS12,
		InsecureSkipVerify: true, //nolint:gosec // replaced by the pin below, not removed
		VerifyPeerCertificate: func(rawCerts [][]byte, _ [][]*x509.Certificate) error {
			if len(rawCerts) == 0 {
				return fmt.Errorf("gateway presented no certificate")
			}
			// The LEAF only. A pin that accepted a match anywhere in the chain
			// would accept a certificate issued by the pinned one, which is a
			// different guarantee from the one this claims to make.
			if !bytesEqual(rawCerts[0], pinned) {
				return fmt.Errorf(
					"gateway certificate does not match the pin (presented %d bytes, pinned %d)",
					len(rawCerts[0]), len(pinned))
			}
			return nil
		},
	})

	if err := tlsConn.SetDeadline(time.Now().Add(timeout)); err != nil {
		return nil, fmt.Errorf("set tls handshake deadline: %w", err)
	}
	if err := tlsConn.Handshake(); err != nil {
		return nil, fmt.Errorf("gateway tls handshake: %w", err)
	}
	// Cleared so the caller's own per-operation deadlines apply, as they do on a
	// plaintext connection.
	if err := tlsConn.SetDeadline(time.Time{}); err != nil {
		return nil, fmt.Errorf("clear tls handshake deadline: %w", err)
	}
	return tlsConn, nil
}

// loadPinnedCertificate reads a PEM file and returns the raw DER of its first
// CERTIFICATE block -- the same bytes TLS puts on the wire, so the comparison is
// of wire bytes and not of a parsed, re-encoded approximation.
func loadPinnedCertificate(path string) ([]byte, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read pinned certificate %s: %w", path, err)
	}
	for block, rest := pem.Decode(raw); block != nil; block, rest = pem.Decode(rest) {
		if block.Type == "CERTIFICATE" {
			return block.Bytes, nil
		}
	}
	// Refusing rather than falling back to plaintext: a caller that asked for a
	// pin and got no usable one must not quietly connect in the clear.
	return nil, fmt.Errorf("no CERTIFICATE block in %s", path)
}

func bytesEqual(a, b []byte) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}
