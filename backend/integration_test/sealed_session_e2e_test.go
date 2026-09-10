//go:build integration

package integration

import (
	"bufio"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/sealed"
)

// Sealed-session end-to-end coverage against a server configured the way a stock
// server now is: GAMESERVER_SEALED defaults to `require`.
//
// WHY THIS FILE EXISTS. Flipping that default without these tests would have made
// CI cover the sealed path LESS than before, not more: every other test in this
// suite passes `--sealed off` (see startDotnetGameServerWith), so after the flip
// the entire suite would have been exercising a configuration no deployment uses.
// These three tests are the ones that run the default.
//
// They are also the only place in the repository where "requiring encryption
// deprecates JSON" is demonstrated rather than asserted in a comment.

// sealedTestClient is a client that can speak the sealed framing. It is
// deliberately not built on MockClient: MockClient encodes and decodes whole
// frames in one call, and sealing has to happen between the envelope bytes and
// the length prefix.
type sealedTestClient struct {
	conn net.Conn
	r    *bufio.Reader
	out  *sealed.Session // nil until the handshake completes
	in   *sealed.Session
}

func dialSealedTestClient(addr string) (*sealedTestClient, error) {
	conn, err := net.DialTimeout("tcp", addr, 2*time.Second)
	if err != nil {
		return nil, err
	}
	return &sealedTestClient{conn: conn, r: bufio.NewReaderSize(conn, 64*1024)}, nil
}

func (c *sealedTestClient) Close() { _ = c.conn.Close() }

// send writes one envelope, sealing it once the session exists.
func (c *sealedTestClient) send(env messages.Envelope) error {
	body, err := messages.EncodeBody(env)
	if err != nil {
		return err
	}
	if c.out != nil {
		if body, err = c.out.Seal(body); err != nil {
			return err
		}
	}
	var frame []byte
	frame = binary.BigEndian.AppendUint32(frame, uint32(len(body)))
	frame = append(frame, body...)
	_, err = c.conn.Write(frame)
	return err
}

// recv reads one envelope and reports whether the frame arrived sealed. The
// caller asserting on that boolean is the difference between proving the
// handshake succeeded and proving the traffic after it is actually encrypted —
// a handshake that completes and then falls back to cleartext would pass every
// other assertion in this file.
func (c *sealedTestClient) recv(timeout time.Duration) (env messages.Envelope, wasSealed bool, err error) {
	if err = c.conn.SetReadDeadline(time.Now().Add(timeout)); err != nil {
		return env, false, err
	}
	var lenBuf [4]byte
	if _, err = io.ReadFull(c.r, lenBuf[:]); err != nil {
		return env, false, err
	}
	body := make([]byte, binary.BigEndian.Uint32(lenBuf[:]))
	if _, err = io.ReadFull(c.r, body); err != nil {
		return env, false, err
	}
	if c.in != nil {
		wasSealed = len(body) > 0 && body[0] == sealed.Marker
		plain, oerr := c.in.Open(body)
		if oerr != nil {
			return env, wasSealed, fmt.Errorf("open sealed frame: %w", oerr)
		}
		body = plain
	}
	env, err = messages.DecodeBody(body)
	return env, wasSealed, err
}

// handshake runs the client half. joinTokenSecret may be empty, which means "do
// not verify the server's binding" — the shipped-client case.
func (c *sealedTestClient) handshake(joinToken, joinTokenSecret string) (sealed.ClientResult, error) {
	claims, err := jwt.ParseUnverified(joinToken)
	if err != nil {
		return sealed.ClientResult{}, fmt.Errorf("read jti: %w", err)
	}
	res, err := sealed.RunClientHandshake(
		sealed.ClientHandshakeConfig{JTI: claims.Jti, JoinTokenSecret: joinTokenSecret},
		func(pub []byte) error {
			env, eerr := messages.NewEnvelopeAs(messages.EncodingProto,
				messages.MsgSealedClientHello, messages.SealedClientHello{PublicKey: pub})
			if eerr != nil {
				return eerr
			}
			return c.send(env)
		},
		func() ([]byte, []byte, string, error) {
			env, _, rerr := c.recv(5 * time.Second)
			if rerr != nil {
				return nil, nil, "", rerr
			}
			if env.Type != messages.MsgSealedServerHello {
				return nil, nil, "", fmt.Errorf("want server hello, got type %d", env.Type)
			}
			var hello messages.SealedServerHello
			if uerr := env.UnmarshalPayload(&hello); uerr != nil {
				return nil, nil, "", uerr
			}
			return hello.PublicKey, hello.Binding, hello.Error, nil
		},
	)
	if err != nil {
		return res, err
	}
	c.out, c.in = res.Outbound, res.Inbound
	return res, nil
}

func requireDotnet(t *testing.T) {
	t.Helper()
	if _, err := exec.LookPath("dotnet"); err != nil {
		home, _ := os.UserHomeDir()
		if _, err := os.Stat(home + "/.dotnet/dotnet"); err != nil {
			t.Skip("dotnet not found")
		}
	}
}

// joinSealedServer walks gateway auth -> enter world -> game-server join against a
// server spawned with the given extra args, and returns the joined client plus the
// join token. Everything is proto: a JSON client cannot reach a sealed handshake
// at all, which TestSealedSession_RequireServerRefusesJSONClient covers.
func joinSealedServer(t *testing.T, extraArgs []string) (*sealedTestClient, string) {
	t.Helper()

	// SealedDefault, not With: this suite must run the DEFAULT value of
	// GAMESERVER_SEALED, because the default is the thing under test. Passing
	// "--sealed", "require" here would prove only that the flag is wired.
	gsAddr, gsCleanup := startDotnetGameServerSealedDefault(t, extraArgs, nil)
	t.Cleanup(gsCleanup)
	gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
	t.Cleanup(gwCleanup)

	gw, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("connect to gateway: %v", err)
	}
	defer gw.Close()

	userID := "sealed-player"
	token, err := jwt.Sign(userID, dotnetJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}
	authEnv, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgAuth,
		messages.AuthRequest{Token: token})
	if err := gw.Send(authEnv); err != nil {
		t.Fatalf("send auth: %v", err)
	}
	if _, err := gw.Receive(); err != nil {
		t.Fatalf("receive auth response: %v", err)
	}

	enterEnv, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgEnterWorld,
		messages.EnterWorldRequest{MapID: dotnetMapID})
	if err := gw.Send(enterEnv); err != nil {
		t.Fatalf("send enter world: %v", err)
	}
	enterRespEnv, err := gw.Receive()
	if err != nil {
		t.Fatalf("receive enter world response: %v", err)
	}
	var enterResp messages.EnterWorldResponse
	if err := enterRespEnv.UnmarshalPayload(&enterResp); err != nil {
		t.Fatalf("unmarshal enter world response: %v", err)
	}
	if enterResp.ServerAddr == "" || enterResp.JoinToken == "" {
		t.Fatalf("enter world missing addr/token: %+v", enterResp)
	}

	gs, err := dialSealedTestClient(enterResp.ServerAddr)
	if err != nil {
		t.Fatalf("connect to game server: %v", err)
	}
	t.Cleanup(gs.Close)

	joinEnv, _ := messages.NewEnvelopeAs(messages.EncodingProto, messages.MsgJoinToken,
		messages.JoinTokenRequest{Token: enterResp.JoinToken})
	if err := gs.send(joinEnv); err != nil {
		t.Fatalf("send join token: %v", err)
	}
	joinRespEnv, _, err := gs.recv(5 * time.Second)
	if err != nil {
		t.Fatalf("receive join response: %v", err)
	}
	var joinResp messages.JoinTokenResponse
	if err := joinRespEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("join rejected: %s", joinResp.Error)
	}
	return gs, enterResp.JoinToken
}

// TestSealedSession_RequireServerAcceptsSealingClient drives the whole flow against
// a server running the DEFAULT configuration -- no --sealed argument at all, so the
// `require` default in Program.cs is what is under test. If someone flips that
// default back, this test fails at the handshake rather than passing quietly on an
// unencrypted session.
func TestSealedSession_RequireServerAcceptsSealingClient(t *testing.T) {
	requireDotnet(t)

	// No --sealed argument anywhere in this call. If the default in Program.cs is
	// ever moved back to `off`, the server will not run a handshake and this test
	// fails at gs.handshake -- which is the whole reason it does not pass the flag.
	gs, joinToken := joinSealedServer(t, nil)

	res, err := gs.handshake(joinToken, dotnetJoinTokenSecret)
	if err != nil {
		t.Fatalf("sealed handshake: %v", err)
	}
	// This harness holds the join-token secret, so unlike a shipped Unity client it
	// can check that the server proved possession of the same secret. A shipped
	// client structurally cannot (see backend/docs/SEALED-FRAMING.md), which is why
	// this assertion lives here and nowhere in the client.
	if !res.BindingVerified {
		t.Error("binding not verified even though the harness holds the join-token secret")
	}

	if err := gs.send(mustProto(t, messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 1})); err != nil {
		t.Fatalf("send sealed input: %v", err)
	}

	// Read until a snapshot arrives, asserting every frame was sealed on the way.
	var snapshots int
	for i := 0; i < 16 && snapshots == 0; i++ {
		env, wasSealed, err := gs.recv(5 * time.Second)
		if err != nil {
			t.Fatalf("receive after handshake: %v", err)
		}
		if !wasSealed {
			t.Fatalf("frame of type %d arrived UNSEALED after a sealed handshake", env.Type)
		}
		if env.Type == messages.MsgSnapshot {
			snapshots++
		}
	}
	if snapshots == 0 {
		t.Fatal("no snapshot arrived within 16 frames")
	}
	t.Logf("PASS: sealed session established (binding_verified=%v), snapshot received over the sealed channel",
		res.BindingVerified)
}

// TestSealedSession_RequireServerRefusesJSONClient is the executable form of the
// claim that requiring encryption deprecates JSON.
//
// A JSON client is not served in the clear and is not downgraded: the join is
// ACCEPTED (the server answers MsgJoinTokenResp, because it must tell the client
// who it is before it can refuse it for anything else) and then the connection is
// closed with no further frames. That shape matters operationally -- an operator
// reading "join accepted" in a client log and concluding the client works is the
// mistake this test documents.
//
// There is no setting that fixes such a client. The JSON codec has no sealed
// frame, so the only fix is a client that speaks protobuf.
func TestSealedSession_RequireServerRefusesJSONClient(t *testing.T) {
	requireDotnet(t)

	gsAddr, gsCleanup := startDotnetGameServerSealedDefault(t, nil, nil)
	defer gsCleanup()
	gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
	defer gwCleanup()

	gw, err := NewMockClient(gwAddr)
	if err != nil {
		t.Fatalf("connect to gateway: %v", err)
	}
	userID := "json-player"
	token, err := jwt.Sign(userID, dotnetJWTSecret, 5*time.Minute)
	if err != nil {
		t.Fatalf("jwt.Sign: %v", err)
	}
	authEnv, _ := messages.NewEnvelopeAs(messages.EncodingJSON, messages.MsgAuth,
		messages.AuthRequest{Token: token})
	if err := gw.Send(authEnv); err != nil {
		t.Fatalf("send auth: %v", err)
	}
	if _, err := gw.Receive(); err != nil {
		t.Fatalf("receive auth response: %v", err)
	}
	enterEnv, _ := messages.NewEnvelopeAs(messages.EncodingJSON, messages.MsgEnterWorld,
		messages.EnterWorldRequest{MapID: dotnetMapID})
	if err := gw.Send(enterEnv); err != nil {
		t.Fatalf("send enter world: %v", err)
	}
	enterRespEnv, err := gw.Receive()
	if err != nil {
		t.Fatalf("receive enter world response: %v", err)
	}
	var enterResp messages.EnterWorldResponse
	if err := enterRespEnv.UnmarshalPayload(&enterResp); err != nil {
		t.Fatalf("unmarshal enter world response: %v", err)
	}
	gw.Close()

	gs, err := NewMockClient(enterResp.ServerAddr)
	if err != nil {
		t.Fatalf("connect to game server: %v", err)
	}
	defer gs.Close()

	joinEnv, _ := messages.NewEnvelopeAs(messages.EncodingJSON, messages.MsgJoinToken,
		messages.JoinTokenRequest{Token: enterResp.JoinToken})
	if err := gs.Send(joinEnv); err != nil {
		t.Fatalf("send join token: %v", err)
	}
	joinRespEnv, err := gs.Receive()
	if err != nil {
		t.Fatalf("receive join response: %v", err)
	}
	var joinResp messages.JoinTokenResponse
	if err := joinRespEnv.UnmarshalPayload(&joinResp); err != nil {
		t.Fatalf("unmarshal join response: %v", err)
	}
	if !joinResp.OK {
		t.Fatalf("join was rejected outright: %q -- this test asserts the harder shape, "+
			"where the join succeeds and the refusal comes after", joinResp.Error)
	}

	// The refusal: the next read ends the stream. Anything else -- a snapshot, a
	// cleartext frame of any kind -- means the server served a client that cannot
	// encrypt, which is the failure the `require` default exists to prevent.
	env, err := gs.Receive()
	if err == nil {
		t.Fatalf("server sent a type-%d frame to a JSON client on a require server; "+
			"expected the connection to be closed", env.Type)
	}
	if !errors.Is(err, io.EOF) && !errors.Is(err, io.ErrUnexpectedEOF) {
		// A read deadline would also end the stream, but for the wrong reason: it
		// would mean the server kept the connection open and simply said nothing,
		// leaving a client hanging rather than refused.
		t.Fatalf("want EOF (connection closed), got %v", err)
	}
	t.Log("PASS: JSON client joined, then was closed rather than served in the clear")
}

// TestSealedSession_RequireServerRefusesNonSealingProtoClient covers the rollout
// hazard the JSON test does not: a client that speaks protobuf perfectly well and
// simply has not shipped the sealed handshake yet. That is every existing client
// on the day the default flips, so its failure mode is worth pinning.
//
// It must not be served in the clear. The server waits for a ClientHello and closes
// at the handshake deadline.
func TestSealedSession_RequireServerRefusesNonSealingProtoClient(t *testing.T) {
	requireDotnet(t)

	// A short handshake deadline keeps the test fast; the default is 5s and this
	// test would otherwise spend all of it waiting to be closed.
	gs, _ := joinSealedServer(t, []string{"--handshake-timeout-ms", "1000"})

	// No handshake. Behave exactly like a pre-sealing client: send an input and
	// wait for the snapshot stream that a pre-flip server would have started.
	if err := gs.send(mustProto(t, messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 1})); err != nil {
		// A write may also fail once the peer has gone, which is the same refusal.
		t.Logf("input write failed (peer already closed): %v", err)
	}
	if env, _, err := gs.recv(5 * time.Second); err == nil {
		t.Fatalf("server sent a type-%d frame to a client that never sealed", env.Type)
	} else if !errors.Is(err, io.EOF) && !errors.Is(err, io.ErrUnexpectedEOF) {
		t.Fatalf("want EOF (connection closed at the handshake deadline), got %v", err)
	}
	t.Log("PASS: non-sealing protobuf client was closed, not served in the clear")
}

func mustProto(t *testing.T, typ messages.MsgType, payload any) messages.Envelope {
	t.Helper()
	env, err := messages.NewEnvelopeAs(messages.EncodingProto, typ, payload)
	if err != nil {
		t.Fatalf("encode %v: %v", typ, err)
	}
	return env
}
