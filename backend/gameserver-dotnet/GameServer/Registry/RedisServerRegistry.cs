using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace GameServer.Registry;

/// <summary>
/// Redis-backed server registry, wire-compatible with the Go gateway.
///
/// Layout (authoritative source: <c>shared/storage/redisstore/registry.go</c>):
/// <code>
///   servers:id:{server_id}   HASH  server_id, map_id, addr, transport,
///                                  capacity, player_count   (+ TTL)
///   servers:map:{map_id}     SET   server ids on that map   (index, no TTL)
/// </code>
/// The hash is the source of truth: a server disappears on its own once it stops
/// heartbeating, and the gateway prunes the map index lazily on lookup. Key
/// names, field names and value formats must stay byte-identical to the Go side
/// — the gateway reads these hashes directly, with no translation layer.
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
    internal static string MapKey(string mapId) => MapPrefix + mapId;

    private IDatabase Db => _mux.GetDatabase();

    /// <inheritdoc />
    public async Task RegisterAsync(ServerInfo info, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = (RedisKey)ServerKey(info.ServerId);
        var db = Db;

        // Same three operations, same order, as the Go TxPipeline: HSET, EXPIRE,
        // SADD. Batched so they travel as one round trip.
        var batch = db.CreateBatch();
        var hset = batch.HashSetAsync(key,
        [
            new HashEntry(FieldServerId, info.ServerId),
            new HashEntry(FieldMapId, info.MapId),
            new HashEntry(FieldAddr, info.Addr),
            new HashEntry(FieldTransport, info.Transport),
            new HashEntry(FieldCapacity, info.Capacity),
            new HashEntry(FieldPlayerCount, info.PlayerCount),
        ]);
        var expire = batch.KeyExpireAsync(key, _ttl);
        var sadd = batch.SetAddAsync(MapKey(info.MapId), info.ServerId);
        batch.Execute();

        await Task.WhenAll(hset, expire, sadd);

        _logger.LogInformation(
            "Registered {ServerId} in Redis: map={MapId} addr={Addr} transport={Transport} capacity={Capacity} ttl={Ttl}s",
            info.ServerId, info.MapId, info.Addr, info.Transport, info.Capacity, (int)_ttl.TotalSeconds);
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
        var key = (RedisKey)ServerKey(serverId);
        var db = Db;

        // Prefer the map id recorded in Redis: if this process was started with one
        // map id and the entry says another, the index membership to remove is the
        // one Redis actually holds.
        var storedMap = await db.HashGetAsync(key, FieldMapId);
        string effectiveMap = storedMap.HasValue ? storedMap.ToString() : mapId;

        var batch = db.CreateBatch();
        var del = batch.KeyDeleteAsync(key);
        Task<bool>? srem = string.IsNullOrEmpty(effectiveMap)
            ? null
            : batch.SetRemoveAsync(MapKey(effectiveMap), serverId);
        batch.Execute();

        await del;
        if (srem != null) await srem;

        _logger.LogInformation("Deregistered {ServerId} from Redis (map={MapId})", serverId, effectiveMap);
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
