package redisstore

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/redis/go-redis/v9"
)

// Redis layout for the dungeon index (ADR-26 decision 2):
//
//	dungeon:alloc:{party_id}   STRING  holder id, short TTL   (allocation election)
//	dungeon:party:{party_id}   STRING  server id, longer TTL  (the answer)
//
// Both expire. A party that abandons its entry halfway must not pin a stale
// server id forever, and a dungeon pod that dies takes its own servers:id:
// hash with it -- so an index entry that outlived the pod would resolve to
// nothing and hand a client an address that is not answering.
const (
	dungeonAllocPrefix = "dungeon:alloc:"
	dungeonPartyPrefix = "dungeon:party:"
)

// DungeonIndexStore is the Redis implementation of storage.DungeonIndex.
type DungeonIndexStore struct {
	client redis.UniversalClient
}

// NewDungeonIndexWithClient builds the index on an existing client, so it
// shares the one pool the gateway already opens for sessions and the registry.
func NewDungeonIndexWithClient(client redis.UniversalClient) *DungeonIndexStore {
	return &DungeonIndexStore{client: client}
}

// ClaimAllocation is SET NX: exactly one caller wins per party until the claim
// expires. The TTL is the safety net -- a gateway that dies mid-allocation
// would otherwise block that party's entry until a human intervened.
func (s *DungeonIndexStore) ClaimAllocation(ctx context.Context, partyID, holder string, ttl time.Duration) (bool, error) {
	ok, err := s.client.SetNX(ctx, dungeonAllocPrefix+partyID, holder, ttl).Result()
	if err != nil {
		return false, fmt.Errorf("claim dungeon allocation for party %s: %w", partyID, err)
	}
	return ok, nil
}

// Publish records the allocated instance for the party.
func (s *DungeonIndexStore) Publish(ctx context.Context, partyID, serverID string, ttl time.Duration) error {
	if err := s.client.Set(ctx, dungeonPartyPrefix+partyID, serverID, ttl).Err(); err != nil {
		return fmt.Errorf("publish dungeon instance for party %s: %w", partyID, err)
	}
	return nil
}

// Lookup returns the instance recorded for the party, or storage.ErrNotFound.
func (s *DungeonIndexStore) Lookup(ctx context.Context, partyID string) (string, error) {
	v, err := s.client.Get(ctx, dungeonPartyPrefix+partyID).Result()
	if errors.Is(err, redis.Nil) {
		return "", storage.ErrNotFound
	}
	if err != nil {
		return "", fmt.Errorf("lookup dungeon instance for party %s: %w", partyID, err)
	}
	return v, nil
}

// Release drops both keys, so the next member to arrive retries the allocation
// instead of polling for an entry that will never be published.
func (s *DungeonIndexStore) Release(ctx context.Context, partyID string) error {
	if err := s.client.Del(ctx, dungeonAllocPrefix+partyID, dungeonPartyPrefix+partyID).Err(); err != nil {
		return fmt.Errorf("release dungeon index for party %s: %w", partyID, err)
	}
	return nil
}
