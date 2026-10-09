//go:build integration

package integration

import (
	"context"
	"io"
	"log/slog"
	"net"
	"strconv"
	"testing"
	"time"

	gwregistry "github.com/duycuong/rpg-mmo/gateway/registry"
	gwserver "github.com/duycuong/rpg-mmo/gateway/server"
	gwsession "github.com/duycuong/rpg-mmo/gateway/session"
	"github.com/duycuong/rpg-mmo/shared/messages"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/duycuong/rpg-mmo/shared/transport"
)

// KCP-only gameplay, end to end against the real C# game server.
//
// The full join flow itself (gateway auth -> enter world -> KCP join -> input ->
// authoritative snapshot) is TestDotnetInterop_FullFlow, which now runs over
// KCP because there is no other game-server transport. These tests cover what
// that flow does not: the advertised transport, a server restart on the same
// UDP port, hostile datagrams on the shared UDP socket, and the refusal of a
// server that does not advertise kcp.

// TestKCP_GatewayAdvertisesKCP pins the contract a real client relies on: the
// gateway hands out transport "kcp" and a UDP endpoint the client can join on.
func TestKCP_GatewayAdvertisesKCP(t *testing.T) {
	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()
	gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
	defer gwCleanup()

	enc := messages.EncodingProto
	gw := dialAndAuth(t, gwAddr, "kcp-advertise", enc)
	enter := enterWorldE2E(t, gw, enc)
	gw.Close()

	if enter.Transport != transport.Gameplay {
		t.Fatalf("EnterWorldResponse.Transport = %q, want %q", enter.Transport, transport.Gameplay)
	}
	if enter.ServerAddr != gsAddr {
		t.Fatalf("ServerAddr = %q, want the game server's UDP endpoint %q", enter.ServerAddr, gsAddr)
	}

	gs, err := NewGameClientFor(enter)
	if err != nil {
		t.Fatalf("dial advertised endpoint over KCP: %v", err)
	}
	defer gs.Close()
	joinGameServer(t, gs, enter.JoinToken, enc, "kcp-advertise")
	snap := waitForSnapshot(t, gs, "kcp-advertise")
	t.Logf("joined over KCP/UDP %s, first snapshot tick=%d", enter.ServerAddr, snap.Tick)
}

// TestKCP_GatewayRefusesNonKCPServer proves there is no TCP path: a registry
// entry advertising tcp (or nothing) is refused by name and no address or join
// token is handed out.
func TestKCP_GatewayRefusesNonKCPServer(t *testing.T) {
	for _, advertised := range []string{"tcp", ""} {
		t.Run("advertised="+strconv.Quote(advertised), func(t *testing.T) {
			reg := storage.NewMemoryServerRegistry()
			if err := reg.Register(context.Background(), storage.ServerInfo{
				ServerID: dotnetServerID, MapID: dotnetMapID, Addr: "127.0.0.1:9", Transport: advertised, Capacity: 100,
			}); err != nil {
				t.Fatalf("register: %v", err)
			}
			logger := slog.New(slog.NewTextHandler(io.Discard, nil))
			gw := gwserver.New(gwsession.NewSessionManager(storage.NewMemorySessionStore()),
				gwregistry.NewRegistryService(reg), dotnetJWTSecret, logger,
				gwserver.WithJoinTokenSecret(dotnetJoinTokenSecret))
			go gw.Run("127.0.0.1:0")
			defer gw.Shutdown()
			var gwAddr string
			for i := 0; i < 100 && gwAddr == ""; i++ {
				gwAddr = gw.Addr()
				time.Sleep(10 * time.Millisecond)
			}
			if gwAddr == "" {
				t.Fatal("gateway did not bind")
			}

			enc := messages.EncodingProto
			c := dialAndAuth(t, gwAddr, "kcp-refuse", enc)
			defer c.Close()
			env, _ := messages.NewEnvelopeAs(enc, messages.MsgEnterWorld, messages.EnterWorldRequest{MapID: dotnetMapID})
			if err := c.Send(env); err != nil {
				t.Fatalf("send enter world: %v", err)
			}
			respEnv, err := c.Receive()
			if err != nil {
				t.Fatalf("enter world response: %v", err)
			}
			var resp messages.EnterWorldResponse
			if err := respEnv.UnmarshalPayload(&resp); err != nil {
				t.Fatalf("unmarshal: %v", err)
			}
			if resp.Error != "server_transport_unsupported" {
				t.Fatalf("Error = %q, want server_transport_unsupported", resp.Error)
			}
			if resp.ServerAddr != "" || resp.JoinToken != "" {
				t.Fatalf("refusal leaked addr=%q token_len=%d", resp.ServerAddr, len(resp.JoinToken))
			}
		})
	}
}

// TestKCP_HostileDatagramsDoNotDisturbTheListener sprays garbage, undersized,
// oversized and wrong-conversation datagrams at the game server's shared UDP
// socket from several source ports, then proves a real client still joins,
// moves and receives authoritative state.
func TestKCP_HostileDatagramsDoNotDisturbTheListener(t *testing.T) {
	gsAddr, gsCleanup := startDotnetGameServer(t)
	defer gsCleanup()
	gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
	defer gwCleanup()

	target, err := net.ResolveUDPAddr("udp", gsAddr)
	if err != nil {
		t.Fatalf("resolve %s: %v", gsAddr, err)
	}
	payloads := [][]byte{
		{},                             // empty
		[]byte("hello"),                // undersized
		make([]byte, 23),               // one byte short of a KCP header
		make([]byte, 9000),             // oversized (> 1500 MTU limit)
		append(make([]byte, 24), 0xff), // header-sized junk with an unknown cmd
	}
	junk := make([]byte, 600)
	for i := range junk {
		junk[i] = byte(i * 31)
	}
	payloads = append(payloads, junk)
	for i := 0; i < 8; i++ {
		c, err := net.DialUDP("udp", nil, target)
		if err != nil {
			t.Fatalf("dial udp: %v", err)
		}
		for _, p := range payloads {
			_, _ = c.Write(p)
		}
		_ = c.Close()
	}

	enc := messages.EncodingProto
	gw := dialAndAuth(t, gwAddr, "kcp-after-junk", enc)
	enter := enterWorldE2E(t, gw, enc)
	gw.Close()
	gs, err := NewGameClientFor(enter)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer gs.Close()
	joinGameServer(t, gs, enter.JoinToken, enc, "kcp-after-junk")

	in, _ := messages.NewEnvelopeAs(enc, messages.MsgInput, messages.InputMessage{Tick: 1, MoveX: 1})
	if err := gs.Send(in); err != nil {
		t.Fatalf("send input: %v", err)
	}
	state := mergeSnapshots(t, gs, 5)
	if state.AckTick != 1 {
		t.Fatalf("ack_tick = %d after hostile datagrams, want 1", state.AckTick)
	}
}

// TestKCP_ServerRestartOnTheSamePort kills the game server, starts a new one on
// the same UDP port and proves a client re-enters through the gateway with a
// fresh join token. KCP has no FIN, so this is the case where stale session
// state on either side would show.
func TestKCP_ServerRestartOnTheSamePort(t *testing.T) {
	port := reserveUDPPort(t)
	listen := net.JoinHostPort("127.0.0.1", strconv.Itoa(port))

	gsAddr, gsCleanup := startDotnetGameServerWith(t, []string{"--addr", listen}, nil)
	gwAddr, gwCleanup := startGatewayForDotnet(t, gsAddr)
	defer gwCleanup()

	enc := messages.EncodingProto
	join := func(who string) *MockClient {
		gw := dialAndAuth(t, gwAddr, who, enc)
		enter := enterWorldE2E(t, gw, enc)
		gw.Close()
		gs, err := NewGameClientFor(enter)
		if err != nil {
			t.Fatalf("%s dial: %v", who, err)
		}
		joinGameServer(t, gs, enter.JoinToken, enc, who)
		waitForSnapshot(t, gs, who)
		return gs
	}

	first := join("kcp-restart-1")
	gsCleanup() // the process is killed; the client gets no FIN over UDP
	first.Close()

	gsAddr2, gsCleanup2 := startDotnetGameServerWith(t, []string{"--addr", listen}, nil)
	defer gsCleanup2()
	if gsAddr2 != gsAddr {
		t.Fatalf("restarted server listens on %s, want the same %s", gsAddr2, gsAddr)
	}
	second := join("kcp-restart-2")
	defer second.Close()
}
