package economy

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// noopLogger implements runtime.Logger and discards everything.
type noopLogger struct{}

func (noopLogger) Debug(string, ...interface{})                       {}
func (noopLogger) Info(string, ...interface{})                        {}
func (noopLogger) Warn(string, ...interface{})                        {}
func (noopLogger) Error(string, ...interface{})                       {}
func (l noopLogger) WithField(string, interface{}) runtime.Logger     { return l }
func (l noopLogger) WithFields(map[string]interface{}) runtime.Logger { return l }
func (noopLogger) Fields() map[string]interface{}                     { return nil }

// mockGranter implements killGranter and records what was granted.
type mockGranter struct {
	walletCalls  int
	walletUser   string
	walletGold   int64
	walletMeta   map[string]interface{}
	walletErr    error
	balance      int64
	lbCalls      int
	lbScore      int64
	lbErr        error
	returnedRank int64
}

func (m *mockGranter) WalletUpdate(_ context.Context, userID string, changeset map[string]int64,
	metadata map[string]interface{}, _ bool) (map[string]int64, map[string]int64, error) {
	m.walletCalls++
	m.walletUser = userID
	m.walletGold = changeset["gold"]
	m.walletMeta = metadata
	if m.walletErr != nil {
		return nil, nil, m.walletErr
	}
	m.balance += changeset["gold"]
	return map[string]int64{"gold": m.balance}, nil, nil
}

func (m *mockGranter) LeaderboardRecordWrite(_ context.Context, _, _, _ string,
	score, _ int64, _ map[string]interface{}, _ *int) (*api.LeaderboardRecord, error) {
	m.lbCalls++
	if m.lbErr != nil {
		return nil, m.lbErr
	}
	m.lbScore += score
	return &api.LeaderboardRecord{Score: m.lbScore, Rank: m.returnedRank}, nil
}

func run(t *testing.T, g *mockGranter, payload string) (RewardKillsResponse, error) {
	t.Helper()
	out, err := rewardKillsCore(context.Background(), noopLogger{}, g, payload)
	var resp RewardKillsResponse
	if err == nil {
		if uerr := json.Unmarshal([]byte(out), &resp); uerr != nil {
			t.Fatalf("response is not valid JSON: %v (%q)", uerr, out)
		}
	}
	return resp, err
}

func TestRewardKills_GrantsGoldAndScoreInOneCall(t *testing.T) {
	g := &mockGranter{returnedRank: 4}
	resp, err := run(t, g,
		`{"user_id":"u1","kills":3,"map_id":"map_01","batch_id":"b-1"}`)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if g.walletCalls != 1 || g.lbCalls != 1 {
		t.Fatalf("want exactly one wallet and one leaderboard call, got %d/%d",
			g.walletCalls, g.lbCalls)
	}
	if g.walletGold != 3*GoldPerKill {
		t.Fatalf("gold = %d, want %d", g.walletGold, 3*GoldPerKill)
	}
	if g.lbScore != 3 {
		t.Fatalf("leaderboard score = %d, want 3", g.lbScore)
	}
	if !resp.Success || resp.Gold != 3*GoldPerKill || resp.Score != 3 || resp.Rank != 4 {
		t.Fatalf("response = %+v", resp)
	}
	// The batch id must reach the wallet metadata: it is the audit trail for a
	// suspected double-grant and the key a future idempotency guard would use.
	if g.walletMeta["batch_id"] != "b-1" || g.walletMeta["kills"] != int64(3) {
		t.Fatalf("wallet metadata = %v", g.walletMeta)
	}
}

func TestRewardKills_RejectsBadPayloads_BeforeAnyGrant(t *testing.T) {
	cases := []struct {
		name    string
		payload string
	}{
		{"not json", `{`},
		{"missing user", `{"kills":1}`},
		{"zero kills", `{"user_id":"u1","kills":0}`},
		{"negative kills", `{"user_id":"u1","kills":-5}`},
		{"over cap", `{"user_id":"u1","kills":1001}`},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			g := &mockGranter{}
			if _, err := run(t, g, c.payload); err == nil {
				t.Fatal("want error")
			}
			if g.walletCalls != 0 || g.lbCalls != 0 {
				t.Fatalf("rejected payload must grant nothing, got wallet=%d lb=%d",
					g.walletCalls, g.lbCalls)
			}
		})
	}
}

// The retry contract: an error means NOTHING was granted, so the game server
// may re-queue the batch without risking a double grant.
func TestRewardKills_WalletFailure_IsAnErrorAndGrantsNothingDownstream(t *testing.T) {
	g := &mockGranter{walletErr: errors.New("db down")}
	_, err := run(t, g, `{"user_id":"u1","kills":2}`)
	if err == nil {
		t.Fatal("want error when the wallet update fails")
	}
	if g.lbCalls != 0 {
		t.Fatal("leaderboard must not be written after a failed wallet update")
	}
}

// The other half of the contract: once gold is granted, the call reports
// success even if the score write fails — an error here would invite a retry
// that grants the gold twice.
func TestRewardKills_LeaderboardFailureAfterGold_IsSuccessWithErrorField(t *testing.T) {
	g := &mockGranter{lbErr: errors.New("leaderboard down")}
	resp, err := run(t, g, `{"user_id":"u1","kills":2}`)
	if err != nil {
		t.Fatalf("must not error after gold was granted: %v", err)
	}
	if !resp.Success || resp.LeaderboardError == "" {
		t.Fatalf("want success with leaderboard_error set, got %+v", resp)
	}
	if resp.Gold != 2*GoldPerKill {
		t.Fatalf("gold = %d, want %d", resp.Gold, 2*GoldPerKill)
	}
}
