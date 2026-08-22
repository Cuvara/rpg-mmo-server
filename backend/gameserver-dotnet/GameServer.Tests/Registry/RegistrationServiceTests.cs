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
        // TTL 3s, so the heartbeat interval is 1s: the entry is refreshed three times per
        // TTL, the same 3x margin production runs at (15s TTL -> 5s interval). Waiting 8s
        // means the entry has outlived more than two and a half full TTLs — it can only
        // still be there because something refreshed it.
        //
        // This was a 2s TTL with a comment claiming a "~667ms" interval. It was not:
        // RegistryDefaults.HeartbeatInterval is Math.Max(1000, ttl/3), so a 2s TTL hits the
        // 1000ms floor and the test ran on a 2x margin while its comment described 3x. 3s
        // is the smallest TTL the floor does not distort, and the wait is raised with it so
        // the assertion still spans more than two TTLs — raising the TTL alone would have
        // left a 5s wait shorter than one TTL, which a dead heartbeat would also survive.
        var ttl = TimeSpan.FromSeconds(3);
        var (reg, mux) = await ConnectAsync(ttl);
        string serverId = $"gs-alive-{Guid.NewGuid():N}"[..16];

        await using var svc = new RegistrationService(
            reg, Opts(serverId, "map_alive", ttl), () => 0, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        await Task.Delay(8000);

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
        //
        // Stopwatch, not DateTime.UtcNow: this host's CLOCK_REALTIME runs 10-17% fast and
        // has been observed stepping backwards (#153, #175), so a wall-clock deadline is
        // not the budget it claims to be — a forward step can end a "15s" wait early and
        // fail an assertion about the product for a reason that has nothing to do with it.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15)
               && !await db.KeyExistsAsync($"servers:id:{serverId}"))
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
        // SkipUnlessAvailable above already proved a docker daemon answers here, so a
        // dedicated container failing to start now is an infrastructure failure and must
        // not be laundered into a skip that reports green over unrun coverage (#175).
        // StartAsync, not TryStartAsync: the latter returns only the container and throws the
        // reason away, so this assertion used to fail with nothing but "could not be started".
        // It did exactly that during the #214 work — one run in ten — and the run could not
        // say whether the cause was the vsock transient this fixture now retries, a genuine
        // container fault, or something else. A reason that was computed and discarded is the
        // same defect as no reason at all.
        var started = await EphemeralRedis.StartAsync();
        await using var redis = started.Container;
        Assert.True(redis is not null,
            "docker is available but a dedicated redis container could not be started — " +
            $"an infrastructure failure, not a missing dependency. Cause: {started.Failure}");

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

        // Stopwatch, not DateTime.UtcNow — same reason as above (#153, #175).
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool back = false;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(45) && !back)
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
