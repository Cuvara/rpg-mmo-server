package transport

import (
	"errors"
	"fmt"
	"net"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
)

// allKinds is the transport matrix every behavioural test runs against.
var allKinds = []string{KindTCP, KindKCP}

// echoServer listens on a free loopback port of the given kind and echoes every
// length-prefixed Envelope back to the sender. It returns the dialable address.
func echoServer(t *testing.T, kind string) string {
	t.Helper()
	ln, err := Listen(kind, "127.0.0.1:0")
	if err != nil {
		t.Fatalf("Listen(%s): %v", kind, err)
	}
	t.Cleanup(func() { ln.Close() })

	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			go func(c net.Conn) {
				defer c.Close()
				for {
					c.SetReadDeadline(time.Now().Add(10 * time.Second))
					env, err := messages.Decode(c)
					if err != nil {
						return
					}
					data, err := messages.Encode(env)
					if err != nil {
						return
					}
					c.SetWriteDeadline(time.Now().Add(10 * time.Second))
					if _, err := c.Write(data); err != nil {
						return
					}
				}
			}(conn)
		}
	}()
	return ln.Addr().String()
}

// roundTrip sends one envelope and reads the echo back.
func roundTrip(conn net.Conn, env messages.Envelope) (messages.Envelope, error) {
	data, err := messages.Encode(env)
	if err != nil {
		return messages.Envelope{}, err
	}
	if err := conn.SetWriteDeadline(time.Now().Add(10 * time.Second)); err != nil {
		return messages.Envelope{}, err
	}
	if _, err := conn.Write(data); err != nil {
		return messages.Envelope{}, err
	}
	if err := conn.SetReadDeadline(time.Now().Add(10 * time.Second)); err != nil {
		return messages.Envelope{}, err
	}
	return messages.Decode(conn)
}

func TestNormalize(t *testing.T) {
	tests := []struct {
		name string
		in   string
		want string
	}{
		{"empty means tcp", "", KindTCP},
		{"tcp", "tcp", KindTCP},
		{"kcp", "kcp", KindKCP},
		{"uppercase", "KCP", KindKCP},
		{"padded", "  tcp  ", KindTCP},
		{"unknown passes through", "sctp", "sctp"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := Normalize(tt.in); got != tt.want {
				t.Errorf("Normalize(%q) = %q, want %q", tt.in, got, tt.want)
			}
		})
	}
}

func TestValidate(t *testing.T) {
	tests := []struct {
		name    string
		in      string
		wantErr bool
	}{
		{"empty is tcp", "", false},
		{"tcp", "tcp", false},
		{"kcp", "kcp", false},
		{"uppercase kcp", "KCP", false},
		{"unknown", "quic", true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := Validate(tt.in)
			if (err != nil) != tt.wantErr {
				t.Fatalf("Validate(%q) error = %v, wantErr %v", tt.in, err, tt.wantErr)
			}
		})
	}
}

func TestKinds(t *testing.T) {
	got := Kinds()
	if len(got) != 2 || got[0] != KindTCP || got[1] != KindKCP {
		t.Errorf("Kinds() = %v, want [tcp kcp]", got)
	}
}

// TestRoundTrip proves the shared/messages codec works unchanged over every
// transport kind.
func TestRoundTrip(t *testing.T) {
	for _, kind := range allKinds {
		t.Run(kind, func(t *testing.T) {
			addr := echoServer(t, kind)
			conn, err := Dial(kind, addr, 2*time.Second)
			if err != nil {
				t.Fatalf("Dial(%s, %s): %v", kind, addr, err)
			}
			defer conn.Close()

			sent, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: "hello-" + kind})
			if err != nil {
				t.Fatalf("NewEnvelope: %v", err)
			}
			got, err := roundTrip(conn, sent)
			if err != nil {
				t.Fatalf("roundTrip: %v", err)
			}
			if got.Type != messages.MsgAuth {
				t.Errorf("type = %d, want %d", got.Type, messages.MsgAuth)
			}
			var req messages.AuthRequest
			if err := got.UnmarshalPayload(&req); err != nil {
				t.Fatalf("UnmarshalPayload: %v", err)
			}
			if req.Token != "hello-"+kind {
				t.Errorf("token = %q, want %q", req.Token, "hello-"+kind)
			}
		})
	}
}

// TestSequentialFrames proves framing holds across many small frames on one
// connection (stream mode, no message boundaries lost).
func TestSequentialFrames(t *testing.T) {
	const frames = 50
	for _, kind := range allKinds {
		t.Run(kind, func(t *testing.T) {
			addr := echoServer(t, kind)
			conn, err := Dial(kind, addr, 2*time.Second)
			if err != nil {
				t.Fatalf("Dial: %v", err)
			}
			defer conn.Close()

			for i := 0; i < frames; i++ {
				want := fmt.Sprintf("frame-%d", i)
				env, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: want})
				if err != nil {
					t.Fatalf("NewEnvelope: %v", err)
				}
				got, err := roundTrip(conn, env)
				if err != nil {
					t.Fatalf("frame %d: %v", i, err)
				}
				var req messages.AuthRequest
				if err := got.UnmarshalPayload(&req); err != nil {
					t.Fatalf("frame %d unmarshal: %v", i, err)
				}
				if req.Token != want {
					t.Fatalf("frame %d token = %q, want %q", i, req.Token, want)
				}
			}
		})
	}
}

// TestConcurrentConnections proves one listener serves many simultaneous
// clients — for KCP that means many sessions multiplexed on one UDP socket.
func TestConcurrentConnections(t *testing.T) {
	const clients = 16
	for _, kind := range allKinds {
		t.Run(kind, func(t *testing.T) {
			addr := echoServer(t, kind)

			var wg sync.WaitGroup
			errCh := make(chan error, clients)
			for i := 0; i < clients; i++ {
				wg.Add(1)
				go func(i int) {
					defer wg.Done()
					conn, err := Dial(kind, addr, 2*time.Second)
					if err != nil {
						errCh <- fmt.Errorf("client %d dial: %w", i, err)
						return
					}
					defer conn.Close()

					want := fmt.Sprintf("client-%d", i)
					env, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: want})
					if err != nil {
						errCh <- err
						return
					}
					got, err := roundTrip(conn, env)
					if err != nil {
						errCh <- fmt.Errorf("client %d roundTrip: %w", i, err)
						return
					}
					var req messages.AuthRequest
					if err := got.UnmarshalPayload(&req); err != nil {
						errCh <- err
						return
					}
					if req.Token != want {
						errCh <- fmt.Errorf("client %d token = %q, want %q", i, req.Token, want)
					}
				}(i)
			}
			wg.Wait()
			close(errCh)
			for err := range errCh {
				t.Error(err)
			}
		})
	}
}

// TestLargePayload sends a payload several times the KCP MTU, proving
// fragmentation + reassembly keeps the length-prefixed framing intact.
func TestLargePayload(t *testing.T) {
	payload := strings.Repeat("x", 8*KCPMTU) // ~10.8KB, 8+ KCP segments
	for _, kind := range allKinds {
		t.Run(kind, func(t *testing.T) {
			addr := echoServer(t, kind)
			conn, err := Dial(kind, addr, 2*time.Second)
			if err != nil {
				t.Fatalf("Dial: %v", err)
			}
			defer conn.Close()

			env, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: payload})
			if err != nil {
				t.Fatalf("NewEnvelope: %v", err)
			}
			got, err := roundTrip(conn, env)
			if err != nil {
				t.Fatalf("roundTrip: %v", err)
			}
			var req messages.AuthRequest
			if err := got.UnmarshalPayload(&req); err != nil {
				t.Fatalf("UnmarshalPayload: %v", err)
			}
			if len(req.Token) != len(payload) {
				t.Fatalf("echoed %d bytes, want %d", len(req.Token), len(payload))
			}
			if req.Token != payload {
				t.Error("echoed payload differs from the one sent")
			}
		})
	}
}

// TestDialDeadPort documents the one behavioural difference between the kinds:
// TCP fails at dial time, KCP (connectionless UDP) only fails on the first
// read, so callers must rely on an application-level reply plus a deadline.
func TestDialDeadPort(t *testing.T) {
	// A port nobody listens on: bind then release it.
	probe, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("probe listen: %v", err)
	}
	dead := probe.Addr().String()
	probe.Close()

	t.Run("tcp fails at dial", func(t *testing.T) {
		conn, err := Dial(KindTCP, dead, 500*time.Millisecond)
		if err == nil {
			conn.Close()
			t.Fatal("Dial(tcp) to a dead port should fail")
		}
	})

	t.Run("kcp fails at first read", func(t *testing.T) {
		conn, err := Dial(KindKCP, dead, 500*time.Millisecond)
		if err != nil {
			t.Fatalf("Dial(kcp) should succeed (UDP is connectionless): %v", err)
		}
		defer conn.Close()

		env, err := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: "t"})
		if err != nil {
			t.Fatalf("NewEnvelope: %v", err)
		}
		data, _ := messages.Encode(env)
		if _, err := conn.Write(data); err != nil {
			t.Fatalf("write to dead kcp peer should be accepted locally: %v", err)
		}
		conn.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
		if _, err := messages.Decode(conn); err == nil {
			t.Fatal("read from a dead KCP peer should time out")
		} else if !isTimeout(err) {
			t.Logf("read failed with a non-timeout error (acceptable): %v", err)
		}
	})
}

func TestListenInvalidKind(t *testing.T) {
	if _, err := Listen("quic", "127.0.0.1:0"); err == nil {
		t.Error("Listen with an unknown kind should fail")
	}
	if _, err := Dial("quic", "127.0.0.1:1", time.Second); err == nil {
		t.Error("Dial with an unknown kind should fail")
	}
}

// TestListenAddrIsDialable proves Addr() on both listeners returns something a
// client can dial back — servers publish it into the registry.
func TestListenAddrIsDialable(t *testing.T) {
	for _, kind := range allKinds {
		t.Run(kind, func(t *testing.T) {
			addr := echoServer(t, kind)
			host, port, err := net.SplitHostPort(addr)
			if err != nil {
				t.Fatalf("SplitHostPort(%q): %v", addr, err)
			}
			if host != "127.0.0.1" || port == "0" {
				t.Fatalf("addr = %q, want a concrete 127.0.0.1:<port>", addr)
			}
		})
	}
}

func isTimeout(err error) bool {
	var ne net.Error
	return errors.As(err, &ne) && ne.Timeout()
}
