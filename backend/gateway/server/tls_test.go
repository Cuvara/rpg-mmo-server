package server

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/binary"
	"encoding/pem"
	"io"
	"log/slog"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/gateway/registry"
	"github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// Gateway-hop TLS (ADR-23).
//
// The claim under test is not "the gateway starts with a certificate". It is
// that the auth token — a one-hour reusable bearer credential, measured
// crossing this hop in the clear — is no longer readable by something sitting
// in the path. So these tests read the bytes off a tapped socket and look for
// the token, which is the only form of the claim that can be wrong.

const (
	tlsTestJWTSecret  = "tls-test-secret-key-for-gateway"
	tlsTestJoinSecret = "tls-test-join-secret-32chars-min"
)

// ---------------------------------------------------------------- test certs

// writeTestCert generates a self-signed localhost certificate and returns the
// two PEM paths. Self-signed is correct here: these tests are about whether the
// bytes on the wire are ciphertext, not about chain validation, and the client
// side pins this exact certificate rather than skipping verification.
func writeTestCert(t *testing.T) (certPath, keyPath string, pool *x509.CertPool) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}
	tmpl := x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "localhost"},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(24 * time.Hour),
		KeyUsage:              x509.KeyUsageDigitalSignature | x509.KeyUsageCertSign,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		DNSNames:              []string{"localhost"},
		IPAddresses:           []net.IP{net.ParseIP("127.0.0.1"), net.ParseIP("::1")},
		IsCA:                  true,
		BasicConstraintsValid: true,
	}
	der, err := x509.CreateCertificate(rand.Reader, &tmpl, &tmpl, &key.PublicKey, key)
	if err != nil {
		t.Fatalf("create certificate: %v", err)
	}
	dir := t.TempDir()
	certPath = filepath.Join(dir, "cert.pem")
	keyPath = filepath.Join(dir, "key.pem")

	if werr := os.WriteFile(certPath, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), 0o600); werr != nil {
		t.Fatalf("write cert: %v", werr)
	}
	keyDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatalf("marshal key: %v", err)
	}
	if werr := os.WriteFile(keyPath, pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER}), 0o600); werr != nil {
		t.Fatalf("write key: %v", werr)
	}

	pool = x509.NewCertPool()
	cert, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatalf("parse certificate: %v", err)
	}
	pool.AddCert(cert)
	return certPath, keyPath, pool
}

// ------------------------------------------------------------------ the tap

// authTap is a transparent relay recording everything crossing it, the same
// instrument as integration_test/hop_confidentiality_tap_test.go reduced to
// what one test needs.
type authTap struct {
	ln  net.Listener
	buf []byte
	mu  chan struct{} // 1-slot semaphore; simpler than a mutex for this size
}

func startAuthTap(t *testing.T, upstream string) *authTap {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("tap listen: %v", err)
	}
	tp := &authTap{ln: ln, mu: make(chan struct{}, 1)}
	tp.mu <- struct{}{}
	go func() {
		for {
			down, aerr := ln.Accept()
			if aerr != nil {
				return
			}
			go tp.relay(down, upstream)
		}
	}()
	t.Cleanup(func() { _ = ln.Close() })
	return tp
}

func (tp *authTap) addr() string { return tp.ln.Addr().String() }

func (tp *authTap) relay(down net.Conn, upstream string) {
	defer down.Close()
	up, err := net.DialTimeout("tcp", upstream, 5*time.Second)
	if err != nil {
		return
	}
	defer up.Close()
	cp := func(dst io.Writer, src io.Reader) {
		b := make([]byte, 16*1024)
		for {
			n, rerr := src.Read(b)
			if n > 0 {
				<-tp.mu
				tp.buf = append(tp.buf, b[:n]...)
				tp.mu <- struct{}{}
				if _, werr := dst.Write(b[:n]); werr != nil {
					return
				}
			}
			if rerr != nil {
				return
			}
		}
	}
	go cp(up, down)
	cp(down, up)
}

func (tp *authTap) captured() []byte {
	<-tp.mu
	out := append([]byte(nil), tp.buf...)
	tp.mu <- struct{}{}
	return out
}

// ------------------------------------------------------------------ fixtures

func startTLSTestGateway(t *testing.T, opts ...Option) (addr string) {
	t.Helper()
	sessions := session.NewSessionManager(storage.NewMemorySessionStore())
	reg := registry.NewRegistryService(storage.NewMemoryServerRegistry())
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))

	base := []Option{WithJoinTokenSecret(tlsTestJoinSecret)}
	gw := New(sessions, reg, tlsTestJWTSecret, logger, append(base, opts...)...)
	go func() { _ = gw.Run("127.0.0.1:0") }()
	t.Cleanup(gw.Shutdown)

	for i := 0; i < 200; i++ {
		if a := gw.Addr(); a != "" {
			return a
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("gateway did not bind")
	return ""
}

// sendAuth writes one MsgAuth frame and reads the reply, over whatever net.Conn
// it is handed — plaintext socket or TLS connection alike.
func sendAuth(t *testing.T, conn net.Conn, token string) messages.AuthResponse {
	t.Helper()
	env, err := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgAuth,
		messages.AuthRequest{Token: token})
	if err != nil {
		t.Fatalf("build auth envelope: %v", err)
	}
	frame, err := messages.Encode(env)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	if _, werr := conn.Write(frame); werr != nil {
		t.Fatalf("write auth: %v", werr)
	}
	if derr := conn.SetReadDeadline(time.Now().Add(5 * time.Second)); derr != nil {
		t.Fatalf("set deadline: %v", derr)
	}
	var lenBuf [4]byte
	if _, rerr := io.ReadFull(conn, lenBuf[:]); rerr != nil {
		t.Fatalf("read length: %v", rerr)
	}
	body := make([]byte, binary.BigEndian.Uint32(lenBuf[:]))
	if _, rerr := io.ReadFull(conn, body); rerr != nil {
		t.Fatalf("read body: %v", rerr)
	}
	respEnv, err := messages.DecodeBody(body)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}
	var resp messages.AuthResponse
	if uerr := respEnv.UnmarshalPayload(&resp); uerr != nil {
		t.Fatalf("unmarshal: %v", uerr)
	}
	return resp
}

func mustToken(t *testing.T) string {
	t.Helper()
	tok, err := jwt.Sign("tls-test-player", tlsTestJWTSecret, time.Hour)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	return tok
}

// ------------------------------------------------------------------ the tests

// TestGatewayTLS_BeforeAndAfter is the acceptance test for ADR-23, and it is
// deliberately one test rather than two: the "before" leg is what makes the
// "after" leg mean anything. A test that only ran the TLS case would pass
// identically against a tap that was silently recording nothing.
func TestGatewayTLS_BeforeAndAfter(t *testing.T) {
	token := mustToken(t)

	t.Run("before: plaintext gateway leaks the auth token", func(t *testing.T) {
		gwAddr := startTLSTestGateway(t)
		tap := startAuthTap(t, gwAddr)

		conn, err := net.DialTimeout("tcp", tap.addr(), 5*time.Second)
		if err != nil {
			t.Fatalf("dial: %v", err)
		}
		defer conn.Close()

		if resp := sendAuth(t, conn, token); !resp.OK {
			t.Fatalf("auth failed: %s", resp.Error)
		}
		time.Sleep(100 * time.Millisecond)

		if !strings.Contains(string(tap.captured()), token) {
			t.Fatal("auth token NOT found in the plaintext capture — the tap is not observing the hop, " +
				"so the 'after' leg below would prove nothing")
		}
		t.Log("auth token readable in the clear on the plaintext gateway hop (the measured state)")
	})

	t.Run("after: TLS gateway does not", func(t *testing.T) {
		certPath, keyPath, pool := writeTestCert(t)
		tlsConf, err := LoadTLSConfig(certPath, keyPath)
		if err != nil {
			t.Fatalf("LoadTLSConfig: %v", err)
		}
		gwAddr := startTLSTestGateway(t, WithTLS(tlsConf))
		tap := startAuthTap(t, gwAddr)

		// RootCAs, not InsecureSkipVerify: a client that accepts any certificate
		// gets confidentiality against a passive observer and nothing against an
		// active one, which is the very trade ADR-23 rejects as Option A. Writing
		// the test the lazy way would quietly endorse the client doing the same.
		conn, err := tls.Dial("tcp", tap.addr(), &tls.Config{
			RootCAs:    pool,
			ServerName: "localhost",
			MinVersion: tls.VersionTLS12,
		})
		if err != nil {
			t.Fatalf("tls dial: %v", err)
		}
		defer conn.Close()

		if resp := sendAuth(t, conn, token); !resp.OK {
			t.Fatalf("auth over TLS failed: %s", resp.Error)
		}
		time.Sleep(100 * time.Millisecond)

		captured := tap.captured()
		if len(captured) == 0 {
			t.Fatal("tap captured nothing; it is not in the path and this proves nothing")
		}
		if strings.Contains(string(captured), token) {
			t.Fatalf("auth token STILL readable in a %d-byte capture of a TLS session", len(captured))
		}
		t.Logf("auth token not readable in %d captured bytes; the same request over the same tap", len(captured))
	})
}

// TestGatewayTLS_NoPlaintextFallback pins decision 3 of ADR-23: a listener with
// a certificate serves TLS only.
//
// The failure this prevents is not theoretical — "accept both and sniff the
// first byte" is a natural-looking convenience, and it is a downgrade attack
// with a friendly name. The gateway already has byte-0 encoding sniffing
// (JSON vs protobuf), so the shape is right there to copy by mistake.
func TestGatewayTLS_NoPlaintextFallback(t *testing.T) {
	certPath, keyPath, _ := writeTestCert(t)
	tlsConf, err := LoadTLSConfig(certPath, keyPath)
	if err != nil {
		t.Fatalf("LoadTLSConfig: %v", err)
	}
	gwAddr := startTLSTestGateway(t, WithTLS(tlsConf))

	conn, err := net.DialTimeout("tcp", gwAddr, 5*time.Second)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	// A plaintext protobuf MsgAuth. To a TLS listener this is a malformed
	// record and the connection must die, not be served.
	env, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgAuth,
		messages.AuthRequest{Token: mustToken(t)})
	frame, _ := messages.Encode(env)
	_, _ = conn.Write(frame)

	if derr := conn.SetReadDeadline(time.Now().Add(5 * time.Second)); derr != nil {
		t.Fatalf("set deadline: %v", derr)
	}
	var lenBuf [4]byte
	_, rerr := io.ReadFull(conn, lenBuf[:])
	if rerr == nil {
		t.Fatal("a plaintext client got a reply from a TLS listener — the gateway is accepting a downgrade")
	}
	t.Logf("plaintext client refused by the TLS listener: %v", rerr)
}

// TestLoadTLSConfig covers the half-configured cases, which are the ones that
// matter operationally: a typo in one of two paths must not silently produce
// the plaintext listener the operator was trying to remove.
func TestLoadTLSConfig(t *testing.T) {
	certPath, keyPath, _ := writeTestCert(t)

	t.Run("neither set is no TLS", func(t *testing.T) {
		cfg, err := LoadTLSConfig("", "")
		if err != nil || cfg != nil {
			t.Fatalf("got (%v, %v), want (nil, nil)", cfg, err)
		}
	})
	t.Run("whitespace counts as unset", func(t *testing.T) {
		cfg, err := LoadTLSConfig("  ", "\t")
		if err != nil || cfg != nil {
			t.Fatalf("got (%v, %v), want (nil, nil)", cfg, err)
		}
	})
	t.Run("cert without key is an error", func(t *testing.T) {
		if _, err := LoadTLSConfig(certPath, ""); err == nil {
			t.Fatal("half-configured TLS was accepted; it must not fall back to plaintext")
		}
	})
	t.Run("key without cert is an error", func(t *testing.T) {
		if _, err := LoadTLSConfig("", keyPath); err == nil {
			t.Fatal("half-configured TLS was accepted; it must not fall back to plaintext")
		}
	})
	t.Run("missing file is an error", func(t *testing.T) {
		if _, err := LoadTLSConfig(filepath.Join(t.TempDir(), "nope.pem"), keyPath); err == nil {
			t.Fatal("a nonexistent certificate path was accepted")
		}
	})
	t.Run("both set loads and pins a TLS floor", func(t *testing.T) {
		cfg, err := LoadTLSConfig(certPath, keyPath)
		if err != nil {
			t.Fatalf("LoadTLSConfig: %v", err)
		}
		if len(cfg.Certificates) != 1 {
			t.Fatalf("got %d certificates, want 1", len(cfg.Certificates))
		}
		if cfg.MinVersion < tls.VersionTLS12 {
			t.Errorf("MinVersion = %#x, want at least TLS 1.2", cfg.MinVersion)
		}
	})
}

// TestGatewayTLS_RefusedOnKCP pins that a certificate on a KCP listener is a
// startup failure rather than a silently plaintext gateway that reports itself
// as configured for TLS.
func TestGatewayTLS_RefusedOnKCP(t *testing.T) {
	certPath, keyPath, _ := writeTestCert(t)
	tlsConf, err := LoadTLSConfig(certPath, keyPath)
	if err != nil {
		t.Fatalf("LoadTLSConfig: %v", err)
	}
	sessions := session.NewSessionManager(storage.NewMemorySessionStore())
	reg := registry.NewRegistryService(storage.NewMemoryServerRegistry())
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	gw := New(sessions, reg, tlsTestJWTSecret, logger,
		WithJoinTokenSecret(tlsTestJoinSecret), WithTransport("kcp"), WithTLS(tlsConf))
	t.Cleanup(gw.Shutdown)

	// Run in a goroutine with a deadline, not inline. Run BLOCKS in its accept
	// loop on success, so an inline call turns "the guard was removed" into a
	// hang that eats the package timeout instead of a failure that names this
	// test. That is not hypothetical: it is what this test did when the guard
	// was mutated out to check that it could fail.
	errCh := make(chan error, 1)
	go func() { errCh <- gw.Run("127.0.0.1:0") }()

	select {
	case err = <-errCh:
		if err == nil {
			t.Fatal("gateway started with TLS on a KCP listener; it would serve plaintext while configured for TLS")
		}
		if !strings.Contains(err.Error(), "TLS") {
			t.Errorf("error does not name TLS, so an operator cannot act on it: %v", err)
		}
		t.Logf("refused: %v", err)
	case <-time.After(5 * time.Second):
		t.Fatal("gateway did not refuse TLS-on-KCP; it is serving, which means it is serving PLAINTEXT while configured for TLS")
	}
}
