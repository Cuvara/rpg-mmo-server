using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Registry;
using StackExchange.Redis;

namespace GameServer.Tests.Registry;

/// <summary>
/// The self-healing contract. The failure this replaces was structural: a
/// deploy-time shell script wrote one registry entry with a 3600s TTL and nothing
/// ever refreshed it, so a Redis wipe left every map unjoinable until a human
/// re-ran the script. These tests prove the server now repairs its own entry with
/// no intervention.
/// </summary>
[Collection(RedisCollection.Name)]
public class RegistrationServiceTests
{
    private readonly RedisFixture _redis;

    public RegistrationServiceTests(RedisFixture redis) => _redis = redis;

    private static RegistrationOptions Opts(string serverId, string mapId, TimeSpan ttl) => new()
    {
        ServerId = serverId,
        MapId = mapId,
        PublicAddr = "203.0.113.7:9200",
        Transport = "tcp",
        Capacity = 64,
        Ttl = ttl
    };

    private async Task<(RedisServerRegistry reg, IConnectionMultiplexer mux)> ConnectAsync(TimeSpan ttl)
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(RedisServerRegistry.BuildOptions(_redis.Addr, null));
        return (new RedisServerRegistry(mux, ttl, NullLogger.Instance), mux);
    }

    [SkippableFact]
    public async Task StartAsync_RegistersImmediately()
    {
        _redis.SkipUnlessAvailable(nameof(StartAsync_RegistersImmediately));
        var ttl = TimeSpan.FromSeconds(10);
        var (reg, mux) = await ConnectAsync(ttl);
        string serverId = $"gs-start-{Guid.NewGuid():N}"[..16];

        await using var svc = new RegistrationService(
            reg, Opts(serverId, "map_start", ttl), () => 0, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var hash = await mux.GetDatabase().HashGetAllAsync($"servers:id:{serverId}");
        Assert.NotEmpty(hash);
        Assert.Equal("203.0.113.7:9200",
            hash.First(e => e.Name == "addr").Value.ToString());

        cts.Cancel();
    }

    [SkippableFact]
    public async Task Heartbeat_KeepsTheEntryAliveBeyondItsTtl()
    {
        _redis.SkipUnlessAvailable(nameof(Heartbeat_KeepsTheEntryAliveBeyondItsTtl));
        // TTL 2s, so the heartbeat interval is ~667ms. Waiting 5s means the entry
        // has outlived more than two full TTLs — it can only still be there because
        // something refreshed it.
        var ttl = TimeSpan.FromSeconds(2);
        var (reg, mux) = await ConnectAsync(ttl);
        string serverId = $"gs-alive-{Guid.NewGuid():N}"[..16];

        await using var svc = new RegistrationService(
            reg, Opts(serverId, "map_alive", ttl), () => 0, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        await Task.Delay(5000);

        Assert.True(await mux.GetDatabase().KeyExistsAsync($"servers:id:{serverId}"),
            "the entry expired despite a running heartbeat");

        cts.Cancel();
    }

    [SkippableFact]
    public async Task WipedRegistry_IsRepairedByTheNextHeartbeat_WithNoIntervention()
    {
        _redis.SkipUnlessAvailable(nameof(WipedRegistry_IsRepairedByTheNextHeartbeat_WithNoIntervention));
        // THE regression test for G1. Redis loses everything; nobody touches the
        // server; the entry must come back on its own.
        var ttl = TimeSpan.FromSeconds(3);
        var (reg, mux) = await ConnectAsync(ttl);
        string serverId = $"gs-heal-{Guid.NewGuid():N}"[..16];
        var db = mux.GetDatabase();

        await using var svc = new RegistrationService(
            reg, Opts(serverId, "map_heal", ttl), () => 0, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        Assert.True(await db.KeyExistsAsync($"servers:id:{serverId}"));

        // Simulate the wipe: the key and its index are gone, exactly as after a
        // FLUSHALL or a failover onto an empty replica.
        await db.KeyDeleteAsync($"servers:id:{serverId}");
        await db.KeyDeleteAsync("servers:map:map_heal");
        Assert.False(await db.KeyExistsAsync($"servers:id:{serverId}"));

        // One heartbeat interval is ttl/3 = 1s; allow a generous margin.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !await db.KeyExistsAsync($"servers:id:{serverId}"))
        {
            await Task.Delay(200);
        }

        Assert.True(await db.KeyExistsAsync($"servers:id:{serverId}"),
            "the registry entry never came back — a Redis wipe would still need a human");
        // The map index must be rebuilt too, otherwise the gateway's FindByMapID
        // finds nothing even though the hash exists.
        Assert.True(await db.SetContainsAsync("servers:map:map_heal", serverId),
            "the map index was not rebuilt, so the gateway still cannot find this server");

        cts.Cancel();
    }

    [SkippableFact]
    public async Task RedisOutage_DoesNotKillTheService_AndItReRegistersOnReconnect()
    {
        _redis.SkipUnlessAvailable(nameof(RedisOutage_DoesNotKillTheService_AndItReRegistersOnReconnect));
        // A dedicated container, because this one gets stopped and started and must
        // not disturb the other tests in the collection.
        await using var redis = await EphemeralRedis.TryStartAsync();
        Skip.If(redis is null, "docker unavailable, no redis to test against");

        var ttl = TimeSpan.FromSeconds(3);
        var mux = await ConnectionMultiplexer.ConnectAsync(
            RedisServerRegistry.BuildOptions(redis!.Addr, null));
        var reg = new RedisServerRegistry(mux, ttl, NullLogger.Instance);
        string serverId = $"gs-outage-{Guid.NewGuid():N}"[..16];

        await using var svc = new RegistrationService(
            reg, Opts(serverId, "map_outage", ttl), () => 0, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        Assert.True(await mux.GetDatabase().KeyExistsAsync($"servers:id:{serverId}"));

        // Redis goes away. The service must survive it — no crash, no unobserved
        // exception — and keep trying.
        redis.Stop();
        await Task.Delay(3000);

        // Redis comes back empty (no volume), which is the real disaster shape.
        redis.Start();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        bool back = false;
        while (DateTime.UtcNow < deadline && !back)
        {
            try { back = await mux.GetDatabase().KeyExistsAsync($"servers:id:{serverId}"); }
            catch { /* still reconnecting */ }
            if (!back) await Task.Delay(300);
        }

        Assert.True(back,
            "the server never re-registered after Redis came back — the outage would still need a human");

        cts.Cancel();
        await mux.CloseAsync();
        mux.Dispose();
    }

    [SkippableFact]
    public async Task PlayerCount_IsPublishedWhenItChanges()
    {
        _redis.SkipUnlessAvailable(nameof(PlayerCount_IsPublishedWhenItChanges));
        var ttl = TimeSpan.FromSeconds(10);
        var (reg, mux) = await ConnectAsync(ttl);
        string serverId = $"gs-count-{Guid.NewGuid():N}"[..16];
        var db = mux.GetDatabase();

        int players = 0;
        await using var svc = new RegistrationService(
            reg, Opts(serverId, "map_count", ttl), () => players, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        Assert.Equal("0", (await db.HashGetAsync($"servers:id:{serverId}", "player_count")).ToString());

        players = 3;
        await svc.PublishPlayerCountIfChangedAsync(default);
        Assert.Equal("3", (await db.HashGetAsync($"servers:id:{serverId}", "player_count")).ToString());

        players = 1;
        await svc.PublishPlayerCountIfChangedAsync(default);
        Assert.Equal("1", (await db.HashGetAsync($"servers:id:{serverId}", "player_count")).ToString());

        cts.Cancel();
    }

    [SkippableFact]
    public async Task Deregister_RemovesTheEntryImmediately()
    {
        _redis.SkipUnlessAvailable(nameof(Deregister_RemovesTheEntryImmediately));
        var ttl = TimeSpan.FromSeconds(30);
        var (reg, mux) = await ConnectAsync(ttl);
        string serverId = $"gs-stop-{Guid.NewGuid():N}"[..16];
        var db = mux.GetDatabase();

        await using var svc = new RegistrationService(
            reg, Opts(serverId, "map_stop", ttl), () => 0, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        Assert.True(await db.KeyExistsAsync($"servers:id:{serverId}"));

        await svc.DeregisterAsync();

        // Gone now, not in 30 seconds: a shutting-down server must stop attracting
        // joins immediately rather than black-holing them for a whole TTL.
        Assert.False(await db.KeyExistsAsync($"servers:id:{serverId}"));

        cts.Cancel();
    }
}
