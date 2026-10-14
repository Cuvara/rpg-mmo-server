package main

import (
	"errors"
	"fmt"
	"net"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

const testKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

// fakeGameServer is a KCP listener standing in for the game server's join step.
// reply decides what (if anything) is written back after MsgJoinToken arrives.
// The received join token is sent on tokens so a test can prove the probe
// actually carried the gateway's token over KCP.
func fakeGameServer(t *testing.T, key string, reply func(c net.Conn) error) (addr string, tokens <-chan string) {
	t.Helper()
	ln, err := transport.Listen(transport.KindKCP, "127.0.0.1:0", transport.WithKey(key))
	if err != nil {
		t.Fatalf("listen kcp: %v", err)
	}
	t.Cleanup(func() { _ = ln.Close() })
	got := make(chan string, 1)
	go func() {
		c, err := ln.Accept()
		if err != nil {
			return
		}
		defer c.Close()
		_ = c.SetReadDeadline(time.Now().Add(5 * time.Second))
		env, err := messages.Decode(c)
		if err != nil || env.Type != messages.MsgJoinToken {
			return
		}
		var req messages.JoinTokenRequest
		if err := env.UnmarshalPayload(&req); err != nil {
			return
		}
		got <- req.Token
		if reply != nil {
			_ = reply(c)
		}
		// Keep the session open past the probe's deadline so "no reply" is a
		// silence, not a close.
		time.Sleep(time.Second)
	}()
	return ln.Addr().String(), got
}

func writeEnvelope(c net.Conn, typ messages.MsgType, payload any) error {
	env, err := messages.NewEnvelope(typ, payload)
	if err != nil {
		return err
	}
	data, err := messages.Encode(env)
	if err != nil {
		return err
	}
	_, err = c.Write(data)
	return err
}

func replyJoin(resp messages.JoinTokenResponse) func(net.Conn) error {
	return func(c net.Conn) error { return writeEnvelope(c, messages.MsgJoinTokenResp, resp) }
}

// TestKCPJoinOutcomes drives kcpJoin against a real KCP listener on loopback and
// pins the one rule verify.sh depends on: kcp_join=ok is printed only after a
// MsgJoinTokenResp from the server that is an acceptance or the sealed-encoding
// refusal. Silence, a wrong key, other frames, other refusals and an undecodable
// reply are all non-ok.
func TestKCPJoinOutcomes(t *testing.T) {
	cases := []struct {
		name      string
		serverKey string
		reply     func(net.Conn) error
		want      string // exact prefix of the result
		wantToken bool   // the server must have received the join token
	}{
		{
			name:      "accepted",
			serverKey: testKey,
			reply:     replyJoin(messages.JoinTokenResponse{OK: true, UserID: "u1"}),
			want:      "kcp_join=ok join_reply=accepted",
			wantToken: true,
		},
		{
			name:      "sealed refusal counts as ok",
			serverKey: testKey,
			reply:     replyJoin(messages.JoinTokenResponse{OK: false, Error: sealedRefusal}),
			want:      "kcp_join=ok join_reply=refused_encoding_cannot_seal",
			wantToken: true,
		},
		{
			name:      "other refusal is rejected",
			serverKey: testKey,
			reply:     replyJoin(messages.JoinTokenResponse{OK: false, Error: "Server is full"}),
			want:      `kcp_join=rejected kcp_error="Server is full"`,
			wantToken: true,
		},
		{
			name:      "refusal without a reason is rejected",
			serverKey: testKey,
			reply:     replyJoin(messages.JoinTokenResponse{OK: false}),
			want:      `kcp_join=rejected kcp_error=""`,
			wantToken: true,
		},
		{
			name:      "no reply is a timeout",
			serverKey: testKey,
			reply:     nil,
			want:      "kcp_join=timeout",
			wantToken: true,
		},
		{
			name:      "only other frames is a timeout",
			serverKey: testKey,
			reply: func(c net.Conn) error {
				return writeEnvelope(c, messages.MsgSnapshot, map[string]any{"tick": 1})
			},
			want:      "kcp_join=timeout",
			wantToken: true,
		},
		{
			name:      "undecodable reply is an error",
			serverKey: testKey,
			reply: func(c net.Conn) error {
				data, err := messages.Encode(messages.Envelope{
					Type: messages.MsgJoinTokenResp, Payload: []byte(`"not an object"`),
				})
				if err != nil {
					return err
				}
				_, err = c.Write(data)
				return err
			},
			want:      "kcp_join=error",
			wantToken: true,
		},
		{
			// The server cannot decrypt the probe's datagrams, so it never sees a
			// session: the only symptom on the probe side is the read deadline.
			name:      "wrong transport key is a timeout",
			serverKey: strings.Repeat("f", 64),
			reply:     replyJoin(messages.JoinTokenResponse{OK: true}),
			want:      "kcp_join=timeout",
			wantToken: false,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			addr, tokens := fakeGameServer(t, tc.serverKey, tc.reply)
			cfg := config{transportKey: testKey, timeout: 500 * time.Millisecond}
			enter := messages.EnterWorldResponse{
				ServerAddr: addr, Transport: transport.KindKCP, JoinToken: "join-token-" + tc.name,
			}
			got := kcpJoin(cfg, enter)
			if !strings.HasPrefix(got, tc.want) {
				t.Fatalf("kcpJoin = %q, want prefix %q", got, tc.want)
			}
			if !strings.HasPrefix(tc.want, "kcp_join=ok") && strings.Contains(got, "kcp_join=ok") {
				t.Fatalf("kcpJoin = %q: printed ok without an accepted reply", got)
			}
			select {
			case tok := <-tokens:
				if !tc.wantToken {
					t.Fatalf("server received a join token with a mismatched key: %q", tok)
				}
				if tok != enter.JoinToken {
					t.Fatalf("server received token %q, want %q", tok, enter.JoinToken)
				}
			default:
				if tc.wantToken {
					t.Fatal("server never received MsgJoinToken")
				}
			}
		})
	}
}

// TestKCPJoinDialError covers the one failure before any datagram is sent: an
// advertised address that cannot be dialled at all.
func TestKCPJoinDialError(t *testing.T) {
	got := kcpJoin(config{timeout: 200 * time.Millisecond},
		messages.EnterWorldResponse{ServerAddr: "not-an-address", Transport: transport.KindKCP})
	if !strings.HasPrefix(got, "kcp_join=error") {
		t.Fatalf("kcpJoin = %q, want kcp_join=error", got)
	}
}

type timeoutErr struct{}

func (timeoutErr) Error() string   { return "i/o timeout" }
func (timeoutErr) Timeout() bool   { return true }
func (timeoutErr) Temporary() bool { return true }

// TestJoinOutcome pins the classification table without a network.
func TestJoinOutcome(t *testing.T) {
	cases := []struct {
		name string
		resp messages.JoinTokenResponse
		err  error
		want string
	}{
		{"accepted", messages.JoinTokenResponse{OK: true}, nil, "kcp_join=ok join_reply=accepted"},
		{"sealed refusal", messages.JoinTokenResponse{Error: sealedRefusal}, nil, "kcp_join=ok join_reply=refused_encoding_cannot_seal"},
		{"other refusal", messages.JoinTokenResponse{Error: "invalid join token"}, nil, `kcp_join=rejected kcp_error="invalid join token"`},
		// A recv error wins over whatever the struct holds: ok must never come
		// from a reply that was not received.
		{"timeout", messages.JoinTokenResponse{OK: true}, fmt.Errorf("recv: %w", timeoutErr{}), `kcp_join=timeout kcp_error="recv: i/o timeout"`},
		{"deadline exceeded", messages.JoinTokenResponse{}, fmt.Errorf("recv: %w", os.ErrDeadlineExceeded), "kcp_join=timeout"},
		{"closed", messages.JoinTokenResponse{OK: true}, errors.New("recv: read length: EOF"), `kcp_join=error kcp_error="recv: read length: EOF"`},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := joinOutcome(tc.resp, tc.err)
			if !strings.HasPrefix(got, tc.want) {
				t.Fatalf("joinOutcome = %q, want prefix %q", got, tc.want)
			}
		})
	}
}
