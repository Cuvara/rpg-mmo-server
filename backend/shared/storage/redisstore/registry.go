package redisstore

import (
	"context"
	"errors"
	"fmt"
	"strconv"
	"time"

	"github.com/duycuong/rpg-mmo/shared/constants"
	"github.com/duycuong/rpg-mmo/shared/storage"
	"github.com/redis/go-redis/v9"
)

// Redis layout for the server registry:
//
//	servers:id:{server_id}   HASH  server fields, TTL = ServerHeartbeatTTL
//	servers:map:{map_id}     SET   server ids on that map (index, no TTL)
//
// The hash is the source of truth. A server disappears automatically when it
// stops heartbeating; the map index is pruned lazily on lookup.
const (
	registryIDPrefix  = constants.ServerRegistryKey + "id:"
	registryMapPrefix = constants.ServerRegistryKey + "map:"
)

// updatePlayerCountScript sets player_count only if the server hash still
// exists, so a stale writer cannot resurrect an expired (TTL-less) entry.
var updatePlayerCountScript = redis.NewScript(`
if redis.call('EXISTS', KEYS[1]) == 0 then
	return 0
end
redis.call('HSET', KEYS[1], 'player_count', ARGV[1])
return 1
`)

// RedisServerRegistry is a Redis-backed ServerRegistry with heartbeat-based
// liveness: entries expire after TTL unless refreshed via Heartbeat.
type ServerRegistry struct {
	client redis.UniversalClient
	ttl    time.Duration
	owned  bool
}

// NewRedisServerRegistry connects to Redis at addr and returns a registry using
// constants.ServerHeartbeatTTL as the liveness window.
func NewServerRegistry(addr, password string) *ServerRegistry {
	return &ServerRegistry{
		client: NewRedisClient(addr, password),
		ttl:    constants.ServerHeartbeatTTL,
		owned:  true,
	}
}

// NewRedisServerRegistryWithClient wraps an existing client. A ttl <= 0 falls
// back to constants.ServerHeartbeatTTL.
func NewServerRegistryWithClient(client redis.UniversalClient, ttl time.Duration) *ServerRegistry {
	if ttl <= 0 {
		ttl = constants.ServerHeartbeatTTL
	}
	return &ServerRegistry{client: client, ttl: ttl}
}

func serverKey(serverID string) string { return registryIDPrefix + serverID }
func mapKey(mapID string) string       { return registryMapPrefix + mapID }

// Register writes the server hash, (re)arms its heartbeat TTL and indexes it
// under its map.
func (r *ServerRegistry) Register(ctx context.Context, info storage.ServerInfo) error {
	key := serverKey(info.ServerID)
	pipe := r.client.TxPipeline()
	pipe.HSet(ctx, key,
		"server_id", info.ServerID,
		"map_id", info.MapID,
		"addr", info.Addr,
		"transport", info.Transport,
		"capacity", info.Capacity,
		"player_count", info.PlayerCount,
	)
	pipe.Expire(ctx, key, r.ttl)
	pipe.SAdd(ctx, mapKey(info.MapID), info.ServerID)
	if _, err := pipe.Exec(ctx); err != nil {
		return fmt.Errorf("redis register server %s: %w", info.ServerID, err)
	}
	return nil
}

// Heartbeat re-arms the TTL of a live server. Returns ErrNotFound if the entry
// already expired or was never registered.
func (r *ServerRegistry) Heartbeat(ctx context.Context, serverID string) error {
	ok, err := r.client.Expire(ctx, serverKey(serverID), r.ttl).Result()
	if err != nil {
		return fmt.Errorf("redis heartbeat server %s: %w", serverID, err)
	}
	if !ok {
		return fmt.Errorf("server %s: %w", serverID, storage.ErrNotFound)
	}
	return nil
}

// Deregister removes the server hash and its map index entry.
func (r *ServerRegistry) Deregister(ctx context.Context, serverID string) error {
	key := serverKey(serverID)
	mapID, err := r.client.HGet(ctx, key, "map_id").Result()
	if err != nil && !errors.Is(err, redis.Nil) {
		return fmt.Errorf("redis lookup server %s: %w", serverID, err)
	}

	pipe := r.client.TxPipeline()
	pipe.Del(ctx, key)
	if mapID != "" {
		pipe.SRem(ctx, mapKey(mapID), serverID)
	}
	if _, err := pipe.Exec(ctx); err != nil {
		return fmt.Errorf("redis deregister server %s: %w", serverID, err)
	}
	return nil
}

// GetServer returns a single live server. Returns ErrNotFound when the entry is
// missing or has expired.
func (r *ServerRegistry) GetServer(ctx context.Context, serverID string) (storage.ServerInfo, error) {
	fields, err := r.client.HGetAll(ctx, serverKey(serverID)).Result()
	if err != nil {
		return storage.ServerInfo{}, fmt.Errorf("redis get server %s: %w", serverID, err)
	}
	if len(fields) == 0 {
		return storage.ServerInfo{}, fmt.Errorf("server %s: %w", serverID, storage.ErrNotFound)
	}
	return infoFromFields(fields), nil
}

// FindByMapID returns all live servers on the map, pruning index entries whose
// hash has expired.
func (r *ServerRegistry) FindByMapID(ctx context.Context, mapID string) ([]storage.ServerInfo, error) {
	idxKey := mapKey(mapID)
	ids, err := r.client.SMembers(ctx, idxKey).Result()
	if err != nil {
		return nil, fmt.Errorf("redis list servers for map %s: %w", mapID, err)
	}

	var (
		result []storage.ServerInfo
		dead   []string
	)
	for _, id := range ids {
		fields, err := r.client.HGetAll(ctx, serverKey(id)).Result()
		if err != nil {
			return nil, fmt.Errorf("redis get server %s: %w", id, err)
		}
		if len(fields) == 0 {
			dead = append(dead, id)
			continue
		}
		result = append(result, infoFromFields(fields))
	}

	if len(dead) > 0 {
		// Best effort: a failed prune only leaves stale index members behind.
		_ = r.client.SRem(ctx, idxKey, toAny(dead)...).Err()
	}
	return result, nil
}

// UpdatePlayerCount sets the current player count of a live server.
func (r *ServerRegistry) UpdatePlayerCount(ctx context.Context, serverID string, count int) error {
	res, err := updatePlayerCountScript.Run(ctx, r.client, []string{serverKey(serverID)}, count).Int64()
	if err != nil {
		return fmt.Errorf("redis update player count for %s: %w", serverID, err)
	}
	if res == 0 {
		return fmt.Errorf("server %s: %w", serverID, storage.ErrNotFound)
	}
	return nil
}

// Close releases the underlying client if this registry created it.
func (r *ServerRegistry) Close() error {
	if !r.owned {
		return nil
	}
	if err := r.client.Close(); err != nil {
		return fmt.Errorf("close redis client: %w", err)
	}
	return nil
}

func infoFromFields(f map[string]string) storage.ServerInfo {
	capacity, _ := strconv.Atoi(f["capacity"])
	count, _ := strconv.Atoi(f["player_count"])
	return storage.ServerInfo{
		ServerID:    f["server_id"],
		MapID:       f["map_id"],
		Addr:        f["addr"],
		Transport:   f["transport"],
		Capacity:    capacity,
		PlayerCount: count,
	}
}

func toAny(s []string) []any {
	out := make([]any, len(s))
	for i, v := range s {
		out[i] = v
	}
	return out
}
