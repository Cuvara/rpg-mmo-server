package economy

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"

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

// errVersionConflict mimics Nakama rejecting a create-only ("*") write
// because the object already exists.
var errVersionConflict = errors.New("Storage write rejected - version check failed.")

// mockGranter implements killGranter with an in-memory storage collection and
// records every mutation, so tests can assert exactly-once behaviour.
type mockGranter struct {
	walletCalls  int // legacy WalletUpdate (reward_kill)
	walletUser   string
	walletGold   int64
	walletMeta   map[string]interface{}
	walletErr    error
	balance      int64
	lbCalls      int
	lbScore      int64
	lbErr        error
	returnedRank int64

	multiCalls int
	multiErr   error // forced failure: nothing is written
	multiMeta  map[string]interface{}
	storage    map[string]string // collection/key/user -> value
	readErr    error
	writeErr   error // StorageWrite (receipt update after leaderboard)
	writeCalls int
}

func skey(c, k, u string) string { return c + "/" + k + "/" + u }

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

func (m *mockGranter) StorageRead(_ context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error) {
	if m.readErr != nil {
		return nil, m.readErr
	}
	var out []*api.StorageObject
	for _, r := range reads {
		if v, ok := m.storage[skey(r.Collection, r.Key, r.UserID)]; ok {
			out = append(out, &api.StorageObject{Collection: r.Collection, Key: r.Key, UserId: r.UserID, Value: v})
		}
	}
	return out, nil
}

func (m *mockGranter) StorageWrite(_ context.Context, writes []*runtime.StorageWrite) ([]*api.StorageObjectAck, error) {
	m.writeCalls++
	if m.writeErr != nil {
		return nil, m.writeErr
	}
	if m.storage == nil {
		m.storage = map[string]string{}
	}
	for _, w := range writes {
		m.storage[skey(w.Collection, w.Key, w.UserID)] = w.Value
	}
	return nil, nil
}

// MultiUpdate applies the create-only version check first and, like Nakama,
// commits nothing when any part fails.
func (m *mockGranter) MultiUpdate(_ context.Context, _ []*runtime.AccountUpdate, writes []*runtime.StorageWrite,
	_ []*runtime.StorageDelete, wallets []*runtime.WalletUpdate, _ bool,
) ([]*api.StorageObjectAck, []*runtime.WalletUpdateResult, error) {
	m.multiCalls++
	if m.multiErr != nil {
		return nil, nil, m.multiErr
	}
	if m.storage == nil {
		m.storage = map[string]string{}
	}
	for _, w := range writes {
		if _, exists := m.storage[skey(w.Collection, w.Key, w.UserID)]; exists && w.Version == "*" {
			return nil, nil, errVersionConflict
		}
	}
	for _, w := range writes {
		m.storage[skey(w.Collection, w.Key, w.UserID)] = w.Value
	}
	var results []*runtime.WalletUpdateResult
	for _, w := range wallets {
		m.walletUser = w.UserID
		m.walletGold = w.Changeset["gold"]
		m.multiMeta = w.Metadata
		m.balance += w.Changeset["gold"]
		results = append(results, &runtime.WalletUpdateResult{
			UserID: w.UserID, Updated: map[string]int64{"gold": m.balance},
		})
	}
	return nil, results, nil
}

func (m *mockGranter) receipt(t *testing.T, user, batch string) rewardReceipt {
	t.Helper()
	v, ok := m.storage[skey(ReceiptCollection, batch, user)]
	if !ok {
		t.Fatalf("no receipt for %s/%s", user, batch)
	}
	var r rewardReceipt
	if err := json.Unmarshal([]byte(v), &r); err != nil {
		t.Fatalf("receipt not json: %v", err)
	}
	return r
}

var fixedNow = func() time.Time { return time.Unix(1_800_000_000, 0) }

func run(t *testing.T, g *mockGranter, payload string) (RewardKillsResponse, error) {
	t.Helper()
	out, err := rewardKillsCore(context.Background(), noopLogger{}, g, payload, fixedNow)
	var resp RewardKillsResponse
	if err == nil {
		if uerr := json.Unmarshal([]byte(out), &resp); uerr != nil {
			t.Fatalf("response is not valid JSON: %v (%q)", uerr, out)
		}
	}
	return resp, err
}

func codeOf(err error) int {
	var rerr *runtime.Error
	if errors.As(err, &rerr) {
		return rerr.Code
	}
	return -1
}

const okPayload = `{"user_id":"u1","kills":3,"map_id":"map_01","batch_id":"b-1"}`

func TestRewardKills_GrantsGoldAndScoreInOneCall(t *testing.T) {
	g := &mockGranter{returnedRank: 4}
	resp, err := run(t, g, okPayload)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if g.multiCalls != 1 || g.lbCalls != 1 || g.walletCalls != 0 {
		t.Fatalf("want one MultiUpdate and one leaderboard call, got multi=%d lb=%d legacyWallet=%d",
			g.multiCalls, g.lbCalls, g.walletCalls)
	}
	if g.walletGold != 3*GoldPerKill || g.lbScore != 3 {
		t.Fatalf("gold = %d, score = %d", g.walletGold, g.lbScore)
	}
	want := RewardKillsResponse{Success: true, Status: StatusGranted, Gold: 3 * GoldPerKill, Balance: 3 * GoldPerKill, Score: 3, Rank: 4}
	if resp != want {
		t.Fatalf("response = %+v, want %+v", resp, want)
	}
	// The batch id must reach the wallet metadata: it is the audit trail for a
	// suspected double-grant and the key the receipt is filed under.
	if g.multiMeta["batch_id"] != "b-1" || g.multiMeta["kills"] != int64(3) {
		t.Fatalf("wallet metadata = %v", g.multiMeta)
	}
	r := g.receipt(t, "u1", "b-1")
	if !r.LeaderboardDone || r.Kills != 3 || r.Gold != 3*GoldPerKill || r.Score != 3 || r.GrantedAt != fixedNow().Unix() {
		t.Fatalf("receipt = %+v", r)
	}
}

// The exactly-once property: the same batch id twice → one wallet update.
func TestRewardKills_ReplayOfSameBatchID_GrantsGoldOnce(t *testing.T) {
	g := &mockGranter{returnedRank: 1}
	first, err := run(t, g, okPayload)
	if err != nil {
		t.Fatalf("first: %v", err)
	}
	second, err := run(t, g, okPayload)
	if err != nil {
		t.Fatalf("replay: %v", err)
	}
	if g.multiCalls != 1 || g.balance != 3*GoldPerKill {
		t.Fatalf("replay touched the wallet: multi=%d balance=%d", g.multiCalls, g.balance)
	}
	if g.lbCalls != 1 || g.lbScore != 3 {
		t.Fatalf("replay re-incremented the leaderboard: calls=%d score=%d", g.lbCalls, g.lbScore)
	}
	if first.Replayed || !second.Replayed {
		t.Fatalf("replayed flags: first=%v second=%v", first.Replayed, second.Replayed)
	}
	if second.Status != StatusGranted || second.Gold != first.Gold || second.Score != 3 || second.Rank != 1 || second.Balance != 0 {
		t.Fatalf("replay response = %+v", second)
	}
}

// Two duplicates racing past the read: the second MultiUpdate loses on the
// create-only version check, rolls back, and is answered as a replay.
func TestRewardKills_ConcurrentDuplicate_LosesOnVersionCheck_NoSecondGrant(t *testing.T) {
	g := &mockGranter{}
	// Simulate "receipt appeared between our read and our write" by seeding
	// the receipt through the mock after the read would have missed it: the
	// mock's MultiUpdate enforces "*" against current state, so seeding first
	// is equivalent for the write path; the fast-path read is covered above.
	if _, err := run(t, g, okPayload); err != nil {
		t.Fatal(err)
	}
	// Force the write path despite an existing receipt by clearing the read
	// result once: a granter whose StorageRead misses but MultiUpdate conflicts.
	racing := &raceGranter{mockGranter: g, missFirstRead: true}
	out, err := rewardKillsCore(context.Background(), noopLogger{}, racing, okPayload, fixedNow)
	if err != nil {
		t.Fatalf("loser of the race must be answered as a replay, got error %v", err)
	}
	var resp RewardKillsResponse
	_ = json.Unmarshal([]byte(out), &resp)
	if !resp.Replayed || g.balance != 3*GoldPerKill || g.multiCalls != 2 {
		t.Fatalf("resp=%+v balance=%d multiCalls=%d", resp, g.balance, g.multiCalls)
	}
}

// raceGranter makes the first StorageRead miss so the caller proceeds to
// MultiUpdate against a receipt that (from its point of view) appeared
// concurrently.
type raceGranter struct {
	*mockGranter
	missFirstRead bool
}

func (r *raceGranter) StorageRead(ctx context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error) {
	if r.missFirstRead {
		r.missFirstRead = false
		return nil, nil
	}
	return r.mockGranter.StorageRead(ctx, reads)
}

func TestRewardKills_RejectsBadPayloads_BeforeAnyGrant(t *testing.T) {
	cases := []struct {
		name     string
		payload  string
		wantCode int
	}{
		{"not json", `{`, 3},
		{"missing user", `{"kills":1,"batch_id":"b"}`, 3},
		{"missing batch id", `{"user_id":"u1","kills":1}`, 3},
		{"zero kills", `{"user_id":"u1","kills":0,"batch_id":"b"}`, CodeKillsOutOfRange},
		{"negative kills", `{"user_id":"u1","kills":-5,"batch_id":"b"}`, CodeKillsOutOfRange},
		{"over cap", `{"user_id":"u1","kills":1001,"batch_id":"b"}`, CodeKillsOutOfRange},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			g := &mockGranter{}
			_, err := run(t, g, c.payload)
			if err == nil {
				t.Fatal("want error")
			}
			if codeOf(err) != c.wantCode {
				t.Fatalf("code = %d, want %d (%v)", codeOf(err), c.wantCode, err)
			}
			if g.multiCalls != 0 || g.lbCalls != 0 || g.walletCalls != 0 || len(g.storage) != 0 {
				t.Fatalf("rejected payload must grant nothing, got multi=%d lb=%d storage=%d",
					g.multiCalls, g.lbCalls, len(g.storage))
			}
		})
	}
}

func TestRewardKills_AtCap_IsAccepted(t *testing.T) {
	g := &mockGranter{}
	resp, err := run(t, g, `{"user_id":"u1","kills":1000,"batch_id":"b"}`)
	if err != nil || resp.Gold != 1000*GoldPerKill {
		t.Fatalf("err=%v resp=%+v", err, resp)
	}
}

// The retry contract: an error means NOTHING was granted — receipt and wallet
// are one transaction, so a failed MultiUpdate leaves no receipt behind and the
// re-sent batch is granted fresh.
func TestRewardKills_MultiUpdateFailure_IsAnErrorAndLeavesNoReceipt(t *testing.T) {
	g := &mockGranter{multiErr: errors.New("db down")}
	_, err := run(t, g, okPayload)
	if err == nil || codeOf(err) != 13 {
		t.Fatalf("want INTERNAL error when the transaction fails, got %v", err)
	}
	if g.lbCalls != 0 || len(g.storage) != 0 || g.balance != 0 {
		t.Fatalf("failed transaction must leave nothing: lb=%d storage=%d balance=%d", g.lbCalls, len(g.storage), g.balance)
	}
	g.multiErr = nil
	if resp, err := run(t, g, okPayload); err != nil || resp.Replayed || g.balance != 3*GoldPerKill {
		t.Fatalf("retry after rollback must grant fresh: err=%v resp=%+v balance=%d", err, resp, g.balance)
	}
}

func TestRewardKills_ReceiptLookupFailure_IsAnErrorBeforeAnyGrant(t *testing.T) {
	g := &mockGranter{readErr: errors.New("storage down")}
	if _, err := run(t, g, okPayload); err == nil || codeOf(err) != 13 {
		t.Fatalf("want INTERNAL, got %v", err)
	}
	if g.multiCalls != 0 {
		t.Fatal("must not grant when the idempotency check cannot run")
	}
}

// Once gold is granted, a leaderboard failure is reported as status=partial
// (not an error, which would invite a wallet retry) and a replay of the same
// batch id retries ONLY the leaderboard.
func TestRewardKills_LeaderboardFailureAfterGold_IsPartial_ThenReplayConvergesScore(t *testing.T) {
	g := &mockGranter{lbErr: errors.New("leaderboard down"), returnedRank: 2}
	resp, err := run(t, g, okPayload)
	if err != nil {
		t.Fatalf("must not error after gold was granted: %v", err)
	}
	if !resp.Success || resp.Status != StatusPartial || resp.LeaderboardError == "" || resp.Gold != 3*GoldPerKill {
		t.Fatalf("want partial, got %+v", resp)
	}
	if r := g.receipt(t, "u1", "b-1"); r.LeaderboardDone {
		t.Fatal("receipt must still say leaderboard pending")
	}

	g.lbErr = nil
	resp, err = run(t, g, okPayload)
	if err != nil {
		t.Fatal(err)
	}
	if !resp.Replayed || resp.Status != StatusGranted || resp.Score != 3 || resp.Rank != 2 {
		t.Fatalf("replay after partial = %+v", resp)
	}
	if g.multiCalls != 1 || g.balance != 3*GoldPerKill {
		t.Fatalf("replay must not re-grant gold: multi=%d balance=%d", g.multiCalls, g.balance)
	}
	if g.lbCalls != 2 || g.lbScore != 3 {
		t.Fatalf("leaderboard: calls=%d score=%d, want 2 calls and score 3", g.lbCalls, g.lbScore)
	}
	if r := g.receipt(t, "u1", "b-1"); !r.LeaderboardDone || r.Score != 3 {
		t.Fatalf("receipt after convergence = %+v", r)
	}

	// A third send is a pure replay: no wallet, no leaderboard.
	if _, err := run(t, g, okPayload); err != nil || g.lbCalls != 2 || g.multiCalls != 1 {
		t.Fatalf("third send: err=%v lb=%d multi=%d", err, g.lbCalls, g.multiCalls)
	}
}

// Receipt storage is server-only: neither readable nor writable by clients.
func TestRewardKills_ReceiptIsServerOnly(t *testing.T) {
	g := &recordingGranter{}
	if _, err := rewardKillsCore(context.Background(), noopLogger{}, g, okPayload, fixedNow); err != nil {
		t.Fatal(err)
	}
	if len(g.multiWrites) != 1 {
		t.Fatalf("want one receipt write in MultiUpdate, got %d", len(g.multiWrites))
	}
	w := g.multiWrites[0]
	if w.Collection != ReceiptCollection || w.Key != "b-1" || w.UserID != "u1" || w.Version != "*" ||
		w.PermissionRead != 0 || w.PermissionWrite != 0 {
		t.Fatalf("receipt write = %+v", w)
	}
	if !strings.Contains(w.Value, `"batch_id":"b-1"`) {
		t.Fatalf("receipt value = %s", w.Value)
	}
}

type recordingGranter struct {
	mockGranter
	multiWrites []*runtime.StorageWrite
}

func (r *recordingGranter) MultiUpdate(ctx context.Context, a []*runtime.AccountUpdate, w []*runtime.StorageWrite,
	d []*runtime.StorageDelete, wu []*runtime.WalletUpdate, l bool,
) ([]*api.StorageObjectAck, []*runtime.WalletUpdateResult, error) {
	r.multiWrites = append(r.multiWrites, w...)
	return r.mockGranter.MultiUpdate(ctx, a, w, d, wu, l)
}
