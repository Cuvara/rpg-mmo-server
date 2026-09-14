package economy

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"time"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// RPCRewardKills is the batched replacement for the reward_kill + submit_kill
// pair. One call grants the gold AND the leaderboard score for every kill a
// player accumulated since the game server's last flush.
//
// Why it exists: the per-kill pair cost 2 HTTP requests and 2 separate meta-DB
// transactions per mob kill. At 200 players on the grindy end (a kill every 3s)
// that is ~133 commits/s of pure per-kill overhead — the first thing to
// saturate a shared small-VPS Postgres (rpg-mmo-server#233). Batching per
// killer turns it into one request and two increments per killer per flush
// interval, with identical semantics because both underlying operations are
// increments.
const RPCRewardKills = "reward_kills"

// MaxKillsPerBatch bounds a single batch. Far above anything a legitimate game
// server flushes (a 3s flush at one kill per second is 3), it exists so a
// corrupted or malicious payload cannot mint unbounded gold in one call. The
// game server splits larger backlogs itself (audit F07); a batch over the cap
// is rejected with CodeKillsOutOfRange so an older caller can tell "split
// this" apart from "malformed".
const MaxKillsPerBatch = 1000

// CodeKillsOutOfRange is the gRPC code (OUT_OF_RANGE) returned when kills is
// outside 1..MaxKillsPerBatch. Nothing was granted; the caller should split
// the batch — each half under a NEW batch id, since this one never reached
// the wallet.
const CodeKillsOutOfRange = 11

// ReceiptCollection is the Nakama storage collection holding one receipt per
// granted batch, keyed by batch_id and owned by the rewarded user. The receipt
// is the idempotency record: it is written in the same transaction as the
// wallet update (nk.MultiUpdate with a create-only version), so a batch id is
// granted at most once no matter how often the game server resends it.
const ReceiptCollection = "reward_receipts"

// Batch status values returned in RewardKillsResponse.Status.
const (
	// StatusGranted: gold and leaderboard score are both committed.
	StatusGranted = "granted"
	// StatusPartial: gold is committed, the leaderboard write failed. Resend
	// the SAME batch id: the replay skips the wallet and retries only the
	// leaderboard.
	StatusPartial = "partial"
)

// RewardKillsRequest is the payload the game server sends.
type RewardKillsRequest struct {
	UserID string `json:"user_id"`
	Kills  int64  `json:"kills"`
	MapID  string `json:"map_id"`
	// BatchID is the idempotency key. It must be stable across retries of the
	// same batch: a batch id that has already been granted is replayed from
	// its receipt without touching the wallet again.
	BatchID string `json:"batch_id"`
}

// RewardKillsResponse is returned to the caller.
type RewardKillsResponse struct {
	Success bool `json:"success"`
	// Status is StatusGranted or StatusPartial; see the constants.
	Status string `json:"status"`
	// Replayed is true when this batch id had already been granted by an
	// earlier call: the wallet was not touched again.
	Replayed bool `json:"replayed"`
	// Gold granted for this batch (GoldPerKill * kills), on the original call
	// and on replays alike.
	Gold int64 `json:"gold"`
	// Balance is the wallet's gold after the grant; only known on the call that
	// performed it, omitted on replays.
	Balance int64 `json:"balance,omitempty"`
	Score   int64 `json:"score"`
	Rank    int64 `json:"rank"`
	// LeaderboardError is set with StatusPartial.
	LeaderboardError string `json:"leaderboard_error,omitempty"`
}

// rewardReceipt is the stored per-batch record.
type rewardReceipt struct {
	BatchID   string `json:"batch_id"`
	UserID    string `json:"user_id"`
	Kills     int64  `json:"kills"`
	Gold      int64  `json:"gold"`
	MapID     string `json:"map_id,omitempty"`
	GrantedAt int64  `json:"granted_at"`
	// LeaderboardDone flips to true once the score increment for this batch
	// has been written; until then a replay retries the leaderboard.
	LeaderboardDone bool  `json:"leaderboard_done"`
	Score           int64 `json:"score,omitempty"`
	Rank            int64 `json:"rank,omitempty"`
}

// killGranter is the slice of runtime.NakamaModule the reward RPCs actually
// use, narrow so tests can implement it without mocking the whole module.
type killGranter interface {
	WalletUpdate(ctx context.Context, userID string, changeset map[string]int64,
		metadata map[string]interface{}, updateLedger bool) (map[string]int64, map[string]int64, error)
	LeaderboardRecordWrite(ctx context.Context, id, ownerID, username string,
		score, subscore int64, metadata map[string]interface{},
		overrideOperator *int) (*api.LeaderboardRecord, error)
	StorageRead(ctx context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error)
	StorageWrite(ctx context.Context, writes []*runtime.StorageWrite) ([]*api.StorageObjectAck, error)
	MultiUpdate(ctx context.Context, accountUpdates []*runtime.AccountUpdate, storageWrites []*runtime.StorageWrite,
		storageDeletes []*runtime.StorageDelete, walletUpdates []*runtime.WalletUpdate, updateLedger bool,
	) ([]*api.StorageObjectAck, []*runtime.WalletUpdateResult, error)
}

// RewardKillsRPC grants gold and leaderboard score for a batch of kills,
// exactly once per batch id.
//
// Callable only via runtime.http_key: a client session is rejected with code 7
// before the payload is parsed (see requireServerCaller).
//
// Contract, load-bearing for the game server's retry policy:
//
//   - An error means NOTHING was granted by this call. The wallet update and
//     the batch receipt commit in one transaction (nk.MultiUpdate), so a
//     failed call leaves neither behind and the batch may be re-sent.
//   - Because the receipt is the idempotency key, re-sending the SAME batch
//     id is always safe — including after a timeout where the caller cannot
//     know whether the first attempt committed. A replay never touches the
//     wallet again (Replayed=true in the response).
//   - The leaderboard write is not part of that transaction (Nakama's
//     MultiUpdate does not cover leaderboards). If it fails after the wallet
//     committed, the call still succeeds with Status=partial; a replay of the
//     same batch id retries only the leaderboard until it lands, so score
//     converges without a second gold grant (ADR-6: double gold is never
//     acceptable, bounded score delay is).
func RewardKillsRPC(ctx context.Context, logger runtime.Logger, db *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	return rewardKillsCore(ctx, logger, nk, payload, time.Now)
}

// rewardKillsCore is RewardKillsRPC against the narrow interface; the split
// exists so tests can drive it with a mock.
func rewardKillsCore(ctx context.Context, logger runtime.Logger, nk killGranter, payload string, now func() time.Time) (string, error) {
	if err := requireServerCaller(ctx); err != nil {
		return "", err
	}
	var req RewardKillsRequest
	if err := json.Unmarshal([]byte(payload), &req); err != nil {
		return "", runtime.NewError("invalid payload", 3) // INVALID_ARGUMENT
	}
	if req.UserID == "" {
		return "", runtime.NewError("user_id is required", 3)
	}
	if req.BatchID == "" {
		return "", runtime.NewError("batch_id is required", 3)
	}
	if req.Kills <= 0 || req.Kills > MaxKillsPerBatch {
		return "", runtime.NewError(
			fmt.Sprintf("kills must be in 1..%d", MaxKillsPerBatch), CodeKillsOutOfRange)
	}

	// Fast path: already granted? Replay from the receipt.
	receipt, err := readReceipt(ctx, nk, req.UserID, req.BatchID)
	if err != nil {
		return "", runtime.NewError(fmt.Sprintf("receipt lookup failed: %v", err), 13) // INTERNAL
	}
	if receipt != nil {
		return replayFromReceipt(ctx, logger, nk, receipt)
	}

	gold := GoldPerKill * req.Kills
	receipt = &rewardReceipt{
		BatchID:   req.BatchID,
		UserID:    req.UserID,
		Kills:     req.Kills,
		Gold:      gold,
		MapID:     req.MapID,
		GrantedAt: now().Unix(),
	}
	receiptJSON, _ := json.Marshal(receipt)

	// Receipt (create-only: Version "*") and wallet update in ONE transaction.
	// Either both commit or neither does; a concurrent duplicate loses on the
	// version check and the whole update, wallet included, rolls back.
	_, results, err := nk.MultiUpdate(ctx,
		nil,
		[]*runtime.StorageWrite{{
			Collection:      ReceiptCollection,
			Key:             req.BatchID,
			UserID:          req.UserID,
			Value:           string(receiptJSON),
			Version:         "*",
			PermissionRead:  0, // server only; the receipt is an audit record, not player data
			PermissionWrite: 0,
		}},
		nil,
		[]*runtime.WalletUpdate{{
			UserID:    req.UserID,
			Changeset: map[string]int64{"gold": gold},
			Metadata: map[string]interface{}{
				"source":   "enemy_kills",
				"kills":    req.Kills,
				"map_id":   req.MapID,
				"batch_id": req.BatchID,
			},
		}},
		true)
	if err != nil {
		// Two duplicates racing: the loser's write is rejected on the version
		// check. Whether that or a real failure, the transaction rolled back;
		// the receipt tells the two apart.
		if existing, rerr := readReceipt(ctx, nk, req.UserID, req.BatchID); rerr == nil && existing != nil {
			return replayFromReceipt(ctx, logger, nk, existing)
		}
		logger.Error("batch grant failed for %s (%d kills, batch %s): %v", req.UserID, req.Kills, req.BatchID, err)
		return "", runtime.NewError(fmt.Sprintf("wallet update failed: %v", err), 13) // INTERNAL
	}

	resp := RewardKillsResponse{Success: true, Gold: gold}
	if len(results) > 0 && results[0] != nil {
		resp.Balance = results[0].Updated["gold"]
	}
	writeLeaderboard(ctx, logger, nk, receipt, &resp)

	// One line per FLUSH, not per kill — reward_kill's per-kill Info line was
	// itself part of the amplification this RPC removes.
	logger.Debug("Awarded %d gold to %s for %d kills on %s (batch %s, status %s)",
		gold, req.UserID, req.Kills, req.MapID, req.BatchID, resp.Status)

	out, _ := json.Marshal(resp)
	return string(out), nil
}

// replayFromReceipt answers a re-sent batch id: gold is never granted twice;
// the leaderboard is retried only if the receipt says it is still pending.
func replayFromReceipt(ctx context.Context, logger runtime.Logger, nk killGranter, receipt *rewardReceipt) (string, error) {
	resp := RewardKillsResponse{Success: true, Replayed: true, Gold: receipt.Gold}
	if receipt.LeaderboardDone {
		resp.Status = StatusGranted
		resp.Score = receipt.Score
		resp.Rank = receipt.Rank
	} else {
		writeLeaderboard(ctx, logger, nk, receipt, &resp)
	}
	logger.Debug("Replayed batch %s for %s (%d kills): status %s", receipt.BatchID, receipt.UserID, receipt.Kills, resp.Status)
	out, _ := json.Marshal(resp)
	return string(out), nil
}

// writeLeaderboard increments the score for the receipt's batch and records
// the outcome in the receipt so a later replay does not increment again. It
// never returns an error: gold is already committed at this point, and an
// error would invite a retry of the wallet — the response carries the state.
func writeLeaderboard(ctx context.Context, logger runtime.Logger, nk killGranter, receipt *rewardReceipt, resp *RewardKillsResponse) {
	record, err := nk.LeaderboardRecordWrite(ctx,
		LeaderboardKillsAllTime, receipt.UserID, "", receipt.Kills, 0, nil, nil)
	if err != nil {
		logger.Error("leaderboard write failed for %s (%d kills, batch %s, gold already granted): %v",
			receipt.UserID, receipt.Kills, receipt.BatchID, err)
		resp.Status = StatusPartial
		resp.LeaderboardError = err.Error()
		return
	}
	resp.Status = StatusGranted
	resp.Score = record.Score
	resp.Rank = record.Rank

	receipt.LeaderboardDone = true
	receipt.Score = record.Score
	receipt.Rank = record.Rank
	value, _ := json.Marshal(receipt)
	if _, err := nk.StorageWrite(ctx, []*runtime.StorageWrite{{
		Collection:      ReceiptCollection,
		Key:             receipt.BatchID,
		UserID:          receipt.UserID,
		Value:           string(value),
		PermissionRead:  0,
		PermissionWrite: 0,
	}}); err != nil {
		// Score is committed but the receipt still says pending: a replay of
		// this exact batch would increment the score once more. Bounded score
		// drift on a double fault is the accepted cost (ADR-6); it is logged
		// so it can be audited.
		logger.Error("receipt update failed for batch %s (%s): a replay may double-count the score: %v",
			receipt.BatchID, receipt.UserID, err)
	}
}

func readReceipt(ctx context.Context, nk killGranter, userID, batchID string) (*rewardReceipt, error) {
	objs, err := nk.StorageRead(ctx, []*runtime.StorageRead{{
		Collection: ReceiptCollection, Key: batchID, UserID: userID,
	}})
	if err != nil {
		return nil, err
	}
	for _, o := range objs {
		if o.GetKey() != batchID {
			continue
		}
		var r rewardReceipt
		if err := json.Unmarshal([]byte(o.GetValue()), &r); err != nil {
			return nil, fmt.Errorf("corrupt receipt %s: %w", batchID, err)
		}
		return &r, nil
	}
	return nil, nil
}
