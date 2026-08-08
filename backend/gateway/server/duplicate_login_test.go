package server

import (
	"io"
	"net"
	"testing"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/messages"
)

func TestGateway_DuplicateLoginKicksOldConnection(t *testing.T) {
	gw := startTestGateway(t)
	defer gw.Shutdown()

	// First login.
	conn1 := dialGateway(t, gw)
	defer conn1.Close()

	token, _ := jwt.Sign("user1", testSecret, time.Hour)
	env, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn1, env)
	resp1 := readEnvelope(t, conn1)
	var authResp1 messages.AuthResponse
	resp1.UnmarshalPayload(&authResp1)
	if !authResp1.OK {
		t.Fatalf("first auth failed: %s", authResp1.Error)
	}

	// Second login with same user on a new connection.
	conn2 := dialGateway(t, gw)
	defer conn2.Close()

	env2, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn2, env2)
	resp2 := readEnvelope(t, conn2)
	var authResp2 messages.AuthResponse
	resp2.UnmarshalPayload(&authResp2)
	if !authResp2.OK {
		t.Fatalf("second auth failed: %s", authResp2.Error)
	}

	// The old connection should receive a MsgDisconnect with reason "duplicate_login".
	conn1.SetReadDeadline(time.Now().Add(2 * time.Second))
	kickEnv, err := messages.Decode(conn1)
	if err != nil {
		// Connection may have been closed already; that is also acceptable.
		if err == io.EOF || isClosedErr(err) {
			t.Log("old connection closed (acceptable)")
			return
		}
		t.Fatalf("reading kick message: %v", err)
	}
	if kickEnv.Type != messages.MsgDisconnect {
		t.Fatalf("expected MsgDisconnect, got type %d", kickEnv.Type)
	}
	var disc messages.DisconnectMessage
	if err := kickEnv.UnmarshalPayload(&disc); err != nil {
		t.Fatalf("unmarshal disconnect: %v", err)
	}
	if disc.Reason != "duplicate_login" {
		t.Errorf("reason = %q, want %q", disc.Reason, "duplicate_login")
	}
}

func TestGateway_DuplicateLoginNewSessionWorks(t *testing.T) {
	gw := startTestGateway(t)
	defer gw.Shutdown()

	token, _ := jwt.Sign("user1", testSecret, time.Hour)

	// First login.
	conn1 := dialGateway(t, gw)
	env1, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn1, env1)
	readEnvelope(t, conn1) // auth resp

	// Second login replaces the first.
	conn2 := dialGateway(t, gw)
	defer conn2.Close()
	env2, _ := messages.NewEnvelope(messages.MsgAuth, messages.AuthRequest{Token: token})
	sendEnvelope(t, conn2, env2)
	resp := readEnvelope(t, conn2)
	var authResp messages.AuthResponse
	resp.UnmarshalPayload(&authResp)
	if !authResp.OK {
		t.Fatalf("second auth failed: %s", authResp.Error)
	}

	// Wait for conn1 to be kicked and cleaned up.
	conn1.Close()
	time.Sleep(50 * time.Millisecond)

	// The new connection should be able to enter world.
	ewResp := enterWorld(t, conn2, "map_forest")
	if ewResp.Error != "" {
		t.Errorf("enter world failed: %s", ewResp.Error)
	}
}

// isClosedErr checks for common "connection closed" errors.
func isClosedErr(err error) bool {
	if err == nil {
		return false
	}
	if netErr, ok := err.(*net.OpError); ok {
		return netErr.Err.Error() == "use of closed network connection"
	}
	return false
}
