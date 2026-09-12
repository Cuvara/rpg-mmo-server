package transfer

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/duycuong/rpg-mmo/shared/jwt"
	"github.com/duycuong/rpg-mmo/shared/storage"
)

// Map transfer (MsgTransferMap / MsgTransferMapResp, types 13/14) is
// client-driven and handled entirely by the game server: the client sends
// MsgTransferMap to its current game server, which saves state, responds,
// removes the entity, and closes the connection. The client then sends a
// fresh MsgEnterWorld to the gateway (the existing flow), and the gateway
// assigns a new map server and mints a join token. No gateway code change
// is required for map transfer -- and ADR-26 decision 7 makes LEAVING a
// dungeon the same path, which is why there is no exit code here either.
//
// Types 11/12 (Ping/Pong) and 15 (Kick) are reserved for protocol and
// session workers respectively.

const (
	// DefaultAllocationClaimTTL bounds how long one gateway may hold the right
	// to allocate for a party. It is the safety net for a gateway that dies
	// mid-allocation: without it that party could never enter again, because
	// every later member would lose an election to a holder that no longer
	// exists.
	DefaultAllocationClaimTTL = 30 * time.Second

	// DefaultInstanceTTL bounds how long a party -> instance mapping survives.
	// It is not the length of a dungeon run: the pod refreshes nothing here,
	// and the mapping only has to outlive the entry of the LAST member. A dead
	// pod takes its servers:id: entry with it, so a mapping that outlived the
	// pod would hand a client an address that is not answering.
	DefaultInstanceTTL = 10 * time.Minute

	// dungeonPollInterval is how often a member that lost the allocation
	// election re-checks for the winner's answer.
	dungeonPollInterval = 150 * time.Millisecond
)

// DungeonAllocator is the half of registry.RegistryService this package needs.
type DungeonAllocator interface {
	AllocateDungeon(ctx context.Context, contentID string) (storage.ServerInfo, error)
	GetServer(ctx context.Context, serverID string) (storage.ServerInfo, error)
}

// DungeonDeps are the collaborators AssignDungeon needs.
type DungeonDeps struct {
	Registry DungeonAllocator
	Index    storage.DungeonIndex
	Party    PartyMembership
	JoinKeys jwt.Keyring

	// ClaimTTL and InstanceTTL default to the constants above when zero.
	ClaimTTL    time.Duration
	InstanceTTL time.Duration
}

// AssignDungeon resolves one member's entry into a dungeon instance for their
// party, allocating the instance if they are the first to arrive.
//
// The shape is an election, not a lock, and the difference matters: the loser
// of the election does not wait for the winner to release anything, it waits
// for the winner's ANSWER. A lock would serialise four party members into four
// sequential allocations if the winner crashed; polling for the published entry
// means the only cost of losing is latency, and the only cost of the winner
// dying is the claim TTL.
//
// Order of operations, each step existing because skipping it is wrong:
//
//  1. Verify membership FIRST (ADR-26 decision 3). A client that names someone
//     else's party must not be able to make this gateway allocate a pod, and an
//     allocation is the most expensive thing an unauthenticated-for-this-party
//     request could trigger.
//  2. Look up an existing instance. The common case for members 2..4.
//  3. Claim the right to allocate. Exactly one member wins.
//  4. Winner allocates and publishes; losers poll step 2 until the budget runs
//     out.
//  5. Mint the join token LAST, from the server the registry returned -- never
//     from the allocation response, whose address ADR-16 measured as not
//     dialable, and never before the pod is registered, because a join token is
//     single-use and short-lived.
func AssignDungeon(ctx context.Context, userID, partyID, contentID string, deps DungeonDeps) (AssignResult, error) {
	if partyID == "" {
		return AssignResult{}, fmt.Errorf("assign dungeon: empty party id")
	}
	if deps.Registry == nil || deps.Index == nil || deps.Party == nil {
		return AssignResult{}, fmt.Errorf("assign dungeon: incomplete dependencies")
	}

	member, err := deps.Party.IsMember(ctx, partyID, userID)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign dungeon: %w", err)
	}
	if !member {
		return AssignResult{}, fmt.Errorf("assign dungeon: user %s party %s: %w", userID, partyID, ErrNotAPartyMember)
	}

	claimTTL := deps.ClaimTTL
	if claimTTL <= 0 {
		claimTTL = DefaultAllocationClaimTTL
	}
	instanceTTL := deps.InstanceTTL
	if instanceTTL <= 0 {
		instanceTTL = DefaultInstanceTTL
	}

	for {
		serverID, err := deps.Index.Lookup(ctx, partyID)
		switch {
		case err == nil:
			info, gerr := deps.Registry.GetServer(ctx, serverID)
			if gerr == nil {
				return mintForServer(info, userID, deps.JoinKeys)
			}
			// The mapping outlived the pod. Drop it and let this caller
			// allocate a fresh instance rather than handing out a dead address.
			if errors.Is(gerr, storage.ErrNotFound) {
				_ = deps.Index.Release(ctx, partyID)
				break
			}
			return AssignResult{}, fmt.Errorf("assign dungeon: resolve instance %s: %w", serverID, gerr)
		case errors.Is(err, storage.ErrNotFound):
			// No instance yet; fall through to the election.
		default:
			return AssignResult{}, fmt.Errorf("assign dungeon: lookup party %s: %w", partyID, err)
		}

		won, err := deps.Index.ClaimAllocation(ctx, partyID, userID, claimTTL)
		if err != nil {
			return AssignResult{}, fmt.Errorf("assign dungeon: claim for party %s: %w", partyID, err)
		}

		if won {
			info, err := deps.Registry.AllocateDungeon(ctx, contentID)
			if err != nil {
				// Release so the next member retries instead of polling for an
				// answer that will never come.
				_ = deps.Index.Release(ctx, partyID)
				return AssignResult{}, fmt.Errorf("assign dungeon: %w", err)
			}
			if err := deps.Index.Publish(ctx, partyID, info.ServerID, instanceTTL); err != nil {
				return AssignResult{}, fmt.Errorf("assign dungeon: publish instance for party %s: %w", partyID, err)
			}
			return mintForServer(info, userID, deps.JoinKeys)
		}

		// Lost the election: wait for the winner's answer, bounded by the
		// caller's context -- which is EnterWorldBudget, so a client gets the
		// retryable "server is starting" rather than a dead connection.
		select {
		case <-ctx.Done():
			return AssignResult{}, fmt.Errorf("assign dungeon: party %s: %w", partyID, ctx.Err())
		case <-time.After(dungeonPollInterval):
		}
	}
}

func mintForServer(info storage.ServerInfo, userID string, joinKeys jwt.Keyring) (AssignResult, error) {
	token, err := GenerateJoinTokenKeyring(userID, info.ServerID, joinKeys)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign dungeon: mint join token: %w", err)
	}
	claims, err := joinKeys.Verify(token)
	if err != nil {
		return AssignResult{}, fmt.Errorf("assign dungeon: read back jti: %w", err)
	}
	return AssignResult{
		ServerID:   info.ServerID,
		ServerAddr: info.Addr,
		JoinToken:  token,
		Transport:  info.Transport,
		JTI:        claims.Jti,
	}, nil
}
