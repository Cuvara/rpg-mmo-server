package redisstore

import (
	"context"
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
//	servers:map:{game}:{map_id} SET server ids for a non-default game (no TTL)
//
// Hash fields: server_id, map_id, addr, transport, capacity, player_count,
// identity_key, game_id (eight fields). Empty/absent game_id means the default
// game, whose map index keeps the original servers:map:{map_id} key.
// The names and their value encodings are a CROSS-LANGUAGE
// CONTRACT — the C# game server writes these entries and this Go code reads
// them with no translation layer — so a change here is a change in
// GameServer/Registry/RedisServerRegistry.cs in the same commit.
//
// The hash is the source of truth. A server disappears automatically when it
// stops heartbeating; the map index is pruned lazily on lookup.
// Registration atomically checks ownership before updating the hash, TTL and
// index; a live id owned by another game is refused without changing Redis.
const (
	registryIDPrefix  = constants.ServerRegistryKey + "id:"
	registryMapPrefix = constants.ServerRegistryKey + "map:"

	fieldServerID    = "server_id"
	fieldMapID       = "map_id"
	fieldAddr        = "addr"
	fieldTransport   = "transport"
	fieldCapacity    = "capacity"
	fieldPlayerCount = "player_count"
	fieldIdentityKey = "identity_key"
	fieldGameID      = "game_id"
)

// registryHashFields is the ordered cross-language registry hash contract.
var registryHashFields = []string{
	fieldServerID, fieldMapID, fieldAddr, fieldTransport,
	fieldCapacity, fieldPlayerCount, fieldIdentityKey, fieldGameID,
}

const registerScriptSource = `-- ADR-33 S1: register a server only if servers:id:{id} is absent or names the same game.
-- KEYS[1] servers:id:{server_id}; KEYS[2] (optional) the map index key.
-- ARGV[1] default game id; ARGV[2] ttl in ms; ARGV[3..10] server_id, map_id, addr,
-- transport, capacity, player_count, identity_key, game_id.
if redis.call('EXISTS', KEYS[1]) == 1 then
	local current = redis.call('HGET', KEYS[1], 'game_id')
	if not current or current == '' then
		current = ARGV[1]
	end
	if current ~= ARGV[10] then
		return 0
	end
end
redis.call('HSET', KEYS[1], 'server_id', ARGV[3], 'map_id', ARGV[4], 'addr', ARGV[5], 'transport', ARGV[6], 'capacity', ARGV[7], 'player_count', ARGV[8], 'identity_key', ARGV[9], 'game_id', ARGV[10])
redis.call('PEXPIRE', KEYS[1], ARGV[2])
if #KEYS > 1 then
	redis.call('SADD', KEYS[2], ARGV[3])
end
return 1
`

var registerScript = redis.NewScript(registerScriptSource)

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
func mapKey(mapID string) string       { return MapIndexKey("", mapID) }

// MapIndexKey returns the registry map index key for a game and map.
// Empty or default game ids preserve the original default-game key.
func MapIndexKey(gameID, mapID string) string {
	if constants.GameOrDefault(gameID) == constants.DefaultGameID {
		return registryMapPrefix + mapID
	}
	return registryMapPrefix + gameID + ":" + mapID
}

// Register writes the server hash, (re)arms its heartbeat TTL and indexes it
// under its game's map. Returns ErrGameMismatch if a live id belongs to another game.
func (r *ServerRegistry) Register(ctx context.Context, info storage.ServerInfo) error {
	gameID := constants.GameOrDefault(info.GameID)
	result, err := registerScript.Run(ctx, r.client,
		[]string{serverKey(info.ServerID), MapIndexKey(gameID, info.MapID)},
		constants.DefaultGameID, r.ttl.Milliseconds(), info.ServerID, info.MapID,
		info.Addr, info.Transport, info.Capacity, info.PlayerCount, info.IdentityKey, gameID,
	).Int64()
	if err != nil {
		return fmt.Errorf("redis register server %s for game %s: %w", info.ServerID, gameID, err)
	}
	if result == 0 {
		return fmt.Errorf("redis register server %s for game %s: %w", info.ServerID, gameID, storage.ErrGameMismatch)
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
	fields, err := r.client.HGetAll(ctx, key).Result()
	if err != nil {
		return fmt.Errorf("redis lookup server %s: %w", serverID, err)
	}

	pipe := r.client.TxPipeline()
	pipe.Del(ctx, key)
	if mapID := fields[fieldMapID]; mapID != "" {
		pipe.SRem(ctx, MapIndexKey(fields[fieldGameID], mapID), serverID)
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
	return r.findByMapIndex(ctx, mapKey(mapID), mapID, "")
}

// FindByGameAndMapID returns live servers owned by gameID on mapID, pruning
// expired index entries. Empty gameID means constants.DefaultGameID.
func (r *ServerRegistry) FindByGameAndMapID(ctx context.Context, gameID, mapID string) ([]storage.ServerInfo, error) {
	return r.findByMapIndex(ctx, MapIndexKey(gameID, mapID), mapID, constants.GameOrDefault(gameID))
}

func (r *ServerRegistry) findByMapIndex(ctx context.Context, idxKey, mapID, gameFilter string) ([]storage.ServerInfo, error) {
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
		info := infoFromFields(fields)
		if gameFilter == "" || info.GameID == gameFilter {
			result = append(result, info)
		}
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
	capacity, _ := strconv.Atoi(f[fieldCapacity])
	count, _ := strconv.Atoi(f[fieldPlayerCount])
	return storage.ServerInfo{
		ServerID:    f[fieldServerID],
		MapID:       f[fieldMapID],
		Addr:        f[fieldAddr],
		Transport:   f[fieldTransport],
		Capacity:    capacity,
		PlayerCount: count,
		// Absent for a pre-ADR-25 server, and absent is not an error: a missing
		// key means a client cannot require identity against that server, which
		// the client decides, not the registry reader.
		IdentityKey: f[fieldIdentityKey],
		GameID:      constants.GameOrDefault(f[fieldGameID]),
	}
}

func toAny(s []string) []any {
	out := make([]any, len(s))
	for i, v := range s {
		out[i] = v
	}
	return out
}
