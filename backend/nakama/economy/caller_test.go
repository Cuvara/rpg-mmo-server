package economy

import (
	"context"
	"errors"
	"testing"

	"github.com/heroiclabs/nakama-common/runtime"
)

// serverCtx mimics a runtime.http_key (server-to-server) invocation: Nakama
// populates no session markers in the context.
func serverCtx() context.Context { return context.Background() }

// clientCtx mimics an authenticated client-session invocation.
func clientCtx() context.Context {
	ctx := context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_ID, "u-attacker")
	ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_SESSION_ID, "s-1")
	return context.WithValue(ctx, runtime.RUNTIME_CTX_USER_SESSION_EXP, int64(1_800_000_000))
}

func TestRequireServerCaller(t *testing.T) {
	cases := []struct {
		name string
		ctx  context.Context
		want error
	}{
		{"no session markers (http_key)", serverCtx(), nil},
		{"empty user id is still a server call",
			context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_ID, ""), nil},
		{"user id present", context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_ID, "u1"), ErrServerOnly},
		{"session id only", context.WithValue(context.Background(), runtime.RUNTIME_CTX_SESSION_ID, "s1"), ErrServerOnly},
		{"session expiry only", context.WithValue(context.Background(), runtime.RUNTIME_CTX_USER_SESSION_EXP, int64(1)), ErrServerOnly},
		{"full client session", clientCtx(), ErrServerOnly},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := requireServerCaller(c.ctx); !errors.Is(got, c.want) {
				t.Fatalf("got %v, want %v", got, c.want)
			}
		})
	}
}

func TestErrServerOnly_IsPermissionDenied(t *testing.T) {
	var rerr *runtime.Error
	if !errors.As(ErrServerOnly, &rerr) || rerr.Code != 7 {
		t.Fatalf("ErrServerOnly = %#v, want runtime.Error code 7", ErrServerOnly)
	}
}

type mutationRPC func(ctx context.Context, logger runtime.Logger, nk killGranter, payload string) (string, error)

var mutationRPCs = []struct {
	name    string
	rpc     mutationRPC
	payload string
}{
	{RPCRewardKill, rewardKillCore, `{"user_id":"u1","victim_id":"m1","map_id":"map_01"}`},
	{RPCRewardKills, func(ctx context.Context, l runtime.Logger, nk killGranter, p string) (string, error) {
		return rewardKillsCore(ctx, l, nk, p, fixedNow)
	}, `{"user_id":"u1","kills":3,"map_id":"map_01","batch_id":"b-1"}`},
	{RPCSubmitKill, submitKillCore, `{"user_id":"u1"}`},
}

// A client session must be rejected with code 7 BEFORE anything is granted —
// the payload is valid here, so a missing guard would mint gold/score.
func TestMutationRPCs_RejectClientSession_BeforeAnyGrant(t *testing.T) {
	for _, m := range mutationRPCs {
		t.Run(m.name, func(t *testing.T) {
			g := &mockGranter{}
			out, err := m.rpc(clientCtx(), noopLogger{}, g, m.payload)
			if !errors.Is(err, ErrServerOnly) {
				t.Fatalf("err = %v (out %q), want ErrServerOnly", err, out)
			}
			if g.walletCalls != 0 || g.lbCalls != 0 || g.multiCalls != 0 {
				t.Fatalf("rejected caller must grant nothing, got wallet=%d lb=%d multi=%d", g.walletCalls, g.lbCalls, g.multiCalls)
			}
		})
	}
}

// The guard runs before parsing: a client sending garbage still gets 7, not 3,
// so the endpoint leaks nothing about its payload schema to clients.
func TestMutationRPCs_ClientSession_RejectedBeforeParse(t *testing.T) {
	for _, m := range mutationRPCs {
		t.Run(m.name, func(t *testing.T) {
			g := &mockGranter{}
			if _, err := m.rpc(clientCtx(), noopLogger{}, g, `{`); !errors.Is(err, ErrServerOnly) {
				t.Fatalf("err = %v, want ErrServerOnly", err)
			}
		})
	}
}

// The server path (no session markers) still grants.
func TestMutationRPCs_ServerCaller_Grants(t *testing.T) {
	for _, m := range mutationRPCs {
		t.Run(m.name, func(t *testing.T) {
			g := &mockGranter{}
			if _, err := m.rpc(serverCtx(), noopLogger{}, g, m.payload); err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if g.walletCalls+g.lbCalls+g.multiCalls == 0 {
				t.Fatal("server caller must grant something")
			}
		})
	}
}
