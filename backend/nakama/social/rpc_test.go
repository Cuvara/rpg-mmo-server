package social

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// mockNakama satisfies the full runtime.NakamaModule interface by embedding it
// (nil) and overriding only the storage methods the party RPCs use. Any other
// method call panics, which is the intended test signal.
type mockNakama struct {
	runtime.NakamaModule
	*memStore
}

func newMockNakama() *mockNakama { return &mockNakama{memStore: newMemStore()} }

// StorageRead disambiguates between the embedded interface and *memStore.
func (m *mockNakama) StorageRead(ctx context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error) {
	return m.memStore.StorageRead(ctx, reads)
}

// MultiUpdate disambiguates between the embedded interface and *memStore.
func (m *mockNakama) MultiUpdate(ctx context.Context, a []*runtime.AccountUpdate, w []*runtime.StorageWrite,
	d []*runtime.StorageDelete, wa []*runtime.WalletUpdate, l bool,
) ([]*api.StorageObjectAck, []*runtime.WalletUpdateResult, error) {
	return m.memStore.MultiUpdate(ctx, a, w, d, wa, l)
}

func decodeResponse(t *testing.T, out string) PartyResponse {
	t.Helper()
	var resp PartyResponse
	if err := json.Unmarshal([]byte(out), &resp); err != nil {
		t.Fatalf("unmarshal response %q: %v", out, err)
	}
	return resp
}

// The whole client-facing lifecycle through the registered handlers.
func TestPartyRPCs_Lifecycle(t *testing.T) {
	nk := newMockNakama()
	log := noopLogger{}

	out, err := PartyCreateRPC(clientCtx("rpc-leader"), log, nil, nk, "")
	if err != nil {
		t.Fatalf("party_create: %v", err)
	}
	created := decodeResponse(t, out)
	if created.PartyID == "" || created.LeaderID != "rpc-leader" || created.MemberCount != 1 {
		t.Fatalf("create response = %+v", created)
	}
	if created.MaxMembers != MaxPartyMembers {
		t.Errorf("max_members = %d, want %d", created.MaxMembers, MaxPartyMembers)
	}

	payload := `{"party_id":"` + created.PartyID + `"}`
	out, err = PartyJoinRPC(clientCtx("rpc-joiner"), log, nil, nk, payload)
	if err != nil {
		t.Fatalf("party_join: %v", err)
	}
	if joined := decodeResponse(t, out); joined.MemberCount != 2 {
		t.Fatalf("join response = %+v", joined)
	}

	// party_get from a client session.
	out, err = PartyGetRPC(clientCtx("rpc-joiner"), log, nil, nk, payload)
	if err != nil {
		t.Fatalf("party_get (client): %v", err)
	}
	if got := decodeResponse(t, out); got.MemberCount != 2 || got.LeaderID != "rpc-leader" {
		t.Fatalf("get response = %+v", got)
	}

	// The leader leaves: leadership transfers, party survives.
	out, err = PartyLeaveRPC(clientCtx("rpc-leader"), log, nil, nk, "")
	if err != nil {
		t.Fatalf("party_leave (leader): %v", err)
	}
	left := decodeResponse(t, out)
	if left.LeaderID != "rpc-joiner" || left.MemberCount != 1 {
		t.Fatalf("leave response = %+v, want leadership on rpc-joiner", left)
	}

	// The last member leaves: the party is gone, and the response says so.
	out, err = PartyLeaveRPC(clientCtx("rpc-joiner"), log, nil, nk, "")
	if err != nil {
		t.Fatalf("party_leave (last): %v", err)
	}
	if last := decodeResponse(t, out); last.PartyID != "" || last.MemberCount != 0 {
		t.Fatalf("final leave response = %+v, want an empty party", last)
	}
	if _, err := PartyGetRPC(serverCtx(), log, nil, nk, payload); !errors.Is(err, ErrPartyNotFound) {
		t.Fatalf("get after deletion = %v, want ErrPartyNotFound", err)
	}
}

// party_get is the one party RPC the gateway calls over runtime.http_key, so
// it must answer a context with no session markers at all. This is the check
// that the dungeon-entry verification path works server-to-server.
func TestPartyGetRPC_WorksServerToServer(t *testing.T) {
	nk := newMockNakama()
	nk.seedParty(t, "party-s2s", "u1", "u2", "u3")

	out, err := PartyGetRPC(serverCtx(), noopLogger{}, nil, nk, `{"party_id":"party-s2s"}`)
	if err != nil {
		t.Fatalf("party_get over http_key: %v", err)
	}
	resp := decodeResponse(t, out)
	if resp.MemberCount != 3 || resp.LeaderID != "u1" {
		t.Fatalf("resp = %+v", resp)
	}
	// What the gateway actually does with it.
	p, err := GetParty(serverCtx(), nk, "party-s2s")
	if err != nil {
		t.Fatalf("GetParty: %v", err)
	}
	if !p.IsMember("u3") {
		t.Error("u3 must be reported as a member")
	}
	if p.IsMember("impostor") {
		t.Error("a non-member must not be reported as a member")
	}
}

// The mutation RPCs act on "the caller", so an http_key call — which carries
// no user id — has no subject and must be rejected outright.
func TestPartyMutationRPCs_RejectServerCaller(t *testing.T) {
	nk := newMockNakama()
	nk.seedParty(t, "party-a", "u1")

	mutations := []struct {
		name string
		call func() (string, error)
	}{
		{RPCPartyCreate, func() (string, error) { return PartyCreateRPC(serverCtx(), noopLogger{}, nil, nk, "") }},
		{RPCPartyJoin, func() (string, error) {
			return PartyJoinRPC(serverCtx(), noopLogger{}, nil, nk, `{"party_id":"party-a"}`)
		}},
		{RPCPartyLeave, func() (string, error) { return PartyLeaveRPC(serverCtx(), noopLogger{}, nil, nk, "") }},
	}
	for _, m := range mutations {
		t.Run(m.name, func(t *testing.T) {
			if _, err := m.call(); !errors.Is(err, ErrUnauthenticated) {
				t.Fatalf("err = %v, want ErrUnauthenticated", err)
			}
		})
	}
	// Nothing was mutated.
	if len(nk.mustParty(t, "party-a").Members) != 1 {
		t.Error("rejected server calls must not change party state")
	}
}

func TestPartyRPCs_PayloadErrors(t *testing.T) {
	nk := newMockNakama()
	cases := []struct {
		name string
		call func() (string, error)
		want error
	}{
		{"join without party id", func() (string, error) {
			return PartyJoinRPC(clientCtx("pe-1"), noopLogger{}, nil, nk, "")
		}, ErrPartyIDRequired},
		{"join with bad json", func() (string, error) {
			return PartyJoinRPC(clientCtx("pe-2"), noopLogger{}, nil, nk, `{`)
		}, ErrInvalidPayload},
		{"get without party id", func() (string, error) {
			return PartyGetRPC(serverCtx(), noopLogger{}, nil, nk, "")
		}, ErrPartyIDRequired},
		{"get with bad json", func() (string, error) {
			return PartyGetRPC(serverCtx(), noopLogger{}, nil, nk, `not json`)
		}, ErrInvalidPayload},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if _, err := c.call(); !errors.Is(err, c.want) {
				t.Fatalf("err = %v, want %v", err, c.want)
			}
		})
	}
}

// A storage outage must reach the client as ErrInternal, with the storage
// error text left in the log: it names collections and keys.
func TestPartyRPCs_StorageFailureIsOpaqueToClient(t *testing.T) {
	nk := newMockNakama()
	nk.failRead = errStorage

	_, err := PartyCreateRPC(clientCtx("outage-user"), noopLogger{}, nil, nk, "")
	if !errors.Is(err, ErrInternal) {
		t.Fatalf("err = %v, want ErrInternal", err)
	}
	var rerr *runtime.Error
	if !errors.As(err, &rerr) || rerr.Code != codeInternal {
		t.Fatalf("err = %#v, want runtime.Error code %d", err, codeInternal)
	}
	if rerr.Message == errStorage.Error() {
		t.Error("storage error text must not reach the client")
	}
}

// Every client-facing error carries the gRPC code the RPC contract promises.
func TestErrorCodes(t *testing.T) {
	cases := []struct {
		err  error
		code int
	}{
		{ErrUnauthenticated, codeUnauthenticated},
		{ErrInvalidPayload, codeInvalidArgument},
		{ErrPartyIDRequired, codeInvalidArgument},
		{ErrPartyNotFound, codeNotFound},
		{ErrPartyFull, codeFailedPrecondition},
		{ErrAlreadyInParty, codeFailedPrecondition},
		{ErrNotInParty, codeFailedPrecondition},
		{ErrPartyBusy, codeAborted},
		{ErrRateLimited, codeResourceExhausted},
		{ErrInternal, codeInternal},
	}
	for _, c := range cases {
		var rerr *runtime.Error
		if !errors.As(c.err, &rerr) {
			t.Errorf("%v is not a *runtime.Error", c.err)
			continue
		}
		if rerr.Code != c.code {
			t.Errorf("%v has code %d, want %d", c.err, rerr.Code, c.code)
		}
	}
}

// The per-user limiter admits a burst and then refuses, and a refusal is
// ErrRateLimited rather than a party error — the client must be able to tell
// "slow down" from "you cannot do that".
func TestPartyWriteRateLimit(t *testing.T) {
	nk := newMockNakama()
	const user = "flooder"

	// Burst is spent on calls that legitimately fail (no party to leave), so
	// this measures the limiter, not the party logic.
	var refusedAt int
	for i := 0; i < PartyWriteBurst+5; i++ {
		_, err := PartyLeaveRPC(clientCtx(user), noopLogger{}, nil, nk, "")
		if errors.Is(err, ErrRateLimited) {
			refusedAt = i
			break
		}
		if !errors.Is(err, ErrNotInParty) {
			t.Fatalf("call %d: err = %v, want ErrNotInParty", i, err)
		}
	}
	if refusedAt != PartyWriteBurst {
		t.Fatalf("rate limited after %d calls, want %d (PartyWriteBurst)", refusedAt, PartyWriteBurst)
	}
	// A different user is unaffected: the bucket is per user id.
	if _, err := PartyLeaveRPC(clientCtx("bystander"), noopLogger{}, nil, nk, ""); !errors.Is(err, ErrNotInParty) {
		t.Fatalf("bystander err = %v, want ErrNotInParty", err)
	}
}
