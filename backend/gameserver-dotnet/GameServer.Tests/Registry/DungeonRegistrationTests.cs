using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Registry;
using StackExchange.Redis;

namespace GameServer.Tests.Registry;

/// <summary>
/// A dungeon instance must be reachable and invisible at the same time (ADR-26
/// decision 8).
///
/// <para><b>Reachable</b>: the gateway allocates the pod, learns its name and then waits
/// for <c>servers:id:{server_id}</c> to appear so it can read the dialable address out of
/// it. Skipping that hash would make a dungeon unallocatable, not merely unindexed.</para>
///
/// <para><b>Invisible</b>: it must not join <c>servers:map:{map_id}</c>, the set
/// <c>FindServer</c> searches. An indexed instance would be handed to an unrelated player
/// as if it were a map, and on a dungeon fleet — which pins no <c>GAMESERVER_MAP_ID</c> —
/// the index key would be the empty one.</para>
/// </summary>
[Collection(RedisCollection.Name)]
public class DungeonRegistrationTests
{
    private readonly RedisFixture _redis;

    public DungeonRegistrationTests(RedisFixture redis) => _redis = redis;

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private static ServerInfo Info(string serverId, string mapId) =>
        new(serverId, mapId, "10.0.0.9:9200", "tcp", 8, 0);

    private async Task<(RedisServerRegistry reg, IConnectionMultiplexer mux)> ConnectAsync()
    {
        var options = RedisServerRegistry.BuildOptions(_redis.Addr, null);
        var mux = await ConnectionMultiplexer.ConnectAsync(options);
        return (new RedisServerRegistry(mux, Ttl, NullLogger.Instance), mux);
    }

    [SkippableFact]
    public async Task HashOnly_WritesTheHashAndItsTtlButNotTheMapIndex()
    {
        _redis.SkipUnlessAvailable(nameof(HashOnly_WritesTheHashAndItsTtlButNotTheMapIndex));
        var (reg, mux) = await ConnectAsync();
        await using var _ = reg;

        string serverId = $"gs-dun-{Guid.NewGuid():N}"[..16];
        string mapId = $"dungeon_{Guid.NewGuid():N}"[..20];

        await reg.RegisterAsync(Info(serverId, mapId), RegistrationScope.HashOnly, default);

        var db = mux.GetDatabase();

        // The hash the gateway reads the address from is there, in full.
        var hash = (await db.HashGetAllAsync($"servers:id:{serverId}"))
            .ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        Assert.Equal(serverId, hash["server_id"]);
        Assert.Equal("10.0.0.9:9200", hash["addr"]);
        Assert.Equal(mapId, hash["map_id"]);
        Assert.Equal(6, hash.Count);

        // ...with its heartbeat TTL armed, so a dead instance still disappears by itself.
        var ttl = await db.KeyTimeToLiveAsync($"servers:id:{serverId}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(1), Ttl);

        // And the index FindServer searches was never touched.
        Assert.Equal(0, await db.SetLengthAsync($"servers:map:{mapId}"));
        Assert.False(await db.SetContainsAsync($"servers:map:{mapId}", serverId));
    }

    /// <summary>
    /// The control. The same call under <see cref="RegistrationScope.MapIndexed"/> DOES
    /// index — otherwise the test above would pass against a registry that had simply
    /// stopped indexing anything.
    /// </summary>
    [SkippableFact]
    public async Task MapIndexed_StillJoinsTheMapIndex()
    {
        _redis.SkipUnlessAvailable(nameof(MapIndexed_StillJoinsTheMapIndex));
        var (reg, mux) = await ConnectAsync();
        await using var _ = reg;

        string serverId = $"gs-map-{Guid.NewGuid():N}"[..16];
        string mapId = $"map_{Guid.NewGuid():N}"[..16];

        await reg.RegisterAsync(Info(serverId, mapId), RegistrationScope.MapIndexed, default);

        var db = mux.GetDatabase();
        Assert.True(await db.SetContainsAsync($"servers:map:{mapId}", serverId));
    }

    /// <summary>
    /// Heartbeat behaviour is scope-independent: the TTL lives on the hash, which both
    /// scopes write, so a dungeon pod self-heals after a Redis wipe exactly like a map.
    /// </summary>
    [SkippableFact]
    public async Task HashOnly_HeartbeatStillRearmsAndStillReportsAWipe()
    {
        _redis.SkipUnlessAvailable(nameof(HashOnly_HeartbeatStillRearmsAndStillReportsAWipe));
        var (reg, mux) = await ConnectAsync();
        await using var _ = reg;

        string serverId = $"gs-hb-{Guid.NewGuid():N}"[..16];
        string mapId = $"dungeon_{Guid.NewGuid():N}"[..20];

        await reg.RegisterAsync(Info(serverId, mapId), RegistrationScope.HashOnly, default);
        Assert.True(await reg.HeartbeatAsync(serverId, default));

        await mux.GetDatabase().KeyDeleteAsync($"servers:id:{serverId}");
        Assert.False(await reg.HeartbeatAsync(serverId, default));
    }
}
