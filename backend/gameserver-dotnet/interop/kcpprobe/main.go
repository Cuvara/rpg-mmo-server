// Command kcpprobe is a Go client harness that proves the C# game server's KCP
// listener is wire-compatible with github.com/xtaci/kcp-go/v5.
//
// It deliberately dials through backend/shared/transport rather than kcp-go
// directly: the thing under test is not "does some KCP implementation talk to
// another", it is "does a client configured exactly the way this project's Go
// clients are configured reach the C# server". Any drift in the tuning profile,
// the encryption key derivation, or the framing shows up here.
//
// Modes:
//
//	derivekey <key>              print the hex AES-256 key TRANSPORT_KEY derives to
//	echo <addr> <key> <payload>  dial, send one length-prefixed frame, print the echo
//	join <addr> <key> <token>    full game join: MsgJoinToken -> MsgInput -> MsgSnapshot
//
// An empty key means plaintext. Every mode exits non-zero on failure and prints
// a single-line result, so a test harness can assert on it.
package main

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// dialTimeout bounds the (nonexistent) KCP handshake. KCP over UDP has no
// connection setup, so this only guards address resolution.
const dialTimeout = 5 * time.Second

// ioTimeout bounds every read. A key mismatch has no error channel — the peer's
// datagrams simply never assemble — so a timeout IS the failure signal.
const ioTimeout = 5 * time.Second

func main() {
	if len(os.Args) < 2 {
		fatal("usage: kcpprobe <derivekey|echo|join> ...")
	}

	switch os.Args[1] {
	case "derivekey":
		requireArgs(3, "derivekey <key>")
		key, err := transport.DeriveKey(os.Args[2])
		if err != nil {
			fatal("derive: %v", err)
		}
		fmt.Println(hex.EncodeToString(key))
	case "echo":
		requireArgs(5, "echo <addr> <key> <payload>")
		runEcho(os.Args[2], os.Args[3], os.Args[4])
	case "join":
		requireArgs(5, "join <addr> <key> <token>")
		runJoin(os.Args[2], os.Args[3], os.Args[4])
	default:
		fatal("unknown mode %q", os.Args[1])
	}
}

func requireArgs(n int, usage string) {
	if len(os.Args) < n {
		fatal("usage: kcpprobe %s", usage)
	}
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "FAIL: "+format+"\n", args...)
	os.Exit(1)
}

// runEcho sends one raw frame and prints whatever comes back. Used for the
// low-level framing check, before any game semantics are involved.
func runEcho(addr, key, payload string) {
	conn, err := transport.Dial(transport.KindKCP, addr, dialTimeout, transport.WithKey(key))
	if err != nil {
		fatal("dial: %v", err)
	}
	defer conn.Close()

	if _, err := conn.Write([]byte(payload)); err != nil {
		fatal("write: %v", err)
	}

	_ = conn.SetReadDeadline(time.Now().Add(ioTimeout))
	buf := make([]byte, 4096)
	n, err := conn.Read(buf)
	if err != nil {
		fatal("read: %v", err)
	}
	fmt.Printf("ECHO %s\n", buf[:n])
}

// runJoin performs the real client sequence against the C# game server: join
// token, then inputs, then snapshots. It is the end-to-end proof that the KCP
// session carries the length-prefixed JSON codec unchanged.
func runJoin(addr, key, token string) {
	conn, err := transport.Dial(transport.KindKCP, addr, dialTimeout, transport.WithKey(key))
	if err != nil {
		fatal("dial: %v", err)
	}
	defer conn.Close()

	// Step 1: MsgJoinToken.
	env, err := messages.NewEnvelope(messages.MsgJoinToken, messages.JoinTokenRequest{Token: token})
	if err != nil {
		fatal("encode join: %v", err)
	}
	if err := writeEnvelope(conn, env); err != nil {
		fatal("write join: %v", err)
	}

	// Step 2: MsgJoinTokenResp.
	_ = conn.SetReadDeadline(time.Now().Add(ioTimeout))
	resp, err := messages.Decode(conn)
	if err != nil {
		fatal("read join resp: %v", err)
	}
	if resp.Type != messages.MsgJoinTokenResp {
		fatal("expected MsgJoinTokenResp, got type %d payload %s", resp.Type, resp.Payload)
	}
	var jr messages.JoinTokenResponse
	if err := json.Unmarshal(resp.Payload, &jr); err != nil {
		fatal("decode join resp: %v", err)
	}
	if !jr.OK {
		fatal("join rejected: %s", jr.Error)
	}
	fmt.Printf("JOINED user=%s\n", jr.UserID)

	// Step 3: drive a few input ticks and collect snapshots. Several ticks, not
	// one: a single exchange could pass on a broken ARQ that only ever delivers
	// the first segment.
	deadline := time.Now().Add(10 * time.Second)
	snapshots := 0
	moved := false

	for tick := uint64(1); tick <= 8; tick++ {
		in, err := messages.NewEnvelope(messages.MsgInput, messages.InputMessage{
			Tick: tick, MoveX: 1, MoveY: 0,
		})
		if err != nil {
			fatal("encode input: %v", err)
		}
		if err := writeEnvelope(conn, in); err != nil {
			fatal("write input %d: %v", tick, err)
		}
		time.Sleep(80 * time.Millisecond)
	}

	var lastX float32
	var firstX float32
	haveFirst := false

	for time.Now().Before(deadline) && snapshots < 12 {
		_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
		env, err := messages.Decode(conn)
		if err != nil {
			break
		}
		if env.Type != messages.MsgSnapshot {
			continue
		}
		var snap messages.SnapshotMessage
		if err := json.Unmarshal(env.Payload, &snap); err != nil {
			fatal("decode snapshot: %v", err)
		}
		snapshots++
		for _, e := range snap.Entities {
			if e.ID != jr.UserID {
				continue
			}
			if !haveFirst {
				firstX, haveFirst = e.X, true
			}
			lastX = e.X
		}
		if haveFirst && lastX != firstX {
			moved = true
		}
	}

	if snapshots == 0 {
		fatal("no snapshots received over KCP")
	}
	fmt.Printf("SNAPSHOTS %d moved=%v x=%v\n", snapshots, moved, lastX)
	fmt.Println("OK")
}

func writeEnvelope(conn interface{ Write([]byte) (int, error) }, env messages.Envelope) error {
	frame, err := messages.Encode(env)
	if err != nil {
		return err
	}
	_, err = conn.Write(frame)
	return err
}
