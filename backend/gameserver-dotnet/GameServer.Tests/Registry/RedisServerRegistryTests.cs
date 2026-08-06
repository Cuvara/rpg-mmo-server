using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Registry;
using StackExchange.Redis;

namespace GameServer.Tests.Registry;

/// <summary>
/// Registry behaviour against a real Redis. These assert the exact key layout,
/// field names and TTL semantics the Go gateway reads — see
/// <c>shared/storage/redisstore/registry.go</c>. A drift here means the gateway
/// silently stops finding game servers, so the shape is pinned field by field.
/// </summary>
[Collection(RedisCollection.Name)]
public class RedisServerRegistryTests
{
    private readonly RedisFixture _redis;

    public RedisServerRegistryTests(RedisFixture redis) => _redis = redis;

    private static ServerInfo Info(string serverId, string mapId, int players = 0) =>
        new(serverId, mapId, "10.0.0.5:9200", "tcp", 100, players);

    private async Task<(RedisServerRegistry reg, IConnectionMultiplexer mux)> ConnectAsync(TimeSpan? ttl = null)
    {
        var options = RedisServerRegistry.BuildOptions(_redis.Addr, null);
        var mux = await ConnectionMultiplexer.ConnectAsync(options);
        var reg = new RedisServerRegistry(mux, ttl ?? TimeSpan.FromSeconds(15), NullLogger.Instance);
        return (reg, mux);
    }

    [SkippableFact]
    public async Task Register_WritesTheExactHashShapeTheGatewayReads()
    {
        _redis.SkipUnlessAvailable(nameof(Register_WritesTheExactHashShapeTheGatewayReads));
        var (reg, mux) = await ConnectAsync();
        await using var _ = reg;

        string serverId = $"gs-shape-{Guid.NewGuid():N}"[..16];
        await reg.RegisterAsync(Info(serverId, "map_shape", players: 7), default);

        var db = mux.GetDatabase();
        var hash = (await db.HashGetAllAsync($"servers:id:{serverId}"))
            .ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

        // Field names are a contract with the Go gateway's infoFromFields().
        Assert.Equal(serverId, hash["server_id"]);
        Assert.Equal("map_shape", hash["map_id"]);
        Assert.Equal("10.0.0.5:9200", hash["addr"]);
        Assert.Equal("tcp", hash["transport"]);
        Assert.Equal("100", hash["capacity"]);
        Assert.Equal("7", hash["player_count"]);
        Assert.Equal(6, hash.Count); // no extra fields the gateway would not expect

        // The map index is a plain SET of ids, with no TTL of its own.
        Assert.True(await db.SetContainsAsync("servers:map:map_shape", serverId));
        Assert.Equal(TimeSpan.Zero, await db.KeyTimeToLiveAsync("servers:map:map_shape") ?? TimeSpan.Zero);

        // The hash carries the liveness TTL.
        var ttl = await db.KeyTimeToLiveAsync($"servers:id:{serverId}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalSeconds, 1, 15);
    }

    [SkippableFact]
    public async Task Heartbeat_ReArmsTtl_AndReportsMissingEntry()
    {
        _redis.SkipUnlessAvailable(nameof(Heartbeat_ReArmsTtl_AndReportsMissingEntry));
        var (reg, mux) = await ConnectAsync(TimeSpan.FromSeconds(10));
        await using var _ = reg;
        var db = mux.GetDatabase();

        string serverId = $"gs-hb-{Guid.NewGuid():N}"[..16];
        await reg.RegisterAsync(Info(serverId, "map_hb"), default);

        // Let the TTL visibly decay, then prove the heartbeat pushes it back up.
        await Task.Delay(2500);
        var beforeTtl = await db.KeyTimeToLiveAsync($"servers:id:{serverId}");
        Assert.NotNull(beforeTtl);
        Assert.True(beforeTtl!.Value.TotalSeconds < 9,
            $"expected the TTL to have decayed below 9s, was {beforeTtl.Value.TotalSeconds}s");

        Assert.True(await reg.HeartbeatAsync(serverId, default));

        var afterTtl = await db.KeyTimeToLiveAsync($"servers:id:{serverId}");
        Assert.NotNull(afterTtl);
        Assert.True(afterTtl!.Value > beforeTtl.Value,
            $"heartbeat did not re-arm the TTL ({beforeTtl.Value.TotalSeconds}s -> {afterTtl.Value.TotalSeconds}s)");

        // A heartbeat for an entry that is gone must report false, not throw — that
        // is the signal RegistrationService re-registers on.
        await db.KeyDeleteAsync($"servers:id:{serverId}");
        Assert.False(await reg.HeartbeatAsync(serverId, default));
    }

    [SkippableFact]
    public async Task Entry_ActuallyExpires_WhenNothingHeartbeats()
    {
        _redis.SkipUnlessAvailable(nameof(Entry_ActuallyExpires_WhenNothingHeartbeats));
        // The bug this whole change fixes: the deploy script wrote a 3600s TTL and
        // nothing refreshed it. Prove the TTL is real and short, so a dead server
        // stops attracting joins in seconds rather than an hour.
        var (reg, mux) = await ConnectAsync(TimeSpan.FromSeconds(1));
        await using var _ = reg;
        var db = mux.GetDatabase();

        string serverId = $"gs-exp-{Guid.NewGuid():N}"[..16];
        await reg.RegisterAsync(Info(serverId, "map_exp"), default);
        Assert.True(await db.KeyExistsAsync($"servers:id:{serverId}"));

        await Task.Delay(1800);
        Assert.False(await db.KeyExistsAsync($"servers:id:{serverId}"),
            "the registry entry outlived its TTL — a crashed server would keep black-holing joins");
    }

    [SkippableFact]
    public async Task Deregister_RemovesHashAndMapIndex()
    {
        _redis.SkipUnlessAvailable(nameof(Deregister_RemovesHashAndMapIndex));
        var (reg, mux) = await ConnectAsync();
        await using var _ = reg;
        var db = mux.GetDatabase();

        string serverId = $"gs-dereg-{Guid.NewGuid():N}"[..16];
        await reg.RegisterAsync(Info(serverId, "map_dereg"), default);

        await reg.DeregisterAsync(serverId, "map_dereg", default);

        Assert.False(await db.KeyExistsAsync($"servers:id:{serverId}"));
        Assert.False(await db.SetContainsAsync("servers:map:map_dereg", serverId));
    }

    [SkippableFact]
    public async Task UpdatePlayerCount_WritesCount_ButRefusesToResurrectAnExpiredEntry()
    {
        _redis.SkipUnlessAvailable(nameof(UpdatePlayerCount_WritesCount_ButRefusesToResurrectAnExpiredEntry));
        var (reg, mux) = await ConnectAsync();
        await using var _ = reg;
        var db = mux.GetDatabase();

        string serverId = $"gs-count-{Guid.NewGuid():N}"[..16];
        await reg.RegisterAsync(Info(serverId, "map_count"), default);

        Assert.True(await reg.UpdatePlayerCountAsync(serverId, 42, default));
        Assert.Equal("42", (await db.HashGetAsync($"servers:id:{serverId}", "player_count")).ToString());

        // Once the entry is gone, a late writer must not recreate it: an HSET on a
        // missing key would produce a hash with NO TTL, i.e. an immortal ghost
        // server the gateway would hand clients to forever.
        await db.KeyDeleteAsync($"servers:id:{serverId}");
        Assert.False(await reg.UpdatePlayerCountAsync(serverId, 9, default));
        Assert.False(await db.KeyExistsAsync($"servers:id:{serverId}"));
    }
}
