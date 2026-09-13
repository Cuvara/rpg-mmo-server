package smoke

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// The pin is the whole point: a gateway presenting a DIFFERENT certificate must
// be refused even when that certificate is perfectly valid. Chain validation
// cannot make that call -- both are self-signed -- so if the pin were not doing
// the work, both cases below would pass and the check would be decorative.
func TestGatewayTLSPin(t *testing.T) {
	dir := t.TempDir()
	serverCert, serverPEM := selfSigned(t, "the-gateway")
	_, otherPEM := selfSigned(t, "not-the-gateway")

	rightPin := write(t, dir, "right.crt", serverPEM)
	wrongPin := write(t, dir, "wrong.crt", otherPEM)

	tests := []struct {
		name    string
		pin     string
		wantErr string // empty = the handshake must succeed
	}{
		{name: "the pinned certificate is accepted", pin: rightPin},
		{name: "a different valid certificate is refused", pin: wrongPin, wantErr: "does not match the pin"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			ln, err := tls.Listen("tcp", "127.0.0.1:0", &tls.Config{
				Certificates: []tls.Certificate{serverCert},
				MinVersion:   tls.VersionTLS12,
			})
			if err != nil {
				t.Fatalf("listen: %v", err)
			}
			defer ln.Close()

			go func() {
				c, aerr := ln.Accept()
				if aerr != nil {
					return
				}
				// Force the handshake so a refusal on the client side is a
				// handshake refusal and not a later read error.
				_ = c.(*tls.Conn).Handshake()
				_ = c.Close()
			}()

			raw, err := net.DialTimeout("tcp", ln.Addr().String(), 5*time.Second)
			if err != nil {
				t.Fatalf("dial: %v", err)
			}
			defer raw.Close()

			conn, err := wrapGatewayTLS(raw, tt.pin, "127.0.0.1", 5*time.Second)
			switch {
			case tt.wantErr == "":
				if err != nil {
					t.Fatalf("the pinned certificate was refused: %v", err)
				}
				_ = conn.Close()
			default:
				if err == nil {
					_ = conn.Close()
					t.Fatal("a certificate that does not match the pin was ACCEPTED")
				}
				if !strings.Contains(err.Error(), tt.wantErr) {
					t.Fatalf("error %q does not mention %q -- a refusal nobody can attribute is "+
						"as expensive as no refusal", err, tt.wantErr)
				}
			}
		})
	}
}

// A caller that asked for a pin and supplied something unusable must be refused,
// never quietly connected in the clear.
func TestLoadPinnedCertificateRefusesJunk(t *testing.T) {
	dir := t.TempDir()

	tests := []struct {
		name, body, want string
	}{
		{name: "missing file", body: "", want: "read pinned certificate"},
		{name: "not PEM", body: "this is not a certificate", want: "no CERTIFICATE block"},
		{name: "PEM of the wrong type", body: "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n", want: "no CERTIFICATE block"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			path := filepath.Join(dir, tt.name+".pem")
			if tt.body != "" {
				write(t, dir, tt.name+".pem", []byte(tt.body))
			}
			_, err := loadPinnedCertificate(path)
			if err == nil {
				t.Fatal("junk was accepted as a pin")
			}
			if !strings.Contains(err.Error(), tt.want) {
				t.Fatalf("error %q does not mention %q", err, tt.want)
			}
		})
	}
}

// --- helpers ---------------------------------------------------------------

func selfSigned(t *testing.T, cn string) (tls.Certificate, []byte) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("key: %v", err)
	}
	tmpl := &x509.Certificate{
		SerialNumber: big.NewInt(time.Now().UnixNano()),
		Subject:      pkix.Name{CommonName: cn},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(time.Hour),
		IPAddresses:  []net.IP{net.ParseIP("127.0.0.1")},
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &key.PublicKey, key)
	if err != nil {
		t.Fatalf("cert: %v", err)
	}
	return tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key},
		pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
}

func write(t *testing.T, dir, name string, body []byte) string {
	t.Helper()
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, body, 0o600); err != nil {
		t.Fatalf("write %s: %v", path, err)
	}
	return path
}
