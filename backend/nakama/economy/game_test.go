package economy

import (
	"context"
	"encoding/json"
	"errors"
	"reflect"
	"strings"
	"testing"

	"github.com/duycuong/rpg-mmo/nakama/gamectx"
	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

func gameContext(games string) context.Context {
	return context.WithValue(serverCtx(), runtime.RUNTIME_CTX_ENV, map[string]string{gamectx.EnvGames: games})
}

func gamePayload(t *testing.T, payload, game string) string {
	t.Helper()
	var fields map[string]interface{}
	if err := json.Unmarshal([]byte(payload), &fields); err != nil {
		t.Fatal(err)
	}
	fields["game_id"] = game
	encoded, err := json.Marshal(fields)
	if err != nil {
		t.Fatal(err)
	}
	return string(encoded)
}

func TestSubmitKill_NonDefaultGameWritesScopedLeaderboard(t *testing.T) {
	g := &mockGranter{}
	if _, err := submitKillCore(gameContext("cuvara,foo"), noopLogger{}, g, `{"user_id":"u1","game_id":"foo"}`); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(g.lbIDs, []string{"foo.kills_alltime"}) || g.lbScore != 1 {
		t.Fatalf("leaderboard writes = %v, score = %d", g.lbIDs, g.lbScore)
	}
}

func TestRewardKills_NonDefaultGameWritesScopedLeaderboardAndGold(t *testing.T) {
	ctx := gameContext("cuvara,foo")
	g := &mockGranter{walletBalances: map[string]int64{"gold": 900, "foo.gold": 7}, returnedRank: 2}
	payload := gamePayload(t, okPayload, "foo")
	out, err := rewardKillsCore(ctx, noopLogger{}, g, payload, fixedNow)
	if err != nil {
		t.Fatal(err)
	}
	var response RewardKillsResponse
	if err := json.Unmarshal([]byte(out), &response); err != nil {
		t.Fatal(err)
	}
	if response.Balance != 37 || response.Gold != 30 || response.Score != 3 || response.Status != StatusGranted {
		t.Fatalf("response = %+v", response)
	}
	if !reflect.DeepEqual(g.walletChangeset, map[string]int64{"foo.gold": 30}) || g.walletBalances["gold"] != 900 {
		t.Fatalf("changeset = %v, balances = %v", g.walletChangeset, g.walletBalances)
	}
	wantMeta := map[string]interface{}{"source": "enemy_kills", "kills": int64(3), "map_id": "map_01", "batch_id": "b-1", "game_id": "foo"}
	if !reflect.DeepEqual(g.multiMeta, wantMeta) {
		t.Fatalf("metadata = %v, want %v", g.multiMeta, wantMeta)
	}
	if !reflect.DeepEqual(g.lbIDs, []string{"foo.kills_alltime"}) {
		t.Fatalf("leaderboard writes = %v", g.lbIDs)
	}
	value, exists := g.storage[skey("foo.reward_receipts", "b-1", "u1")]
	if !exists || len(g.storage) != 1 {
		t.Fatalf("receipt storage = %v", g.storage)
	}
	var receipt rewardReceipt
	if err := json.Unmarshal([]byte(value), &receipt); err != nil || !receipt.LeaderboardDone {
		t.Fatalf("updated scoped receipt = %+v, err = %v", receipt, err)
	}
	out, err = rewardKillsCore(ctx, noopLogger{}, g, payload, fixedNow)
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal([]byte(out), &response); err != nil {
		t.Fatal(err)
	}
	if !response.Replayed || g.multiCalls != 1 || g.lbCalls != 1 || g.walletBalances["foo.gold"] != 37 {
		t.Fatalf("replay = %+v, wallet calls = %d, leaderboard calls = %d", response, g.multiCalls, g.lbCalls)
	}
	for _, read := range g.reads {
		if read.Collection != "foo.reward_receipts" {
			t.Fatalf("scoped replay read %q", read.Collection)
		}
	}

	// Identical user and batch ids in cuvara must still grant a fresh reward.
	out, err = rewardKillsCore(ctx, noopLogger{}, g, okPayload, fixedNow)
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal([]byte(out), &response); err != nil {
		t.Fatal(err)
	}
	if response.Replayed || response.Balance != 930 || response.Score != 3 || g.multiCalls != 2 {
		t.Fatalf("default-game grant = %+v, transactions = %d", response, g.multiCalls)
	}
	if _, exists := g.storage[skey("reward_receipts", "b-1", "u1")]; !exists || len(g.storage) != 2 {
		t.Fatalf("separate default receipt missing: %v", g.storage)
	}
	if !reflect.DeepEqual(g.lbIDs, []string{"foo.kills_alltime", "kills_alltime"}) || g.walletBalances["foo.gold"] != 37 {
		t.Fatalf("leaderboards = %v, balances = %v", g.lbIDs, g.walletBalances)
	}
	if _, exists := g.multiMeta["game_id"]; exists {
		t.Fatalf("default ledger metadata = %v", g.multiMeta)
	}
}

func TestRewardKills_NonDefaultGameReplayRetriesScopedLeaderboard(t *testing.T) {
	for _, race := range []bool{false, true} {
		t.Run(map[bool]string{false: "receipt fast path", true: "transaction conflict"}[race], func(t *testing.T) {
			ctx := gameContext("cuvara,foo")
			g := &mockGranter{lbErr: errors.New("leaderboard unavailable")}
			payload := gamePayload(t, okPayload, "foo")
			if _, err := rewardKillsCore(ctx, noopLogger{}, g, payload, fixedNow); err != nil {
				t.Fatal(err)
			}
			g.lbErr = nil
			var nk killGranter = g
			wantTransactions := 1
			if race {
				nk = &raceGranter{mockGranter: g, missFirstRead: true}
				wantTransactions = 2
			}
			out, err := rewardKillsCore(ctx, noopLogger{}, nk, payload, fixedNow)
			if err != nil {
				t.Fatal(err)
			}
			var response RewardKillsResponse
			if err := json.Unmarshal([]byte(out), &response); err != nil {
				t.Fatal(err)
			}
			if !response.Replayed || response.Status != StatusGranted || response.Score != 3 || g.multiCalls != wantTransactions || g.walletBalances["foo.gold"] != 30 {
				t.Fatalf("replay = %+v, transactions = %d, balances = %v", response, g.multiCalls, g.walletBalances)
			}
			if !reflect.DeepEqual(g.lbIDs, []string{"foo.kills_alltime", "foo.kills_alltime"}) || len(g.storage) != 1 {
				t.Fatalf("leaderboards = %v, storage = %v", g.lbIDs, g.storage)
			}
			for _, read := range g.reads {
				if read.Collection != "foo.reward_receipts" {
					t.Fatalf("receipt read = %+v", read)
				}
			}
		})
	}
}

func TestRewardKill_NonDefaultGameCreditsScopedGold(t *testing.T) {
	g := &mockGranter{walletBalances: map[string]int64{"gold": 900, "foo.gold": 7}}
	out, err := rewardKillCore(gameContext("cuvara,foo"), noopLogger{}, g, `{"user_id":"u1","victim_id":"m1","map_id":"map_01","game_id":"foo"}`)
	if err != nil {
		t.Fatal(err)
	}
	var response RewardKillResponse
	if err := json.Unmarshal([]byte(out), &response); err != nil {
		t.Fatal(err)
	}
	if response != (RewardKillResponse{Success: true, Gold: 17}) {
		t.Fatalf("response = %+v", response)
	}
	if !reflect.DeepEqual(g.walletChangeset, map[string]int64{"foo.gold": 10}) || g.walletBalances["gold"] != 900 {
		t.Fatalf("changeset = %v, balances = %v", g.walletChangeset, g.walletBalances)
	}
	wantMeta := map[string]interface{}{"source": "enemy_kill", "victim_id": "m1", "map_id": "map_01", "game_id": "foo"}
	if !reflect.DeepEqual(g.walletMeta, wantMeta) {
		t.Fatalf("metadata = %v, want %v", g.walletMeta, wantMeta)
	}
}

func TestMutationRPCs_DefaultGamePreservesDevelop(t *testing.T) {
	t.Setenv("GAMES", "")
	for _, game := range []string{"", "cuvara"} {
		for _, mutation := range mutationRPCs {
			t.Run(mutation.name+"/game="+game, func(t *testing.T) {
				g := &mockGranter{}
				payload := mutation.payload
				if game != "" {
					payload = gamePayload(t, payload, game)
				}
				out, err := mutation.rpc(serverCtx(), noopLogger{}, g, payload)
				if err != nil {
					t.Fatal(err)
				}
				var wantJSON string
				switch mutation.name {
				case RPCRewardKill:
					wantJSON = `{"gold":10,"success":true}`
					wantMeta := map[string]interface{}{"source": "enemy_kill", "victim_id": "m1", "map_id": "map_01"}
					if !reflect.DeepEqual(g.walletMeta, wantMeta) || !reflect.DeepEqual(g.walletChangeset, map[string]int64{"gold": 10}) {
						t.Fatalf("metadata = %v, changeset = %v", g.walletMeta, g.walletChangeset)
					}
				case RPCRewardKills:
					wantJSON = `{"success":true,"status":"granted","replayed":false,"gold":30,"balance":30,"score":3,"rank":0}`
					wantMeta := map[string]interface{}{"source": "enemy_kills", "kills": int64(3), "map_id": "map_01", "batch_id": "b-1"}
					if !reflect.DeepEqual(g.multiMeta, wantMeta) || !reflect.DeepEqual(g.walletChangeset, map[string]int64{"gold": 30}) {
						t.Fatalf("metadata = %v, changeset = %v", g.multiMeta, g.walletChangeset)
					}
					wantReceipt := `{"batch_id":"b-1","user_id":"u1","kills":3,"gold":30,"map_id":"map_01","granted_at":1800000000,"leaderboard_done":true,"score":3}`
					if len(g.storage) != 1 || g.storage[skey("reward_receipts", "b-1", "u1")] != wantReceipt {
						t.Fatalf("default receipt = %v", g.storage)
					}
				case RPCSubmitKill:
					wantJSON = `{"rank":0,"score":1,"success":true}`
				}
				if out != wantJSON {
					t.Fatalf("response = %s, want %s", out, wantJSON)
				}
				if mutation.name != RPCRewardKill && !reflect.DeepEqual(g.lbIDs, []string{"kills_alltime"}) {
					t.Fatalf("leaderboard writes = %v", g.lbIDs)
				}
			})
		}
	}
}

func assertNoEconomyAccess(t *testing.T, g *mockGranter) {
	t.Helper()
	if g.walletCalls != 0 || g.multiCalls != 0 || g.lbCalls != 0 || g.readCalls != 0 || g.writeCalls != 0 {
		t.Fatalf("unexpected access: wallet=%d multi=%d leaderboard=%d read=%d write=%d", g.walletCalls, g.multiCalls, g.lbCalls, g.readCalls, g.writeCalls)
	}
}

func TestMutationRPCs_RejectInvalidGameBeforeAnyAccess(t *testing.T) {
	for _, tc := range []struct {
		name  string
		games string
		game  string
		want  error
	}{
		{"outside allow-list", "cuvara,foo", "bar", gamectx.ErrGameNotAllowed},
		{"invalid game", "cuvara,foo", "bad-game", gamectx.ErrGameNotAllowed},
		{"invalid configuration", "cuvara,bad-game", "foo", runtime.NewError("internal error", 13)},
	} {
		for _, mutation := range mutationRPCs {
			t.Run(mutation.name+"/"+tc.name, func(t *testing.T) {
				g := &mockGranter{}
				_, err := mutation.rpc(gameContext(tc.games), noopLogger{}, g, gamePayload(t, mutation.payload, tc.game))
				if tc.want == gamectx.ErrGameNotAllowed {
					if !errors.Is(err, tc.want) {
						t.Fatalf("error = %v, want %v", err, tc.want)
					}
				} else {
					var rerr *runtime.Error
					if !errors.As(err, &rerr) || rerr.Code != 13 || rerr.Message != "internal error" {
						t.Fatalf("configuration error leaked: %v", err)
					}
				}
				assertNoEconomyAccess(t, g)
			})
		}
	}
}

func TestMutationRPCs_GameValidationFollowsCallerAndPayloadValidation(t *testing.T) {
	for _, mutation := range mutationRPCs {
		for _, payload := range []string{"{", gamePayload(t, mutation.payload, "bar")} {
			t.Run(mutation.name+"/client/"+payload, func(t *testing.T) {
				ctx := context.WithValue(clientCtx(), runtime.RUNTIME_CTX_ENV, map[string]string{"GAMES": "invalid-config"})
				g := &mockGranter{}
				if _, err := mutation.rpc(ctx, noopLogger{}, g, payload); !errors.Is(err, ErrServerOnly) {
					t.Fatalf("error = %v, want ErrServerOnly", err)
				}
				assertNoEconomyAccess(t, g)
			})
		}
		for _, payload := range []string{"{", `{}`} {
			t.Run(mutation.name+"/invalid payload/"+payload, func(t *testing.T) {
				g := &mockGranter{}
				if _, err := mutation.rpc(gameContext("invalid-config"), noopLogger{}, g, payload); codeOf(err) != 3 {
					t.Fatalf("error = %v, want payload error code 3", err)
				}
				assertNoEconomyAccess(t, g)
			})
		}
	}
}

type gameLeaderboardQuery struct {
	runtime.NakamaModule
	ids []string
}

func (m *gameLeaderboardQuery) LeaderboardRecordsList(_ context.Context, id string, _ []string, _ int, _ string, _ int64) ([]*api.LeaderboardRecord, []*api.LeaderboardRecord, string, string, error) {
	m.ids = append(m.ids, id)
	return []*api.LeaderboardRecord{{OwnerId: "u1", Score: 3, Rank: 1}}, nil, "", "", nil
}

func TestGetLeaderboard_GameFromSession(t *testing.T) {
	for _, tc := range []struct {
		name string
		game string
		want string
	}{
		{"non-default session", "foo", "foo.kills_alltime"},
		{"missing session variable", "", "kills_alltime"},
		{"explicit default session", "cuvara", "kills_alltime"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			ctx := context.WithValue(gameContext("cuvara,foo"), runtime.RUNTIME_CTX_USER_ID, "u1")
			if tc.game != "" {
				ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_VARS, map[string]string{gamectx.VarGame: tc.game})
			}
			g := &gameLeaderboardQuery{}
			// The payload remains ignored, including a conflicting game_id.
			out, err := GetLeaderboardRPC(ctx, noopLogger{}, nil, g, `{"game_id":"bar"}`)
			if err != nil {
				t.Fatal(err)
			}
			var response struct {
				LeaderboardID string `json:"leaderboard_id"`
				Records       []struct {
					UserID string `json:"user_id"`
				} `json:"records"`
			}
			if err := json.Unmarshal([]byte(out), &response); err != nil {
				t.Fatal(err)
			}
			if !reflect.DeepEqual(g.ids, []string{tc.want}) || response.LeaderboardID != tc.want || len(response.Records) != 1 || response.Records[0].UserID != "u1" {
				t.Fatalf("list calls = %v, response = %s", g.ids, out)
			}
		})
	}
}

func TestGetLeaderboard_DefaultGameWithGamesUnset(t *testing.T) {
	t.Setenv("GAMES", "")
	g := &gameLeaderboardQuery{}
	out, err := GetLeaderboardRPC(clientCtx(), noopLogger{}, nil, g, "{")
	if err != nil || !reflect.DeepEqual(g.ids, []string{"kills_alltime"}) || !strings.Contains(out, `"leaderboard_id":"kills_alltime"`) {
		t.Fatalf("default list calls = %v, response = %s, error = %v", g.ids, out, err)
	}
}

func TestGetLeaderboard_RejectInvalidGameBeforeRead(t *testing.T) {
	for _, tc := range []struct {
		games string
		code  int
	}{
		{"cuvara", 3},
		{"invalid-config", 13},
	} {
		t.Run(tc.games, func(t *testing.T) {
			ctx := context.WithValue(gameContext(tc.games), runtime.RUNTIME_CTX_USER_ID, "u1")
			ctx = context.WithValue(ctx, runtime.RUNTIME_CTX_VARS, map[string]string{gamectx.VarGame: "foo"})
			g := &gameLeaderboardQuery{}
			_, err := GetLeaderboardRPC(ctx, noopLogger{}, nil, g, "")
			if codeOf(err) != tc.code || len(g.ids) != 0 {
				t.Fatalf("error = %v, list calls = %v", err, g.ids)
			}
			if tc.code == 13 {
				var rerr *runtime.Error
				if !errors.As(err, &rerr) || rerr.Message != "internal error" {
					t.Fatalf("configuration error leaked: %v", err)
				}
			} else if !errors.Is(err, gamectx.ErrGameNotAllowed) {
				t.Fatalf("error = %v, want ErrGameNotAllowed", err)
			}
		})
	}
}

func TestSetupLeaderboards_Games(t *testing.T) {
	for _, tc := range []struct {
		name  string
		games []string
		ids   []string
	}{
		{"nil", nil, []string{"kills_alltime"}},
		{"default only", []string{"cuvara"}, []string{"kills_alltime"}},
		{"two games", []string{"cuvara", "foo"}, []string{"kills_alltime", "foo.kills_alltime"}},
		{"duplicates", []string{"foo", "cuvara", "foo", "cuvara", ""}, []string{"kills_alltime", "foo.kills_alltime"}},
		{"default board always exists", []string{"foo", "bar"}, []string{"kills_alltime", "foo.kills_alltime", "bar.kills_alltime"}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			g := &mockLbAdmin{}
			if err := setupLeaderboardsCore(serverCtx(), noopLogger{}, g, "", tc.games); err != nil {
				t.Fatal(err)
			}
			var ids []string
			for _, created := range g.creates {
				ids = append(ids, created.id)
				if !created.authoritative || created.sortOrder != "desc" || created.operator != "incr" {
					t.Fatalf("create = %+v", created)
				}
			}
			if !reflect.DeepEqual(ids, tc.ids) || len(g.calls) != 2*len(tc.ids) || len(g.deletes) != 0 {
				t.Fatalf("created ids = %v, calls = %v, deleted = %v", ids, g.calls, g.deletes)
			}
		})
	}
}

func TestSetupLeaderboards_ScopedAuthoritativeRules(t *testing.T) {
	for _, tc := range []struct {
		name     string
		auth     bool
		migrate  string
		wantErr  bool
		recreate bool
	}{
		{name: "authoritative existing board", auth: true},
		{name: "non-authoritative refuses startup", wantErr: true},
		{name: "unknown migrate refuses startup", migrate: "yes", wantErr: true},
		{name: "non-authoritative opt-in recreate", migrate: "recreate", recreate: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			g := &mockLbAdmin{existing: []*api.Leaderboard{board(true), {Id: "foo.kills_alltime", Authoritative: tc.auth}}}
			err := setupLeaderboardsCore(serverCtx(), noopLogger{}, g, tc.migrate, []string{"cuvara", "foo"})
			if tc.wantErr {
				if err == nil || !strings.Contains(err.Error(), "foo.kills_alltime exists with authoritative=false") {
					t.Fatalf("error = %v, want scoped authoritative error", err)
				}
			} else if err != nil {
				t.Fatal(err)
			}
			if tc.recreate {
				if !reflect.DeepEqual(g.deletes, []string{"foo.kills_alltime"}) || !reflect.DeepEqual(g.creates, []createCall{{"foo.kills_alltime", true, "desc", "incr"}}) || !reflect.DeepEqual(g.calls, []string{"get", "get", "delete", "create"}) {
					t.Fatalf("deletes = %v, creates = %v, calls = %v", g.deletes, g.creates, g.calls)
				}
			} else if len(g.deletes) != 0 || len(g.creates) != 0 || !reflect.DeepEqual(g.calls, []string{"get", "get"}) {
				t.Fatalf("deletes = %v, creates = %v, calls = %v", g.deletes, g.creates, g.calls)
			}
		})
	}
}
