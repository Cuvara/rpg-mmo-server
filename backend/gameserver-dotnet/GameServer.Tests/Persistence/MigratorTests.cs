using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace GameServer.Tests.Persistence;

/// <summary>
/// Migration runner behaviour against a real PostgreSQL: ordering, idempotency,
/// checksum drift detection, and per-migration transactional rollback.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MigratorTests
{
    private readonly PostgresFixture _pg;

    public MigratorTests(PostgresFixture pg) => _pg = pg;

    /// <summary>
    /// A migrator bound to its own throwaway database, so schema mutations in one
    /// test can never leak into another.
    /// </summary>
    private async Task<(NpgsqlDataSource Ds, string DbName)> FreshDatabaseAsync()
    {
        string dbName = $"mig_{Guid.NewGuid():N}"[..20];

        await using (var admin = new NpgsqlConnection(PostgresPlayerStore.BuildConnectionString(_pg.Dsn)))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {dbName}", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(PostgresPlayerStore.BuildConnectionString(_pg.Dsn))
        {
            Database = dbName
        };
        return (new NpgsqlDataSourceBuilder(builder.ConnectionString).Build(), dbName);
    }

    private static Migrator MigratorFor(NpgsqlDataSource ds, IReadOnlyList<MigrationScript>? scripts = null)
        => new(ds, NullLogger.Instance, scripts);

    private static MigrationScript Script(int version, string name, string sql)
        => new(version, name, sql, Migrator.ComputeChecksum(sql));

    private static async Task<long> ScalarAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task FreshDatabase_AppliesAllMigrationsInOrder()
    {
        _pg.SkipUnlessAvailable(nameof(FreshDatabase_AppliesAllMigrationsInOrder));

        var (ds, _) = await FreshDatabaseAsync();
        await using var _ds = ds;

        var result = await MigratorFor(ds).ApplyAsync();

        Assert.NotEmpty(result.Applied);
        Assert.Empty(result.AlreadyApplied);
        Assert.Equal(result.Applied.OrderBy(v => v).ToList(), result.Applied); // ascending
        Assert.Equal(Migrator.Embedded.Select(s => s.Version).ToList(), result.Applied);

        // The schema the migrations describe actually exists.
        Assert.Equal(1, await ScalarAsync(ds,
            "SELECT count(*) FROM information_schema.tables WHERE table_name = 'player_states'"));
        Assert.Equal(1, await ScalarAsync(ds,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'player_states_map_id_idx'"));

        // History rows carry the binary's checksums.
        await using var cmd = ds.CreateCommand("SELECT version, name, checksum FROM schema_migrations ORDER BY version");
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(int Version, string Name, string Checksum)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));

        Assert.Equal(Migrator.Embedded.Count, rows.Count);
        foreach (var script in Migrator.Embedded)
        {
            var row = rows.Single(r => r.Version == script.Version);
            Assert.Equal(script.Name, row.Name);
            Assert.Equal(script.Checksum, row.Checksum);
            Assert.StartsWith("sha256:", row.Checksum);
        }
    }

    [SkippableFact]
    public async Task SecondRun_IsNoOp()
    {
        _pg.SkipUnlessAvailable(nameof(SecondRun_IsNoOp));

        var (ds, _) = await FreshDatabaseAsync();
        await using var _ds = ds;

        var first = await MigratorFor(ds).ApplyAsync();
        var second = await MigratorFor(ds).ApplyAsync();

        Assert.NotEmpty(first.Applied);
        Assert.True(second.NoOp);
        Assert.Empty(second.Applied);
        Assert.Equal(first.Applied, second.AlreadyApplied);

        // No duplicate history rows.
        Assert.Equal(Migrator.Embedded.Count, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
    }

    [SkippableFact]
    public async Task AppliesOnlyPendingMigrations_WhenBinaryGainsANewOne()
    {
        _pg.SkipUnlessAvailable(nameof(AppliesOnlyPendingMigrations_WhenBinaryGainsANewOne));

        var (ds, _) = await FreshDatabaseAsync();
        await using var _ds = ds;

        var v1 = Script(1, "001_init", "CREATE TABLE t1 (id int PRIMARY KEY);");
        var v2 = Script(2, "002_more", "CREATE TABLE t2 (id int PRIMARY KEY);");

        var first = await MigratorFor(ds, [v1]).ApplyAsync();
        var second = await MigratorFor(ds, [v1, v2]).ApplyAsync();

        Assert.Equal([1], first.Applied);
        Assert.Equal([2], second.Applied);          // only the new one
        Assert.Equal([1], second.AlreadyApplied);
        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM information_schema.tables WHERE table_name='t2'"));
    }

    [SkippableFact]
    public async Task EditedMigration_FailsWithDriftError()
    {
        _pg.SkipUnlessAvailable(nameof(EditedMigration_FailsWithDriftError));

        var (ds, _) = await FreshDatabaseAsync();
        await using var _ds = ds;

        var original = Script(1, "001_init", "CREATE TABLE t1 (id int PRIMARY KEY);");
        await MigratorFor(ds, [original]).ApplyAsync();

        // Same version, different statements — the file was edited after shipping.
        var tampered = Script(1, "001_init", "CREATE TABLE t1 (id int PRIMARY KEY, extra text);");

        var ex = await Assert.ThrowsAsync<MigrationDriftException>(
            () => MigratorFor(ds, [tampered]).ApplyAsync());

        Assert.Contains("modified after it was applied", ex.Message);
        Assert.Contains(original.Checksum, ex.Message);
        Assert.Contains(tampered.Checksum, ex.Message);
    }

    [SkippableFact]
    public async Task CommentOnlyEdit_DoesNotTripDrift()
    {
        _pg.SkipUnlessAvailable(nameof(CommentOnlyEdit_DoesNotTripDrift));

        var (ds, _) = await FreshDatabaseAsync();
        await using var _ds = ds;

        var before = Script(1, "001_init", "-- old note\nCREATE TABLE t1 (id int PRIMARY KEY);");
        var after = Script(1, "001_init", "-- reworded note\n-- extra line\nCREATE TABLE  t1 (id int PRIMARY KEY);");

        await MigratorFor(ds, [before]).ApplyAsync();
        var second = await MigratorFor(ds, [after]).ApplyAsync();

        // Checksums cover statements, not prose — rewording a comment is safe.
        Assert.Equal(before.Checksum, after.Checksum);
        Assert.True(second.NoOp);
    }

    [SkippableFact]
    public async Task FailingMigration_RollsBackEntirelyAndRecordsNothing()
    {
        _pg.SkipUnlessAvailable(nameof(FailingMigration_RollsBackEntirelyAndRecordsNothing));

        var (ds, _) = await FreshDatabaseAsync();
        await using var _ds = ds;

        var good = Script(1, "001_good", "CREATE TABLE ok_table (id int PRIMARY KEY);");
        // Valid first statement, then a syntax error: proves the whole script is
        // one transaction, not statement-by-statement autocommit.
        var bad = Script(2, "002_bad", "CREATE TABLE half_table (id int PRIMARY KEY); THIS IS NOT SQL;");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MigratorFor(ds, [good, bad]).ApplyAsync());
        Assert.Contains("002_bad", ex.Message);
        Assert.Contains("rolled back", ex.Message);

        // Migration 1 committed; migration 2 left no table and no history row.
        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM information_schema.tables WHERE table_name='ok_table'"));
        Assert.Equal(0, await ScalarAsync(ds, "SELECT count(*) FROM information_schema.tables WHERE table_name='half_table'"));
        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
        Assert.Equal(0, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations WHERE version = 2"));

        // The failure is recoverable: fixing the script and re-running applies it.
        var fixedUp = Script(2, "002_bad", "CREATE TABLE half_table (id int PRIMARY KEY);");
        var retry = await MigratorFor(ds, [good, fixedUp]).ApplyAsync();
        Assert.Equal([2], retry.Applied);
    }

    [SkippableFact]
    public async Task ConcurrentMigrators_ApplyEachMigrationExactlyOnce()
    {
        _pg.SkipUnlessAvailable(nameof(ConcurrentMigrators_ApplyEachMigrationExactlyOnce));

        var (ds, _) = await FreshDatabaseAsync();
        await using var _ds = ds;

        // Eight servers booting at once against one database.
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => MigratorFor(ds).ApplyAsync()));

        Assert.Equal(Migrator.Embedded.Count, results.Sum(r => r.Applied.Count));
        Assert.Equal(Migrator.Embedded.Count, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
    }

    [SkippableFact]
    public async Task DatabaseAheadOfBinary_IsAllowed()
    {
        _pg.SkipUnlessAvailable(nameof(DatabaseAheadOfBinary_IsAllowed));

        var (ds, _) = await FreshDatabaseAsync();
        await using var _ds = ds;

        var v1 = Script(1, "001_init", "CREATE TABLE t1 (id int PRIMARY KEY);");
        var v2 = Script(2, "002_later", "CREATE TABLE t2 (id int PRIMARY KEY);");
        await MigratorFor(ds, [v1, v2]).ApplyAsync();

        // Roll back to a binary that only knows about 001 — must still start.
        var result = await MigratorFor(ds, [v1]).ApplyAsync();

        Assert.True(result.NoOp);
        Assert.Contains(1, result.AlreadyApplied);
        Assert.Contains(2, result.AlreadyApplied);
    }

    // ── Embedded resources ──

    [Fact]
    public void EmbeddedMigrations_AreDiscoveredAndWellFormed()
    {
        Assert.NotEmpty(Migrator.Embedded);

        // Versions are unique and ascending, names are <number>_<description>.
        var versions = Migrator.Embedded.Select(s => s.Version).ToList();
        Assert.Equal(versions.Distinct().Count(), versions.Count);
        Assert.Equal(versions.OrderBy(v => v).ToList(), versions);

        Assert.Contains(Migrator.Embedded, s => s.Version == 1 && s.Name == "001_init");
        foreach (var script in Migrator.Embedded)
        {
            Assert.False(string.IsNullOrWhiteSpace(script.Sql));
            Assert.StartsWith("sha256:", script.Checksum);
        }
    }

    [SkippableFact]
    public void EmbeddedMigrations_MatchDeployCopies()
    {
        string? migrationsDir = PostgresPlayerStoreTests.FindRepoFile(
            Path.Combine("backend", "deploy", "db", "migrations", "gamestate", "001_init.sql"));
        Skip.If(migrationsDir is null,
            "deploy migrations not found (running outside the repo tree)");

        string deployDir = Path.GetDirectoryName(migrationsDir)!;

        // Every embedded script has an identical ops copy, and vice versa — a
        // migration that exists only in the binary cannot be applied by hand, and
        // one that exists only on disk would never ship.
        var deployFiles = Directory.GetFiles(deployDir, "*.sql").Select(Path.GetFileName).Order().ToList();
        var embeddedFiles = Migrator.Embedded.Select(s => s.Name + ".sql").Order().ToList();
        Assert.Equal(embeddedFiles, deployFiles);

        foreach (var script in Migrator.Embedded)
        {
            string onDisk = File.ReadAllText(Path.Combine(deployDir, script.Name + ".sql"));
            Assert.Equal(Migrator.Normalize(script.Sql), Migrator.Normalize(onDisk));
        }
    }

    [SkippableFact]
    public void InitGamestateSql_MatchesFirstMigration()
    {
        string? path = PostgresPlayerStoreTests.FindRepoFile(
            Path.Combine("backend", "deploy", "db", "init-gamestate.sql"));
        Skip.If(path is null, "init-gamestate.sql not found (running outside the repo tree)");

        // init-gamestate.sql seeds a brand-new postgres volume via docker-entrypoint,
        // before any gameserver connects. It must describe exactly what 001 describes,
        // or a fresh volume and a migrated volume would disagree.
        var first = Migrator.Embedded.Single(s => s.Version == 1);
        Assert.Equal(Migrator.Normalize(first.Sql), Migrator.Normalize(File.ReadAllText(path!)));
    }

    [Fact]
    public void Normalize_IgnoresCommentsAndWhitespace_ButNotStatements()
    {
        Assert.Equal(
            Migrator.Normalize("CREATE TABLE t (id int);"),
            Migrator.Normalize("-- header\n\n  CREATE   TABLE\tt (id int);\n-- trailer\n"));

        Assert.NotEqual(
            Migrator.Normalize("CREATE TABLE t (id int);"),
            Migrator.Normalize("CREATE TABLE t (id bigint);"));

        // A '--' inside a string literal is data, not a comment, and must survive.
        Assert.Contains("--", Migrator.Normalize("INSERT INTO t VALUES ('a -- b');"));
    }
}
