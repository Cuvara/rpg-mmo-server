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

    /// <summary>
    /// The TTL <see cref="ConnectAsync"/> uses unless a test asks for another. Named rather
    /// than inlined because the TTL assertions below assert on this exact value: a test that
    /// pins the level Redis must report has to be able to say what that level is.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private async Task<(RedisServerRegistry reg, IConnectionMultiplexer mux)> ConnectAsync(TimeSpan? ttl = null)
    {
        var options = RedisServerRegistry.BuildOptions(_redis.Addr, null);
        var mux = await ConnectionMultiplexer.ConnectAsync(options);
        var reg = new RedisServerRegistry(mux, ttl ?? Ttl, NullLogger.Instance);
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

        // The hash carries the liveness TTL, and this asserts its LEVEL from a single
        // read — not decay between two reads.
        //
        // This used to compare two reads, on the stated grounds that decay is "immune to
        // clock steps (see #161)". That is false, and #175 has the counter-example: a run
        // wrote a 15s TTL and Redis reported 17.23s remaining. Redis computes PTTL as
        // `expire_at_ms - now_ms` from its OWN wall clock, so a remaining TTL above the
        // configured maximum is arithmetically impossible unless CLOCK_REALTIME stepped
        // backwards between the two reads — which is #153, observed here twice. A
        // Stopwatch cannot rescue the old form either: the quantity asserted on is computed
        // inside Redis, where this process's monotonic clock does not reach.
        //
        // A single read is self-consistent no matter what the clock does afterwards, and it
        // pins the thing the test actually names: the deploy script's 3600s TTL. It is the
        // stronger assertion — decay only proved the number moved down, this proves the
        // number is inside the window the gateway's liveness contract requires.
        var ttl1 = await db.KeyTimeToLiveAsync($"servers:id:{serverId}");
        Assert.NotNull(ttl1);
        Assert.InRange(ttl1!.Value, TimeSpan.Zero, Ttl);
    }

    [SkippableFact]
    public async Task Heartbeat_ReArmsTtl_AndReportsMissingEntry()
    {
        _redis.SkipUnlessAvailable(nameof(Heartbeat_ReArmsTtl_AndReportsMissingEntry));
        var hbTtl = TimeSpan.FromSeconds(10);
        var (reg, mux) = await ConnectAsync(hbTtl);
        await using var _ = reg;
        var db = mux.GetDatabase();

        string serverId = $"gs-hb-{Guid.NewGuid():N}"[..16];
        await reg.RegisterAsync(Info(serverId, "map_hb"), default);

        // Let some of the TTL burn off, then prove the heartbeat re-arms it.
        //
        // There is deliberately no "the TTL decayed below 9s" precondition any more. That
        // read asserted on a quantity Redis derives from its own wall clock, and on this
        // host that clock steps (#153): a run measured 10.254s remaining on a 10s TTL after
        // a 2.5s wait, which no monotonic clock can produce. The precondition was measuring
        // the box, not the product.
        //
        // What replaces it is stronger, not weaker. `after > before` only proved the
        // direction; asserting the LEVEL pins the value the product is required to write —
        // a heartbeat must reset the key to the full configured TTL, so anything below
        // ttl-1s is a real defect that the old comparison would have passed happily.
        await Task.Delay(2500);

        Assert.True(await reg.HeartbeatAsync(serverId, default));

        var afterTtl = await db.KeyTimeToLiveAsync($"servers:id:{serverId}");
        Assert.NotNull(afterTtl);
        Assert.InRange(afterTtl!.Value, hbTtl - TimeSpan.FromSeconds(1), hbTtl);

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
