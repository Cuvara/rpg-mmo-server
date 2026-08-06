package transport

import (
	"bytes"
	"encoding/hex"
	"errors"
	"net"
	"os"
	"strings"
	"testing"
	"time"
)

const (
	testKeyHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
	otherKey   = "a-different-passphrase"
)

func TestDeriveKey(t *testing.T) {
	tests := []struct {
		name    string
		key     string
		wantErr bool
		// wantRaw is set when the key must be decoded verbatim rather than
		// stretched.
		wantRaw string
	}{
		{name: "64 hex chars decode verbatim", key: testKeyHex, wantRaw: testKeyHex},
		{name: "uppercase hex decodes verbatim", key: strings.ToUpper(testKeyHex), wantRaw: strings.ToUpper(testKeyHex)},
		{name: "passphrase is stretched", key: "correct horse battery staple"},
		{name: "short passphrase is stretched", key: "x"},
		{name: "64 non-hex chars fall back to passphrase", key: strings.Repeat("z", 64)},
		{name: "surrounding whitespace trimmed", key: "  " + testKeyHex + "  ", wantRaw: testKeyHex},
		{name: "empty key is an error", key: "", wantErr: true},
		{name: "whitespace-only key is an error", key: "   ", wantErr: true},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := DeriveKey(tt.key)
			if tt.wantErr {
				if err == nil {
					t.Fatalf("DeriveKey(%q) should fail", tt.key)
				}
				return
			}
			if err != nil {
				t.Fatalf("DeriveKey(%q) error: %v", tt.key, err)
			}
			if len(got) != aesKeySize {
				t.Fatalf("DeriveKey() len = %d, want %d (AES-256)", len(got), aesKeySize)
			}
			if tt.wantRaw != "" {
				want, derr := hex.DecodeString(strings.TrimSpace(tt.wantRaw))
				if derr != nil {
					t.Fatalf("bad test fixture: %v", derr)
				}
				if !bytes.Equal(got, want) {
					t.Errorf("hex key was not decoded verbatim")
				}
			}
		})
	}
}

func TestDeriveKeyIsDeterministicAndDistinct(t *testing.T) {
	a1, _ := DeriveKey("passphrase-a")
	a2, _ := DeriveKey("passphrase-a")
	b, _ := DeriveKey("passphrase-b")
	if !bytes.Equal(a1, a2) {
		t.Error("DeriveKey must be deterministic — both peers derive independently")
	}
	if bytes.Equal(a1, b) {
		t.Error("different passphrases must derive different keys")
	}
}

func TestBlockCryptEmptyKeyIsPlaintext(t *testing.T) {
	bc, err := blockCrypt("")
	if err != nil {
		t.Fatalf("blockCrypt(\"\") error: %v", err)
	}
	if bc != nil {
		t.Error("empty key must yield a nil BlockCrypt (plaintext)")
	}
}

// roundtrip sends one frame from a dialer to a listener and reports whether it
// arrived intact within the deadline.
func roundtrip(t *testing.T, lnKey, dialKey string) error {
	t.Helper()

	ln, err := Listen(KindKCP, "127.0.0.1:0", WithKey(lnKey))
	if err != nil {
		t.Fatalf("Listen: %v", err)
	}
	defer ln.Close()

	accepted := make(chan []byte, 1)
	accErr := make(chan error, 1)
	go func() {
		conn, aerr := ln.Accept()
		if aerr != nil {
			accErr <- aerr
			return
		}
		defer conn.Close()
		_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
		buf := make([]byte, 64)
		n, rerr := conn.Read(buf)
		if rerr != nil {
			accErr <- rerr
			return
		}
		accepted <- buf[:n]
	}()

	conn, err := Dial(KindKCP, ln.Addr().String(), 2*time.Second, WithKey(dialKey))
	if err != nil {
		t.Fatalf("Dial: %v", err)
	}
	defer conn.Close()

	payload := []byte("hello-realtime")
	if _, err := conn.Write(payload); err != nil {
		t.Fatalf("Write: %v", err)
	}

	select {
	case got := <-accepted:
		if !bytes.Equal(got, payload) {
			return errors.New("payload corrupted in transit")
		}
		return nil
	case err := <-accErr:
		return err
	case <-time.After(2500 * time.Millisecond):
		// A key mismatch has no error channel: the receiver decrypts garbage,
		// drops it as a malformed KCP segment, and the frame simply never
		// arrives. Silence IS the failure signal here.
		return errors.New("timeout: no frame arrived")
	}
}

func TestKCPEncryptionRoundtrip(t *testing.T) {
	tests := []struct {
		name    string
		lnKey   string
		dialKey string
		wantOK  bool
	}{
		{name: "plaintext listener + plaintext dialer", lnKey: "", dialKey: "", wantOK: true},
		{name: "encrypted listener + matching key", lnKey: testKeyHex, dialKey: testKeyHex, wantOK: true},
		{name: "encrypted with passphrase + matching passphrase", lnKey: "shared-pass", dialKey: "shared-pass", wantOK: true},
		// The three that must fail closed:
		{name: "encrypted listener + plaintext dialer", lnKey: testKeyHex, dialKey: "", wantOK: false},
		{name: "plaintext listener + encrypted dialer", lnKey: "", dialKey: testKeyHex, wantOK: false},
		{name: "mismatched keys", lnKey: testKeyHex, dialKey: otherKey, wantOK: false},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := roundtrip(t, tt.lnKey, tt.dialKey)
			if tt.wantOK && err != nil {
				t.Errorf("roundtrip should succeed, got: %v", err)
			}
			if !tt.wantOK && err == nil {
				t.Error("roundtrip should FAIL — a peer without the right key must never be able to talk to the listener")
			}
		})
	}
}

func TestTCPIgnoresKey(t *testing.T) {
	// A transport key is meaningless for TCP (TLS or the cluster network is the
	// answer there); passing one must not break the listener.
	ln, err := Listen(KindTCP, "127.0.0.1:0", WithKey(testKeyHex))
	if err != nil {
		t.Fatalf("Listen tcp: %v", err)
	}
	defer ln.Close()

	go func() {
		conn, aerr := ln.Accept()
		if aerr == nil {
			_, _ = conn.Write([]byte("ok"))
			conn.Close()
		}
	}()

	conn, err := Dial(KindTCP, ln.Addr().String(), 2*time.Second, WithKey("totally-different"))
	if err != nil {
		t.Fatalf("Dial tcp: %v", err)
	}
	defer conn.Close()
	_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 8)
	n, err := conn.Read(buf)
	if err != nil {
		t.Fatalf("Read: %v", err)
	}
	if string(buf[:n]) != "ok" {
		t.Errorf("got %q, want %q — TCP must ignore the transport key", buf[:n], "ok")
	}
}

func TestListenRejectsUnusableKey(t *testing.T) {
	// A whitespace-only key is neither "unset" nor usable; it must fail at
	// start-up rather than silently downgrade to plaintext... except that
	// WithKey trims it to "", which IS the documented "unset" spelling.
	ln, err := Listen(KindKCP, "127.0.0.1:0", WithKey("   "))
	if err != nil {
		t.Fatalf("whitespace key trims to unset, Listen should succeed: %v", err)
	}
	defer ln.Close()
	if Encrypted(WithKey("   ")) {
		t.Error("a whitespace-only key must be treated as unset, not as encryption")
	}
	if !Encrypted(WithKey(testKeyHex)) {
		t.Error("a real key must report Encrypted() == true")
	}
}

func TestKeyEnvVarName(t *testing.T) {
	// Pins the env var name the deploy manifests and docs reference.
	if KeyEnvVar != "TRANSPORT_KEY" {
		t.Errorf("KeyEnvVar = %q, want TRANSPORT_KEY", KeyEnvVar)
	}
	if _, set := os.LookupEnv(KeyEnvVar); set {
		t.Logf("note: %s is set in this environment", KeyEnvVar)
	}
}

// compile-time assertions that the option-carrying signatures still satisfy the
// interfaces callers rely on.
var (
	_ func(string, string, ...Option) (net.Listener, error)            = Listen
	_ func(string, string, time.Duration, ...Option) (net.Conn, error) = Dial
)
