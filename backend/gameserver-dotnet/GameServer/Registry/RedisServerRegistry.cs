using System.Globalization;
using GameServer.Games;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace GameServer.Registry;

/// <summary>
/// Redis-backed server registry, wire-compatible with the Go gateway.
///
/// Layout (authoritative source: <c>shared/storage/redisstore/registry.go</c>):
/// <code>
///   servers:id:{server_id}   HASH  server_id, map_id, addr, transport,
///                                  capacity, player_count, identity_key, game_id (+ TTL)
///   servers:map:{map_id}     SET   default-game server ids on that map (no TTL)
///   servers:map:{game}:{map_id} SET server ids for another game (no TTL)
///
/// Hash fields: server_id, map_id, addr, transport, capacity, player_count,
/// identity_key, game_id. Mirrors shared/storage/redisstore/registry.go byte for byte.
/// </code>
/// The hash is the source of truth: a server disappears on its own once it stops
/// heartbeating, and the gateway prunes the map index lazily on lookup. Key
/// names, field names and value formats must stay byte-identical to the Go side
/// — the gateway reads these hashes directly, with no translation layer.
/// Missing or empty game_id means the default game. Registration atomically refuses
/// to overwrite a live server id belonging to another game before changing any keys.
/// </summary>
public sealed class RedisServerRegistry : IServerRegistry
{
    // Mirrors constants.ServerRegistryKey ("servers:") plus the Go prefixes.
    private const string IdPrefix = "servers:id:";
    private const string MapPrefix = "servers:map:";

    // Field names as the gateway's infoFromFields() reads them.
    private const string FieldServerId = "server_id";
    private const string FieldMapId = "map_id";
    private const string FieldAddr = "addr";
    private const string FieldTransport = "transport";
    private const string FieldCapacity = "capacity";
    private const string FieldPlayerCount = "player_count";
    // ADR-25. Written by this process, read by the Go gateway's infoFromFields()
    // with no translation layer, so the name and the base64 encoding of its value
    // must change on both sides in one commit or not at all.
    private const string FieldIdentityKey = "identity_key";
    private const string FieldGameId = "game_id";

    internal static readonly string[] HashFields =
    [
        FieldServerId, FieldMapId, FieldAddr, FieldTransport,
        FieldCapacity, FieldPlayerCount, FieldIdentityKey, FieldGameId,
    ];

    // Shared verbatim with shared/testdata/multigame/register.lua and the Go registry.
    // A single key writes only the hash; the optional second key adds map membership.
    internal const string RegisterLua = @"-- ADR-33 S1: register a server only if servers:id:{id} is absent or names the same game.
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
";

    /// <summary>
    /// Sets player_count only if the hash still exists, so a stale writer cannot
    /// resurrect an expired entry as a TTL-less (immortal) one. Byte-for-byte the
    /// Go updatePlayerCountScript.
    /// </summary>
    private const string UpdatePlayerCountLua = @"
if redis.call('EXISTS', KEYS[1]) == 0 then
	return 0
end
redis.call('HSET', KEYS[1], 'player_count', ARGV[1])
return 1
";

    private readonly IConnectionMultiplexer _mux;
    private readonly bool _ownsMux;
    private readonly TimeSpan _ttl;
    private readonly ILogger _logger;

    /// <summary>TTL armed on every register/heartbeat.</summary>
    public TimeSpan Ttl => _ttl;

    public RedisServerRegistry(
        IConnectionMultiplexer mux,
        TimeSpan ttl,
        ILogger logger,
        bool ownsMux = false)
    {
        _mux = mux;
        _ttl = ttl > TimeSpan.Zero ? ttl : RegistryDefaults.HeartbeatTtl;
        _logger = logger;
        _ownsMux = ownsMux;
    }

    /// <summary>
    /// Connect to Redis and return a registry over that connection.
    /// </summary>
    /// <remarks>
    /// <c>AbortOnConnectFail = false</c> is deliberate and load-bearing: it lets the
    /// server boot while Redis is down and lets the multiplexer reconnect by itself
    /// afterwards, instead of throwing at startup and leaving the map permanently
    /// unregistered. Combined with the re-register-on-missing rule in
    /// <see cref="RegistrationService"/>, that is what makes a Redis wipe self-heal.
    /// </remarks>
    public static async Task<RedisServerRegistry> ConnectAsync(
        string addr, string? password, TimeSpan ttl, ILogger logger)
    {
        var options = BuildOptions(addr, password);
        var mux = await ConnectionMultiplexer.ConnectAsync(options);
        return new RedisServerRegistry(mux, ttl, logger, ownsMux: true);
    }

    /// <summary>
    /// Build the connection options from a Go-style <c>host:port</c> address.
    /// </summary>
    internal static ConfigurationOptions BuildOptions(string addr, string? password)
    {
        var options = ConfigurationOptions.Parse(string.IsNullOrWhiteSpace(addr) ? "localhost:6379" : addr);
        if (!string.IsNullOrEmpty(password))
        {
            options.Password = password;
        }
        // Never abort on a failed initial connect: the game server must come up and
        // keep serving even if Redis is unreachable, then register once it returns.
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 3;
        options.ClientName = "gameserver";
        return options;
    }

    internal static string ServerKey(string serverId) => IdPrefix + serverId;
    internal static string MapKey(string mapId) => MapIndexKey(null, mapId);

    internal static string MapIndexKey(string? gameId, string mapId) =>
        GameId.IsDefaultGame(gameId) ? MapPrefix + mapId : MapPrefix + gameId + ":" + mapId;

    internal static RedisValue[] BuildRegisterArgs(ServerInfo info, TimeSpan ttl) =>
    [
        GameId.DefaultGameId, (long)ttl.TotalMilliseconds,
        info.ServerId, info.MapId, info.Addr, info.Transport,
        info.Capacity, info.PlayerCount, info.IdentityKey, GameId.GameOrDefault(info.GameId),
    ];

    private IDatabase Db => _mux.GetDatabase();

    /// <inheritdoc />
    public async Task RegisterAsync(ServerInfo info, RegistrationScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string gameId = GameId.GameOrDefault(info.GameId);
        RedisKey[] keys = scope == RegistrationScope.MapIndexed
            ? [ServerKey(info.ServerId), MapIndexKey(gameId, info.MapId)]
            : [ServerKey(info.ServerId)];
        var result = await Db.ScriptEvaluateAsync(RegisterLua, keys, BuildRegisterArgs(info, _ttl));
        if ((long)result == 0)
            throw new RegistryGameMismatchException(info.ServerId, gameId);

        _logger.LogInformation(
            "Registered {ServerId} in Redis: map={MapId} addr={Addr} transport={Transport} capacity={Capacity} ttl={Ttl}s scope={Scope} identity={IdentityKey}",
            info.ServerId, info.MapId, info.Addr, info.Transport, info.Capacity, (int)_ttl.TotalSeconds, scope,
            string.IsNullOrEmpty(info.IdentityKey) ? "(none)" : info.IdentityKey);
    }

    /// <inheritdoc />
    public async Task<bool> HeartbeatAsync(string serverId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // KeyExpire returns false when the key does not exist — which is exactly the
        // "Redis was wiped or we expired" signal the caller re-registers on.
        return await Db.KeyExpireAsync(ServerKey(serverId), _ttl);
    }

    /// <inheritdoc />
    public async Task DeregisterAsync(string serverId, string mapId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = (RedisKey)ServerKey(serverId);
        var db = Db;

        // Prefer the map id recorded in Redis: if this process was started with one
        // map id and the entry says another, the index membership to remove is the
        // one Redis actually holds.
        var stored = await db.HashGetAsync(key, [(RedisValue)FieldMapId, FieldGameId]);
        string effectiveMap = stored[0].HasValue ? stored[0].ToString() : mapId;
        string gameId = GameId.GameOrDefault(stored[1].ToString());

        var batch = db.CreateBatch();
        var del = batch.KeyDeleteAsync(key);
        Task<bool>? srem = string.IsNullOrEmpty(effectiveMap)
            ? null
            : batch.SetRemoveAsync(MapIndexKey(gameId, effectiveMap), serverId);
        batch.Execute();

        await del;
        if (srem != null) await srem;

        _logger.LogInformation("Deregistered {ServerId} from Redis (map={MapId})", serverId, effectiveMap);
    }

    /// <summary>
    /// Return live servers on a game's map, ignoring expired index members and entries
    /// owned by another game. An omitted or empty game id selects the default game.
    /// </summary>
    public async Task<IReadOnlyList<ServerInfo>> FindByGameAndMapAsync(
        string? gameId, string mapId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string effectiveGame = GameId.GameOrDefault(gameId);
        var db = Db;
        var ids = await db.SetMembersAsync(MapIndexKey(effectiveGame, mapId));
        var result = new List<ServerInfo>(ids.Length);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var entries = await db.HashGetAllAsync(ServerKey(id.ToString()));
            if (entries.Length == 0) continue;
            var info = FromHashEntries(entries);
            if (string.Equals(info.GameId, effectiveGame, StringComparison.Ordinal))
                result.Add(info);
        }
        return result;
    }

    internal static ServerInfo FromHashEntries(HashEntry[] entries)
    {
        var fields = new Dictionary<string, string>(entries.Length, StringComparer.Ordinal);
        foreach (var entry in entries) fields[entry.Name.ToString()] = entry.Value.ToString();
        string Field(string name) => fields.GetValueOrDefault(name, "");
        int.TryParse(Field(FieldCapacity), NumberStyles.Integer, CultureInfo.InvariantCulture, out int capacity);
        int.TryParse(Field(FieldPlayerCount), NumberStyles.Integer, CultureInfo.InvariantCulture, out int playerCount);
        return new ServerInfo(
            Field(FieldServerId), Field(FieldMapId), Field(FieldAddr), Field(FieldTransport),
            capacity, playerCount, Field(FieldIdentityKey), GameId.GameOrDefault(Field(FieldGameId)));
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePlayerCountAsync(string serverId, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await Db.ScriptEvaluateAsync(
            UpdatePlayerCountLua,
            [ServerKey(serverId)],
            [count]);
        return (long)result == 1;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsMux)
        {
            await _mux.CloseAsync();
            _mux.Dispose();
        }
    }
}
