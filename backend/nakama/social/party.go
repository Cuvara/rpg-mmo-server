// Package social implements the party RPCs for the Nakama Go runtime plugin.
//
// # Why storage RPCs and not Nakama's realtime Party API
//
// Nakama ships a realtime (socket) Party API. It is not usable here for two
// independent reasons:
//
//  1. This client talks to Nakama over HTTP for every meta service (auth,
//     economy, leaderboard). It holds its two realtime sockets against the
//     gateway and the game server instead (ADR-3), so there is no Nakama
//     socket to hang a realtime party off.
//  2. The gateway must ask "is user U really in party P" server-to-server
//     before it allocates a dungeon instance. Realtime party state lives in
//     the socket layer and is not addressable from a runtime.http_key call;
//     a storage-backed party is, which is what party_get does.
//
// So a party is two storage records and four RPCs over them.
//
// # Storage model
//
//	collection "party",        key <party id>, owner SYSTEM  -> Party
//	collection "party_member", key "current",  owner <user>  -> membership
//
// The Party record is server-owned (empty UserID, which Nakama maps to the
// system owner) with permissions 0/0, so no client can read or write party
// state through Nakama's public storage API — only through these RPCs, which
// enforce the cap and the leadership rules. The membership record is the
// reverse index that makes "at most one party per user" enforceable with a
// create-only write rather than a scan.
//
// # Concurrency
//
// Every mutation is one nk.MultiUpdate carrying BOTH records, so the party and
// the reverse index can never disagree. The Party write always carries the
// version read at the start of the attempt, and the membership write carries
// the create-only version "*". Nakama rejects the whole update — storage and
// all — if either version check fails, which is what stops two simultaneous
// joins from taking a 3-member party to 5: one write wins, the other is
// rejected, re-reads a 4-member party and is answered ErrPartyFull.
package social

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/heroiclabs/nakama-common/api"
	"github.com/heroiclabs/nakama-common/runtime"
)

// RPC names registered from main.go.
const (
	RPCPartyCreate = "party_create"
	RPCPartyJoin   = "party_join"
	RPCPartyLeave  = "party_leave"
	RPCPartyGet    = "party_get"
)

// MaxPartyMembers is the hard cap on party size, leader included.
//
// 4 is the number the root CLAUDE.md states under Social ("Party API (max 4)")
// and the one nakama/CLAUDE.md repeats ("CreateParty (open=true, max=4)"). It
// is enforced here rather than left to the client because party size decides
// dungeon instance sizing and loot splitting downstream.
const MaxPartyMembers = 4

// Storage locations. See the package doc for the ownership and permission
// rationale.
const (
	// PartyCollection holds one system-owned record per live party, keyed by
	// party id.
	PartyCollection = "party"
	// MembershipCollection holds one record per user that is in a party,
	// keyed by MembershipKey and owned by that user.
	MembershipCollection = "party_member"
	// MembershipKey is the single key inside MembershipCollection. A user is
	// in at most one party, so one key per user is all that is needed — and
	// the fixed key is what makes the create-only ("*") write enforce it.
	MembershipKey = "current"
)

// maxWriteAttempts bounds the optimistic-concurrency retry loop.
//
// Each attempt is a fresh read plus one MultiUpdate. A retry only happens when
// a concurrent writer changed the record under us, so the loop's length is
// bounded in practice by the number of players racing on ONE party — at most
// MaxPartyMembers-1 of them can win, and every winner moves the party closer
// to full, at which point the remaining attempts fail fast on the cap instead
// of retrying. 5 leaves headroom over that without letting a pathological
// case spin.
const maxWriteAttempts = 5

// Party is the stored party record.
type Party struct {
	ID string `json:"id"`
	// LeaderID is always one of Members.
	LeaderID string `json:"leader_id"`
	// Members is in join order, leader first at creation. Order is
	// load-bearing: leadership transfers to Members[0] after the leader is
	// removed, i.e. to the longest-standing remaining member.
	Members   []string `json:"members"`
	CreatedAt int64    `json:"created_at"`
	UpdatedAt int64    `json:"updated_at"`
}

// membership is the per-user reverse index: which party this user is in.
type membership struct {
	PartyID  string `json:"party_id"`
	JoinedAt int64  `json:"joined_at"`
}

// partyStore is the narrow slice of runtime.NakamaModule the party logic uses.
// runtime.NakamaModule satisfies it; tests implement it with a version-aware
// in-memory store.
type partyStore interface {
	StorageRead(ctx context.Context, reads []*runtime.StorageRead) ([]*api.StorageObject, error)
	MultiUpdate(ctx context.Context, accountUpdates []*runtime.AccountUpdate, storageWrites []*runtime.StorageWrite,
		storageDeletes []*runtime.StorageDelete, walletUpdates []*runtime.WalletUpdate, updateLedger bool,
	) ([]*api.StorageObjectAck, []*runtime.WalletUpdateResult, error)
}

// IsMember reports whether userID is in the party. It is the check the gateway
// performs on the party_get response before allocating a dungeon instance.
func (p *Party) IsMember(userID string) bool {
	for _, m := range p.Members {
		if m == userID {
			return true
		}
	}
	return false
}

// newPartyID returns a random 128-bit hex party id.
//
// Nakama's Go runtime exposes no uuid helper, and the id only has to be
// unguessable and collision-free — a party id is a bearer-ish handle: knowing
// one lets you attempt a join. crypto/rand, not math/rand, for that reason.
func newPartyID() (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", fmt.Errorf("generate party id: %w", err)
	}
	return hex.EncodeToString(b[:]), nil
}

// callerID returns the authenticated caller's user id.
//
// A runtime.http_key (server-to-server) call carries no user id, so this also
// serves as the "client session required" guard for the mutation RPCs — the
// mirror image of economy.requireServerCaller, which rejects when the marker
// IS present.
func callerID(ctx context.Context) (string, error) {
	userID, _ := ctx.Value(runtime.RUNTIME_CTX_USER_ID).(string)
	if userID == "" {
		return "", ErrUnauthenticated
	}
	return userID, nil
}

// readParty loads a party and its storage version. A missing party returns a
// nil Party and no error: "gone" is a normal state, not a failure.
func readParty(ctx context.Context, nk partyStore, partyID string) (*Party, string, error) {
	objs, err := nk.StorageRead(ctx, []*runtime.StorageRead{{
		Collection: PartyCollection,
		Key:        partyID,
		UserID:     "", // system-owned
	}})
	if err != nil {
		return nil, "", fmt.Errorf("read party %s: %w", partyID, err)
	}
	for _, o := range objs {
		if o.GetCollection() != PartyCollection || o.GetKey() != partyID {
			continue
		}
		var p Party
		if err := json.Unmarshal([]byte(o.GetValue()), &p); err != nil {
			return nil, "", fmt.Errorf("corrupt party record %s: %w", partyID, err)
		}
		return &p, o.GetVersion(), nil
	}
	return nil, "", nil
}

// readMembership loads a user's current-party index and its storage version.
func readMembership(ctx context.Context, nk partyStore, userID string) (*membership, string, error) {
	objs, err := nk.StorageRead(ctx, []*runtime.StorageRead{{
		Collection: MembershipCollection,
		Key:        MembershipKey,
		UserID:     userID,
	}})
	if err != nil {
		return nil, "", fmt.Errorf("read membership %s: %w", userID, err)
	}
	for _, o := range objs {
		if o.GetCollection() != MembershipCollection || o.GetKey() != MembershipKey {
			continue
		}
		var m membership
		if err := json.Unmarshal([]byte(o.GetValue()), &m); err != nil {
			return nil, "", fmt.Errorf("corrupt membership record %s: %w", userID, err)
		}
		return &m, o.GetVersion(), nil
	}
	return nil, "", nil
}

// partyWrite builds the storage write for a party record at a known version.
// version is "*" for a create and the version read at the start of the attempt
// for an update; either way a concurrent writer makes this write fail.
func partyWrite(p *Party, version string) *runtime.StorageWrite {
	value, _ := json.Marshal(p) // Party has no unmarshalable field
	return &runtime.StorageWrite{
		Collection:      PartyCollection,
		Key:             p.ID,
		UserID:          "", // system-owned: no client may read or write it directly
		Value:           string(value),
		Version:         version,
		PermissionRead:  0,
		PermissionWrite: 0,
	}
}

// membershipWrite builds the create-only write for a user's party index. The
// "*" version is what enforces one-party-at-a-time: if the user already has an
// index record, the whole MultiUpdate is rejected.
func membershipWrite(userID, partyID string, now time.Time) *runtime.StorageWrite {
	value, _ := json.Marshal(membership{PartyID: partyID, JoinedAt: now.Unix()})
	return &runtime.StorageWrite{
		Collection:      MembershipCollection,
		Key:             MembershipKey,
		UserID:          userID,
		Value:           string(value),
		Version:         "*",
		PermissionRead:  1, // owner may read "which party am I in" without an RPC
		PermissionWrite: 0, // server-authoritative: only these RPCs change it
	}
}

// CreateParty makes the caller the leader of a fresh party.
//
// The party record and the caller's membership index are written in one
// MultiUpdate, both create-only, so a caller who is already in a party gets
// ErrAlreadyInParty and no orphan party is left behind.
func CreateParty(ctx context.Context, nk partyStore, userID string, now func() time.Time) (*Party, error) {
	if userID == "" {
		return nil, ErrUnauthenticated
	}
	if m, _, err := readMembership(ctx, nk, userID); err != nil {
		return nil, fmt.Errorf("create party: %w", err)
	} else if m != nil {
		return nil, ErrAlreadyInParty
	}

	id, err := newPartyID()
	if err != nil {
		return nil, fmt.Errorf("create party: %w", err)
	}
	ts := now()
	p := &Party{
		ID:        id,
		LeaderID:  userID,
		Members:   []string{userID},
		CreatedAt: ts.Unix(),
		UpdatedAt: ts.Unix(),
	}

	if _, _, err := nk.MultiUpdate(ctx, nil,
		[]*runtime.StorageWrite{partyWrite(p, "*"), membershipWrite(userID, id, ts)},
		nil, nil, false); err != nil {
		// The party id is fresh and unguessable, so the only version check
		// that can realistically fail here is the membership one: the caller
		// joined or created a party between our read and this write.
		if m, _, rerr := readMembership(ctx, nk, userID); rerr == nil && m != nil {
			return nil, ErrAlreadyInParty
		}
		return nil, fmt.Errorf("create party %s: %w", id, err)
	}
	return p, nil
}

// JoinParty adds the caller to an existing party.
//
// One party at a time — this FAILS, it does not silently move the caller.
// Auto-leaving the old party would make an additive-looking call destructive:
// if the caller happened to be leading a party, a mistyped or replayed join
// would transfer that leadership away, or delete the party outright if they
// were its last member. A client that wants to switch calls party_leave first;
// the error names the condition so it can do exactly that.
//
// Re-joining the party the caller is ALREADY in is not an error. It returns
// the current party unchanged, so a client that retries after a timeout cannot
// be told "already in a party" about itself.
//
// Concurrency: bounded read-modify-write. Each attempt re-reads the party, so
// a retry always validates the cap against the state the winner just wrote.
func JoinParty(ctx context.Context, nk partyStore, userID, partyID string, now func() time.Time) (*Party, error) {
	if userID == "" {
		return nil, ErrUnauthenticated
	}
	if partyID == "" {
		return nil, ErrPartyIDRequired
	}

	var lastErr error
	for attempt := 0; attempt < maxWriteAttempts; attempt++ {
		m, _, err := readMembership(ctx, nk, userID)
		if err != nil {
			return nil, fmt.Errorf("join party: %w", err)
		}
		if m != nil {
			if m.PartyID != partyID {
				return nil, ErrAlreadyInParty
			}
			// Already in this exact party: idempotent success.
			p, _, err := readParty(ctx, nk, partyID)
			if err != nil {
				return nil, fmt.Errorf("join party: %w", err)
			}
			if p == nil {
				// Index survived its party (a torn leave). Report the party
				// as gone; party_leave will clear the stale index.
				return nil, ErrPartyNotFound
			}
			return p, nil
		}

		p, version, err := readParty(ctx, nk, partyID)
		if err != nil {
			return nil, fmt.Errorf("join party: %w", err)
		}
		if p == nil {
			return nil, ErrPartyNotFound
		}
		// The cap is re-checked on every attempt, against the version this
		// attempt writes back. That pairing is the guard: a stale read cannot
		// commit.
		if len(p.Members) >= MaxPartyMembers {
			return nil, ErrPartyFull
		}

		ts := now()
		updated := &Party{
			ID:        p.ID,
			LeaderID:  p.LeaderID,
			Members:   append(append(make([]string, 0, len(p.Members)+1), p.Members...), userID),
			CreatedAt: p.CreatedAt,
			UpdatedAt: ts.Unix(),
		}
		if _, _, err := nk.MultiUpdate(ctx, nil,
			[]*runtime.StorageWrite{partyWrite(updated, version), membershipWrite(userID, partyID, ts)},
			nil, nil, false); err != nil {
			lastErr = err
			continue // lost a version check (or a transient failure): re-read and re-validate
		}
		return updated, nil
	}
	return nil, fmt.Errorf("join party %s after %d attempts: %w: %w",
		partyID, maxWriteAttempts, ErrPartyBusy, lastErr)
}

// LeaveParty removes the caller from whatever party they are in.
//
//   - Last member out deletes the party record, so a party never outlives its
//     members and a stale party id reads as ErrPartyNotFound.
//   - If the leader leaves and others remain, leadership transfers to
//     Members[0] after removal — the longest-standing remaining member. No
//     election, no vote: the point is that a party always has exactly one
//     leader, because the gateway allocates a dungeon per leader.
//
// It returns the party as it stands after the departure, or nil when the
// party was deleted.
func LeaveParty(ctx context.Context, nk partyStore, userID string, now func() time.Time) (*Party, error) {
	if userID == "" {
		return nil, ErrUnauthenticated
	}

	var lastErr error
	for attempt := 0; attempt < maxWriteAttempts; attempt++ {
		m, memberVersion, err := readMembership(ctx, nk, userID)
		if err != nil {
			return nil, fmt.Errorf("leave party: %w", err)
		}
		if m == nil {
			return nil, ErrNotInParty
		}

		p, version, err := readParty(ctx, nk, m.PartyID)
		if err != nil {
			return nil, fmt.Errorf("leave party: %w", err)
		}

		memberDelete := &runtime.StorageDelete{
			Collection: MembershipCollection,
			Key:        MembershipKey,
			UserID:     userID,
			Version:    memberVersion,
		}

		// Stale index: the party is already gone. Clear the index so the user
		// is not stuck unable to join anything, and report success.
		if p == nil {
			if _, _, err := nk.MultiUpdate(ctx, nil, nil,
				[]*runtime.StorageDelete{memberDelete}, nil, false); err != nil {
				lastErr = err
				continue
			}
			return nil, nil
		}

		remaining := make([]string, 0, len(p.Members))
		for _, id := range p.Members {
			if id != userID {
				remaining = append(remaining, id)
			}
		}
		if len(remaining) == len(p.Members) {
			// The index says this party, the party does not list the user.
			// Drop the index; there is nothing to remove from the party.
			if _, _, err := nk.MultiUpdate(ctx, nil, nil,
				[]*runtime.StorageDelete{memberDelete}, nil, false); err != nil {
				lastErr = err
				continue
			}
			return p, nil
		}

		// Last member out: delete the party and the index in one update.
		if len(remaining) == 0 {
			if _, _, err := nk.MultiUpdate(ctx, nil, nil, []*runtime.StorageDelete{
				{Collection: PartyCollection, Key: p.ID, UserID: "", Version: version},
				memberDelete,
			}, nil, false); err != nil {
				lastErr = err
				continue
			}
			return nil, nil
		}

		leader := p.LeaderID
		if leader == userID {
			leader = remaining[0]
		}
		updated := &Party{
			ID:        p.ID,
			LeaderID:  leader,
			Members:   remaining,
			CreatedAt: p.CreatedAt,
			UpdatedAt: now().Unix(),
		}
		if _, _, err := nk.MultiUpdate(ctx, nil,
			[]*runtime.StorageWrite{partyWrite(updated, version)},
			[]*runtime.StorageDelete{memberDelete}, nil, false); err != nil {
			lastErr = err
			continue
		}
		return updated, nil
	}
	return nil, fmt.Errorf("leave party after %d attempts: %w: %w",
		maxWriteAttempts, ErrPartyBusy, lastErr)
}

// GetParty reads a party by id. No caller check: party_get is the one party
// RPC the gateway calls server-to-server.
func GetParty(ctx context.Context, nk partyStore, partyID string) (*Party, error) {
	if partyID == "" {
		return nil, ErrPartyIDRequired
	}
	p, _, err := readParty(ctx, nk, partyID)
	if err != nil {
		return nil, fmt.Errorf("get party: %w", err)
	}
	if p == nil {
		return nil, ErrPartyNotFound
	}
	return p, nil
}

// ---------------------------------------------------------------------------
// RPC payloads
// ---------------------------------------------------------------------------

// PartyRequest is the payload of party_join and party_get. party_create and
// party_leave take no payload: their subject is the caller.
type PartyRequest struct {
	PartyID string `json:"party_id"`
}

// PartyResponse is returned by every party RPC.
type PartyResponse struct {
	// PartyID is empty when the call left the caller in no party (the last
	// member leaving, which deletes the party).
	PartyID  string   `json:"party_id,omitempty"`
	LeaderID string   `json:"leader_id,omitempty"`
	Members  []string `json:"members,omitempty"`
	// MemberCount is served alongside Members so a caller that only needs the
	// size does not have to count, and so the cap is visible in every reply.
	MemberCount int `json:"member_count"`
	// MaxMembers echoes MaxPartyMembers so a client can render "3/4" without
	// hardcoding the cap.
	MaxMembers int   `json:"max_members"`
	CreatedAt  int64 `json:"created_at,omitempty"`
	UpdatedAt  int64 `json:"updated_at,omitempty"`
}

func respond(p *Party) (string, error) {
	resp := PartyResponse{MaxMembers: MaxPartyMembers}
	if p != nil {
		resp.PartyID = p.ID
		resp.LeaderID = p.LeaderID
		resp.Members = p.Members
		resp.MemberCount = len(p.Members)
		resp.CreatedAt = p.CreatedAt
		resp.UpdatedAt = p.UpdatedAt
	}
	out, err := json.Marshal(resp)
	if err != nil {
		return "", fmt.Errorf("marshal party response: %w", err)
	}
	return string(out), nil
}

// decodeRequest parses an optional JSON payload. An empty payload is valid and
// yields a zero request; callers that need party_id check it themselves.
func decodeRequest(payload string) (PartyRequest, error) {
	var req PartyRequest
	if payload == "" {
		return req, nil
	}
	if err := json.Unmarshal([]byte(payload), &req); err != nil {
		return req, ErrInvalidPayload
	}
	return req, nil
}

// ---------------------------------------------------------------------------
// RPC handlers — registered in main.go
// ---------------------------------------------------------------------------

// PartyCreateRPC handles party_create. Client session required.
func PartyCreateRPC(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, _ string) (string, error) {
	userID, err := callerID(ctx)
	if err != nil {
		return "", err
	}
	if !allowPartyWrite(userID) {
		return "", ErrRateLimited
	}
	p, err := CreateParty(ctx, nk, userID, time.Now)
	if err != nil {
		return "", clientError(logger, "party_create", userID, err)
	}
	logger.Debug("party %s created by %s", p.ID, userID)
	return respond(p)
}

// PartyJoinRPC handles party_join. Client session required.
func PartyJoinRPC(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	userID, err := callerID(ctx)
	if err != nil {
		return "", err
	}
	if !allowPartyWrite(userID) {
		return "", ErrRateLimited
	}
	req, err := decodeRequest(payload)
	if err != nil {
		return "", err
	}
	if req.PartyID == "" {
		return "", ErrPartyIDRequired
	}
	p, err := JoinParty(ctx, nk, userID, req.PartyID, time.Now)
	if err != nil {
		return "", clientError(logger, "party_join", userID, err)
	}
	logger.Debug("user %s joined party %s (%d/%d)", userID, p.ID, len(p.Members), MaxPartyMembers)
	return respond(p)
}

// PartyLeaveRPC handles party_leave. Client session required. The response has
// an empty party_id when the caller was the last member, because the party is
// then deleted.
func PartyLeaveRPC(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, _ string) (string, error) {
	userID, err := callerID(ctx)
	if err != nil {
		return "", err
	}
	if !allowPartyWrite(userID) {
		return "", ErrRateLimited
	}
	p, err := LeaveParty(ctx, nk, userID, time.Now)
	if err != nil {
		return "", clientError(logger, "party_leave", userID, err)
	}
	logger.Debug("user %s left party (remaining: %d)", userID, len(p.GetMembers()))
	return respond(p)
}

// PartyGetRPC handles party_get.
//
// Callable BOTH by a client session and by the gateway over runtime.http_key —
// deliberately, and it is the only party RPC that is. The gateway calls it to
// verify that a user entering a dungeon is really in the party they claim
// (`GetParty(...).IsMember(userID)`), and the gateway has no client session.
// Because it is read-only and the party record it returns holds nothing but
// user ids the caller could already be told by the party UI, the exposure of
// leaving it open to clients is invite/roster flows, not privilege.
//
// It is also NOT rate-limited per user for the same reason: the gateway's
// calls carry no user id to key a bucket on, and keying them all to the empty
// string would let one player's joins throttle the whole cluster's dungeon
// allocations.
func PartyGetRPC(ctx context.Context, logger runtime.Logger, _ *sql.DB, nk runtime.NakamaModule, payload string) (string, error) {
	req, err := decodeRequest(payload)
	if err != nil {
		return "", err
	}
	if req.PartyID == "" {
		return "", ErrPartyIDRequired
	}
	p, err := GetParty(ctx, nk, req.PartyID)
	if err != nil {
		callerLabel, _ := ctx.Value(runtime.RUNTIME_CTX_USER_ID).(string)
		if callerLabel == "" {
			callerLabel = "server"
		}
		return "", clientError(logger, "party_get", callerLabel, err)
	}
	return respond(p)
}

// GetMembers is nil-safe so a deleted party can be logged without a branch.
func (p *Party) GetMembers() []string {
	if p == nil {
		return nil
	}
	return p.Members
}

// clientError passes the typed runtime errors above through untouched and
// collapses everything else to ErrInternal after logging it. Storage error
// text names collections and keys; a client gets none of that.
func clientError(logger runtime.Logger, rpc, userID string, err error) error {
	// The typed errors are *runtime.Error, and the retry-exhausted paths wrap
	// ErrPartyBusy together with the underlying storage error, so unwrap
	// rather than type-assert. ErrPartyBusy reaching a client as ABORTED is
	// the point: it is the one party error worth retrying.
	var rerr *runtime.Error
	if errors.As(err, &rerr) {
		if errors.Is(err, ErrPartyBusy) {
			// Contention that outlasted every retry is the signal that the
			// retry budget is wrong; the underlying storage error is only in
			// the log, never in the response.
			logger.Warn("%s gave up on contention for %s: %v", rpc, userID, err)
		}
		return rerr
	}
	logger.Error("%s failed for %s: %v", rpc, userID, err)
	return ErrInternal
}
