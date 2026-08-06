using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace GameServer.Tests.Persistence;

/// <summary>
/// Store behaviour verified against a real PostgreSQL instance (ephemeral docker
/// container, random loopback port). Skips cleanly when docker is unavailable.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresPlayerStoreTests
{
    private readonly PostgresFixture _pg;

    public PostgresPlayerStoreTests(PostgresFixture pg) => _pg = pg;

    private async Task<PostgresPlayerStore> MigratedStoreAsync()
    {
        var store = await _pg.ConnectStoreAsync();
        await store.MigrateAsync();
        return store;
    }

    [SkippableFact]
    public async Task SaveThenLoad_RoundTripsAllColumns()
    {
        _pg.SkipUnlessAvailable(nameof(SaveThenLoad_RoundTripsAllColumns));

        await using var store = await MigratedStoreAsync();
        string userId = $"user-{Guid.NewGuid():N}";

        await store.SavePlayerAsync(new PlayerState(userId, 12.5f, -3.25f, 77, 120, "map_07"), default);

        var loaded = await store.LoadPlayerAsync(userId, default);

        Assert.NotNull(loaded);
        Assert.Equal(userId, loaded!.UserId);
        Assert.Equal(12.5f, loaded.X);
        Assert.Equal(-3.25f, loaded.Y);
        Assert.Equal(77, loaded.Hp);
        Assert.Equal(120, loaded.MaxHp);
        Assert.Equal("map_07", loaded.MapId);
    }

    [SkippableFact]
    public async Task Save_Twice_UpsertsInPlaceAndBumpsUpdatedAt()
    {
        _pg.SkipUnlessAvailable(nameof(Save_Twice_UpsertsInPlaceAndBumpsUpdatedAt));

        await using var store = await MigratedStoreAsync();
        string userId = $"user-{Guid.NewGuid():N}";

        await store.SavePlayerAsync(new PlayerState(userId, 1, 1, 10, 100, "map_01"), default);
        var firstUpdatedAt = await QueryUpdatedAtAsync(userId);
        long rowsAfterFirst = await CountRowsAsync(userId);

        await Task.Delay(50);
        await store.SavePlayerAsync(new PlayerState(userId, 9, 8, 42, 100, "map_02"), default);

        var loaded = await store.LoadPlayerAsync(userId, default);
        Assert.NotNull(loaded);
        Assert.Equal(9, loaded!.X);
        Assert.Equal(8, loaded.Y);
        Assert.Equal(42, loaded.Hp);
        Assert.Equal("map_02", loaded.MapId);

        // Upsert, not insert: still exactly one row, and updated_at moved forward.
        Assert.Equal(1, rowsAfterFirst);
        Assert.Equal(1, await CountRowsAsync(userId));
        Assert.True(await QueryUpdatedAtAsync(userId) > firstUpdatedAt, "updated_at should advance on upsert");
    }

    [SkippableFact]
    public async Task LoadPlayer_Missing_ReturnsNull()
    {
        _pg.SkipUnlessAvailable(nameof(LoadPlayer_Missing_ReturnsNull));

        await using var store = await MigratedStoreAsync();

        // Same not-found contract as MemoryPlayerStore: null, never an exception.
        Assert.Null(await store.LoadPlayerAsync($"missing-{Guid.NewGuid():N}", default));
        Assert.Null(await store.LoadPlayerAsync("", default));
    }

    [SkippableFact]
    public async Task DeletePlayer_RemovesRow_AndMissingDeleteIsNoOp()
    {
        _pg.SkipUnlessAvailable(nameof(DeletePlayer_RemovesRow_AndMissingDeleteIsNoOp));

        await using var store = await MigratedStoreAsync();
        string userId = $"user-{Guid.NewGuid():N}";

        await store.SavePlayerAsync(new PlayerState(userId, 1, 2, 3, 4, "map_01"), default);
        await store.DeletePlayerAsync(userId);
        Assert.Null(await store.LoadPlayerAsync(userId, default));

        await store.DeletePlayerAsync(userId); // no-op, must not throw
    }

    [SkippableFact]
    public async Task Migrate_RunTwice_IsIdempotentAndPreservesData()
    {
        _pg.SkipUnlessAvailable(nameof(Migrate_RunTwice_IsIdempotentAndPreservesData));

        await using var store = await MigratedStoreAsync();
        string userId = $"user-{Guid.NewGuid():N}";
        await store.SavePlayerAsync(new PlayerState(userId, 5, 6, 7, 8, "map_03"), default);

        // Booting again must not throw and must not wipe existing rows.
        await store.MigrateAsync();
        await store.MigrateAsync();

        var loaded = await store.LoadPlayerAsync(userId, default);
        Assert.NotNull(loaded);
        Assert.Equal(5, loaded!.X);
        Assert.Equal(1, await CountIndexAsync("player_states_map_id_idx"));
    }

    [Fact]
    public async Task Connect_UnreachableDatabase_Throws()
    {
        // No docker needed: nothing listens on this port.
        int deadPort = 1;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgresPlayerStore.ConnectAsync(
                $"postgres://u:secret@127.0.0.1:{deadPort}/gamestate?sslmode=disable&connect_timeout=2"));

        Assert.Contains("postgres connect failed", ex.Message);
        Assert.DoesNotContain("secret", ex.Message); // password never leaks into logs
    }

    [SkippableFact]
    public async Task Save_AfterDatabaseGoesAway_SurfacesErrorAndIncrementsMetric()
    {
        _pg.SkipUnlessAvailable(nameof(Save_AfterDatabaseGoesAway_SurfacesErrorAndIncrementsMetric));

        // Dedicated container — this test destroys the server mid-flight.
        await using var victim = await EphemeralPostgres.TryStartAsync();
        if (victim is null)
        {
            Console.WriteLine("[SKIP] could not start dedicated postgres container");
            return;
        }

        var store = await PostgresPlayerStore.ConnectAsync(victim.Dsn);
        await store.MigrateAsync();
        await store.SavePlayerAsync(new PlayerState("u1", 1, 1, 10, 100, "map_01"), default);

        victim.Kill();

        // 1. The store surfaces the failure rather than swallowing it.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.SavePlayerAsync(new PlayerState("u1", 2, 2, 10, 100, "map_01"), default));

        // 2. AsyncSaver turns that failure into gameserver_player_saves_total{status="error"}.
        using var harness = new MetricHarness(nameof(Save_AfterDatabaseGoesAway_SurfacesErrorAndIncrementsMetric));
        using var world = new GameWorld();
        world.AddEntity(TestHelpers.CreatePlayer("u1", 2, 2));

        var saver = new AsyncSaver(store, world, "map_01", TimeSpan.FromSeconds(30),
            NullLogger.Instance, harness.Metrics);
        await saver.SaveAllAsync();

        Assert.Equal(1, harness.SaveCount("error"));
        Assert.Equal(0, harness.SaveCount("ok"));

        await store.DisposeAsync();
    }

    // ── DSN parsing / masking (no database required) ──

    [Theory]
    [InlineData("postgres://game:localdev@localhost:5433/gamestate?sslmode=disable",
                "postgres://game:****@localhost:5433/gamestate?sslmode=disable")]
    [InlineData("postgresql://game:p%40ss@db:5432/gamestate", "postgresql://game:****@db:5432/gamestate")]
    [InlineData("postgres://localhost:5432/gamestate", "postgres://localhost:5432/gamestate")]
    [InlineData("Host=db;Port=5432;Database=gamestate;Username=game;Password=localdev",
                "Host=db;Port=5432;Database=gamestate;Username=game;Password=****")]
    [InlineData("", "")]
    public void MaskDsn_RedactsPassword(string dsn, string expected)
        => Assert.Equal(expected, PostgresPlayerStore.MaskDsn(dsn));

    [Fact]
    public void BuildConnectionString_ParsesLibpqUrl()
    {
        string cs = PostgresPlayerStore.BuildConnectionString(
            "postgres://game:localdev@localhost:5433/gamestate?sslmode=disable&application_name=gs");
        var b = new NpgsqlConnectionStringBuilder(cs);

        Assert.Equal("localhost", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("gamestate", b.Database);
        Assert.Equal("game", b.Username);
        Assert.Equal("localdev", b.Password);
        Assert.Equal(SslMode.Disable, b.SslMode);
        Assert.Equal("gs", b.ApplicationName);
        Assert.Equal(PostgresPlayerStore.DefaultCommandTimeoutSeconds, b.CommandTimeout);
        Assert.Equal(10, b.Timeout); // bounded connect timeout -> fail fast at boot
    }

    [Fact]
    public void BuildConnectionString_PassesThroughKeywordForm_AndRejectsMissingDatabase()
    {
        var b = new NpgsqlConnectionStringBuilder(PostgresPlayerStore.BuildConnectionString(
            "Host=db;Database=gamestate;Username=game;Password=pw;Timeout=3"));
        Assert.Equal("db", b.Host);
        Assert.Equal(3, b.Timeout); // explicit timeout is respected

        Assert.Throws<ArgumentException>(() =>
            PostgresPlayerStore.BuildConnectionString("postgres://game:pw@localhost:5432/"));
    }

    // ── Helpers ──

    internal static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = new NpgsqlConnection(PostgresPlayerStore.BuildConnectionString(_pg.Dsn));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<long> CountRowsAsync(string userId)
        => ScalarAsync<long>("SELECT count(*) FROM player_states WHERE user_id = @u",
            new NpgsqlParameter("u", userId));

    private Task<DateTime> QueryUpdatedAtAsync(string userId)
        => ScalarAsync<DateTime>("SELECT updated_at FROM player_states WHERE user_id = @u",
            new NpgsqlParameter("u", userId));

    private Task<long> CountIndexAsync(string indexName)
        => ScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE indexname = @n",
            new NpgsqlParameter("n", indexName));

    /// <summary>Minimal in-memory metric reader for the save counter.</summary>
    internal sealed class MetricHarness : IDisposable
    {
        public GameMetrics Metrics { get; }
        private readonly List<Metric> _exported = new();
        private readonly MeterProvider _provider;

        public MetricHarness(string testName, string mapId = "map_01")
        {
            string meterName = $"rpg.gameserver.test.{testName}.{Guid.NewGuid():N}";
            Metrics = new GameMetrics(mapId, meterName);
            _provider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(meterName)
                .AddInMemoryExporter(_exported)
                .Build()!;
        }

        public long SaveCount(string status)
        {
            _exported.Clear();
            _provider.ForceFlush();

            long total = 0;
            var metric = _exported.FirstOrDefault(m => m.Name == "gameserver.player.saves");
            if (metric is null) return 0;

            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                bool match = false;
                foreach (var tag in point.Tags)
                {
                    if (tag.Key == "status" && (string?)tag.Value == status) match = true;
                }
                if (match) total += point.GetSumLong();
            }
            return total;
        }

        public void Dispose()
        {
            _provider.Dispose();
            Metrics.Dispose();
        }
    }
}
