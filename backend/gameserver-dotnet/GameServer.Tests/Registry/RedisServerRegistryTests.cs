using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Games;
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

    /// <summary>A fixed, valid base64 Ed25519 public key, so the hash assertions can pin bytes.</summary>
    private const string TestIdentityKey = "ebVWLo/mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ=";

    private static ServerInfo Info(string serverId, string mapId, int players = 0,
        string identityKey = TestIdentityKey) =>
        new(serverId, mapId, "10.0.0.5:9200", "kcp", 100, players, identityKey);

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
        await reg.RegisterAsync(Info(serverId, "map_shape", players: 7), RegistrationScope.MapIndexed, default);

        var db = mux.GetDatabase();
        var hash = (await db.HashGetAllAsync($"servers:id:{serverId}"))
            .ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

        // Field names are a contract with the Go gateway's infoFromFields().
        Assert.Equal(serverId, hash["server_id"]);
        Assert.Equal("map_shape", hash["map_id"]);
        Assert.Equal("10.0.0.5:9200", hash["addr"]);
        Assert.Equal("kcp", hash["transport"]);
        Assert.Equal("100", hash["capacity"]);
        Assert.Equal("7", hash["player_count"]);
        // ADR-25. The gateway reads this field with sealed.DecodeIdentityKey and hands the
        // decoded bytes to the client; a rename or a different base64 alphabet here is a
        // silent break, because the gateway would read an empty key and every client that
        // requires identity would refuse a server that is perfectly capable of it.
        Assert.Equal(TestIdentityKey, hash["identity_key"]);
        Assert.Equal(GameId.DefaultGameId, hash["game_id"]);
        Assert.Equal(8, hash.Count); // no extra fields the gateway would not expect

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

    /// <summary>
    /// A server that publishes no identity key still writes a complete, readable entry
    /// (ADR-25), because an empty key must not take a map offline.
    /// </summary>
    /// <remarks>
    /// The field is written EMPTY rather than omitted. Omitting it would make an old entry
    /// and a new one differ in shape as well as in content, and the Go reader's
    /// <c>f["identity_key"]</c> yields "" for both — so writing the empty string keeps one
    /// shape for the gateway to parse and one meaning for it to act on.
    /// </remarks>
    [SkippableFact]
    public async Task Register_WithNoIdentity_WritesAnEmptyFieldRatherThanOmittingIt()
    {
        _redis.SkipUnlessAvailable(nameof(Register_WithNoIdentity_WritesAnEmptyFieldRatherThanOmittingIt));
        var (reg, mux) = await ConnectAsync();
        await using var _ = reg;

        string serverId = $"gs-noid-{Guid.NewGuid():N}"[..16];
        await reg.RegisterAsync(Info(serverId, "map_noid", identityKey: ""), RegistrationScope.MapIndexed, default);

        var db = mux.GetDatabase();
        var hash = (await db.HashGetAllAsync($"servers:id:{serverId}"))
            .ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

        Assert.True(hash.ContainsKey("identity_key"));
        Assert.Equal("", hash["identity_key"]);
        Assert.Equal(GameId.DefaultGameId, hash["game_id"]);
        Assert.Equal(8, hash.Count);
        // Still findable: an identity-less server is a server, not a broken entry.
        Assert.True(await db.SetContainsAsync("servers:map:map_noid", serverId));
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
        await reg.RegisterAsync(Info(serverId, "map_hb"), RegistrationScope.MapIndexed, default);

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
        await reg.RegisterAsync(Info(serverId, "map_exp"), RegistrationScope.MapIndexed, default);
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
        await reg.RegisterAsync(Info(serverId, "map_dereg"), RegistrationScope.MapIndexed, default);

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
        await reg.RegisterAsync(Info(serverId, "map_count"), RegistrationScope.MapIndexed, default);

        Assert.True(await reg.UpdatePlayerCountAsync(serverId, 42, default));
        Assert.Equal("42", (await db.HashGetAsync($"servers:id:{serverId}", "player_count")).ToString());

        // Once the entry is gone, a late writer must not recreate it: an HSET on a
        // missing key would produce a hash with NO TTL, i.e. an immortal ghost
        // server the gateway would hand clients to forever.
        await db.KeyDeleteAsync($"servers:id:{serverId}");
        Assert.False(await reg.UpdatePlayerCountAsync(serverId, 9, default));
        Assert.False(await db.KeyExistsAsync($"servers:id:{serverId}"));
    }

    [SkippableTheory]
    [InlineData("foo", "bar", false)]
    [InlineData(GameId.DefaultGameId, "foo", false)]
    [InlineData("foo", GameId.DefaultGameId, false)]
    [InlineData("foo", "foo", true)]
    [InlineData(GameId.DefaultGameId, GameId.DefaultGameId, true)]
    [InlineData(null, GameId.DefaultGameId, true)]
    [InlineData(null, "foo", false)]
    [InlineData("", GameId.DefaultGameId, true)]
    [InlineData("", "foo", false)]
    public async Task Register_ChecksExistingGameBeforeChangingAnyKeys(
        string? storedGame, string requestedGame, bool accepted)
    {
        _redis.SkipUnlessAvailable(nameof(Register_ChecksExistingGameBeforeChangingAnyKeys));
        var (reg, mux) = await ConnectAsync();
        using var connection = mux;
        await using var registry = reg;
        var db = mux.GetDatabase();
        string serverId = $"gs-cas-{Guid.NewGuid():N}";
        string oldMap = $"map_old_{Guid.NewGuid():N}";
        string newMap = $"map_new_{Guid.NewGuid():N}";
        string key = RedisServerRegistry.ServerKey(serverId);
        var oldHash = new List<HashEntry>
        {
            new("server_id", serverId), new("map_id", oldMap), new("addr", "old:9200"),
            new("transport", "kcp"), new("capacity", "50"), new("player_count", "1"),
            new("identity_key", "old-key"),
        };
        if (storedGame != null) oldHash.Add(new HashEntry("game_id", storedGame));
        await db.HashSetAsync(key, oldHash.ToArray());
        string oldIndex = RedisServerRegistry.MapIndexKey(storedGame, oldMap);
        string newIndex = RedisServerRegistry.MapIndexKey(requestedGame, newMap);
        await db.SetAddAsync(oldIndex, serverId);
        var requested = Info(serverId, newMap, players: 9) with { GameId = requestedGame };

        if (accepted)
        {
            await reg.RegisterAsync(requested, RegistrationScope.MapIndexed, default);
            Assert.Equal(requested, RedisServerRegistry.FromHashEntries(await db.HashGetAllAsync(key)));
            Assert.True(await db.SetContainsAsync(newIndex, serverId));
            Assert.NotNull(await db.KeyTimeToLiveAsync(key));
        }
        else
        {
            var error = await Assert.ThrowsAsync<RegistryGameMismatchException>(() =>
                reg.RegisterAsync(requested, RegistrationScope.MapIndexed, default));
            Assert.Equal(serverId, error.ServerId);
            Assert.Equal(requestedGame, error.GameId);
            Assert.Contains(serverId, error.Message);
            Assert.Contains(requestedGame, error.Message);
            var hash = await db.HashGetAllAsync(key);
            Assert.Equal(oldHash.OrderBy(entry => entry.Name.ToString()), hash.OrderBy(entry => entry.Name.ToString()));
            Assert.Null(await db.KeyTimeToLiveAsync(key)); // refusal did not arm a TTL
            Assert.True(await db.SetContainsAsync(oldIndex, serverId));
            Assert.False(await db.KeyExistsAsync(newIndex));
        }
    }

    [SkippableFact]
    public async Task Register_AbsentNonDefaultGameUsesItsOwnMapIndex()
    {
        _redis.SkipUnlessAvailable(nameof(Register_AbsentNonDefaultGameUsesItsOwnMapIndex));
        var (reg, mux) = await ConnectAsync();
        using var connection = mux;
        await using var registry = reg;
        var db = mux.GetDatabase();
        string serverId = $"foo-{Guid.NewGuid():N}";
        var info = Info(serverId, "map_01") with { GameId = "foo" };

        Assert.False(await db.KeyExistsAsync(RedisServerRegistry.ServerKey(serverId)));
        await reg.RegisterAsync(info, RegistrationScope.MapIndexed, default);

        Assert.Equal(info, RedisServerRegistry.FromHashEntries(
            await db.HashGetAllAsync(RedisServerRegistry.ServerKey(serverId))));
        Assert.True(await db.SetContainsAsync("servers:map:foo:map_01", serverId));
        Assert.False(await db.SetContainsAsync("servers:map:map_01", serverId));
    }

    [SkippableTheory]
    [InlineData(GameId.DefaultGameId)]
    [InlineData("foo")]
    public async Task HashOnly_WritesGameButDoesNotCreateAMapIndex(string gameId)
    {
        _redis.SkipUnlessAvailable(nameof(HashOnly_WritesGameButDoesNotCreateAMapIndex));
        var (reg, mux) = await ConnectAsync();
        using var connection = mux;
        await using var registry = reg;
        var db = mux.GetDatabase();
        string serverId = $"{gameId}-{Guid.NewGuid():N}";
        string mapId = $"dungeon_{Guid.NewGuid():N}";
        await reg.RegisterAsync(Info(serverId, mapId) with { GameId = gameId }, RegistrationScope.HashOnly, default);

        Assert.Equal(gameId, (await db.HashGetAsync(RedisServerRegistry.ServerKey(serverId), "game_id")).ToString());
        Assert.Equal(8, await db.HashLengthAsync(RedisServerRegistry.ServerKey(serverId)));
        Assert.NotNull(await db.KeyTimeToLiveAsync(RedisServerRegistry.ServerKey(serverId)));
        Assert.False(await db.KeyExistsAsync(RedisServerRegistry.MapIndexKey(gameId, mapId)));
        Assert.False(await db.KeyExistsAsync(RedisServerRegistry.MapKey(mapId)));
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(GameId.DefaultGameId)]
    [InlineData("foo")]
    public async Task FindByGameAndMap_FiltersForeignAndExpiredEntries(string? gameId)
    {
        _redis.SkipUnlessAvailable(nameof(FindByGameAndMap_FiltersForeignAndExpiredEntries));
        var (reg, mux) = await ConnectAsync();
        using var connection = mux;
        await using var registry = reg;
        var db = mux.GetDatabase();
        string effectiveGame = GameId.GameOrDefault(gameId);
        string mapId = $"map_lookup_{Guid.NewGuid():N}";
        var expected = Info($"{effectiveGame}-{Guid.NewGuid():N}", mapId) with { GameId = effectiveGame };
        var foreign = Info($"bar-{Guid.NewGuid():N}", mapId) with { GameId = "bar" };
        await reg.RegisterAsync(expected, RegistrationScope.MapIndexed, default);
        await reg.RegisterAsync(foreign, RegistrationScope.MapIndexed, default);
        string index = RedisServerRegistry.MapIndexKey(gameId, mapId);
        string expiredId = $"expired-{Guid.NewGuid():N}";
        await db.SetAddAsync(index, [foreign.ServerId, expiredId]);

        var found = await reg.FindByGameAndMapAsync(gameId, mapId, default);

        Assert.Equal(expected, Assert.Single(found));
        Assert.True(await db.SetContainsAsync(index, expiredId)); // lookup need not prune
    }

    [SkippableFact]
    public async Task FindByGameAndMap_LegacyHashBelongsToDefaultGame()
    {
        _redis.SkipUnlessAvailable(nameof(FindByGameAndMap_LegacyHashBelongsToDefaultGame));
        var (reg, mux) = await ConnectAsync();
        using var connection = mux;
        await using var registry = reg;
        var db = mux.GetDatabase();
        string serverId = $"gs-legacy-{Guid.NewGuid():N}";
        string mapId = $"map_legacy_{Guid.NewGuid():N}";
        await reg.RegisterAsync(Info(serverId, mapId), RegistrationScope.MapIndexed, default);
        await db.HashDeleteAsync(RedisServerRegistry.ServerKey(serverId), "game_id");

        var info = Assert.Single(await reg.FindByGameAndMapAsync(null, mapId, default));
        Assert.Equal(serverId, info.ServerId);
        Assert.Equal(GameId.DefaultGameId, info.GameId);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("foo")]
    public async Task Deregister_UsesTheStoredGameAndMap(string? gameId)
    {
        _redis.SkipUnlessAvailable(nameof(Deregister_UsesTheStoredGameAndMap));
        var (reg, mux) = await ConnectAsync();
        using var connection = mux;
        await using var registry = reg;
        var db = mux.GetDatabase();
        string serverId = $"gs-dereg-game-{Guid.NewGuid():N}";
        string storedMap = $"map_stored_{Guid.NewGuid():N}";
        var info = Info(serverId, storedMap) with { GameId = GameId.GameOrDefault(gameId) };
        await reg.RegisterAsync(info, RegistrationScope.MapIndexed, default);
        if (gameId == null) await db.HashDeleteAsync(RedisServerRegistry.ServerKey(serverId), "game_id");

        await reg.DeregisterAsync(serverId, "map_incorrect", default);

        Assert.False(await db.KeyExistsAsync(RedisServerRegistry.ServerKey(serverId)));
        Assert.False(await db.SetContainsAsync(RedisServerRegistry.MapIndexKey(gameId, storedMap), serverId));
    }
}
