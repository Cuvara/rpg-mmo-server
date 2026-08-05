using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Persistence;

/// <summary>One numbered migration script embedded in the binary.</summary>
/// <param name="Version">Numeric prefix of the file name (<c>001_init.sql</c> -> 1).</param>
/// <param name="Name">File name without the extension (<c>001_init</c>).</param>
/// <param name="Sql">Raw script contents, applied verbatim.</param>
/// <param name="Checksum">
/// <c>sha256:&lt;hex&gt;</c> over the *normalized* SQL — see <see cref="Migrator.Normalize"/>.
/// </param>
public sealed record MigrationScript(int Version, string Name, string Sql, string Checksum);

/// <summary>Raised when the database disagrees with the binary about schema history.</summary>
public sealed class MigrationDriftException : Exception
{
    public MigrationDriftException(string message) : base(message) { }
}

/// <summary>Outcome of one <see cref="Migrator.ApplyAsync"/> run.</summary>
/// <param name="Applied">Versions applied by this run, ascending.</param>
/// <param name="AlreadyApplied">Versions that were already recorded and verified.</param>
public sealed record MigrationResult(IReadOnlyList<int> Applied, IReadOnlyList<int> AlreadyApplied)
{
    /// <summary>True when this run changed nothing.</summary>
    public bool NoOp => Applied.Count == 0;
}

/// <summary>
/// Numbered, checksummed, transactional schema migrations for the game-state
/// database.
///
/// Scripts live in <c>GameServer/Persistence/Migrations/*.sql</c> and are embedded
/// as assembly resources, so the binary is self-contained — no migration
/// directory has to be shipped alongside it. Resource *streams* are AOT-safe
/// (unlike reflection over types), so this works under NativeAOT.
///
/// Guarantees:
/// <list type="bullet">
///   <item>Each pending script runs in its own transaction together with its
///         <c>schema_migrations</c> row — a failing script leaves no partial state
///         and no version record.</item>
///   <item>Scripts apply in ascending version order and only once.</item>
///   <item>Checksums of already-applied scripts are verified on every run; an
///         edited-after-shipping migration fails loudly instead of silently
///         diverging across environments.</item>
///   <item>A session-level advisory lock serialises concurrent runners, so
///         several gameservers booting at once cannot race.</item>
/// </list>
/// </summary>
public sealed class Migrator
{
    /// <summary>Advisory lock key — arbitrary but must be stable across versions.</summary>
    private const long AdvisoryLockKey = 0x52504720_44424D31; // "RPG DBM1"

    /// <summary>Statement timeout for the bootstrap + bookkeeping statements, in seconds.</summary>
    private const int BookkeepingTimeoutSeconds = 30;

    /// <summary>Statement timeout for an individual migration script, in seconds.</summary>
    public const int ScriptTimeoutSeconds = 300;

    private const string CreateHistorySql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version    integer     PRIMARY KEY,
            name       text        NOT NULL,
            checksum   text        NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    private static readonly Lazy<IReadOnlyList<MigrationScript>> LazyEmbedded = new(LoadEmbedded);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<MigrationScript> _scripts;

    /// <summary>Migration scripts embedded in this binary, ascending by version.</summary>
    public static IReadOnlyList<MigrationScript> Embedded => LazyEmbedded.Value;

    /// <param name="dataSource">Pooled data source for the game-state database.</param>
    /// <param name="logger">Destination for per-migration progress lines.</param>
    /// <param name="scripts">Script set override; defaults to <see cref="Embedded"/> (tests pass their own).</param>
    public Migrator(NpgsqlDataSource dataSource, ILogger logger, IReadOnlyList<MigrationScript>? scripts = null)
    {
        _dataSource = dataSource;
        _logger = logger;
        _scripts = scripts ?? Embedded;
    }

    /// <summary>
    /// Apply every pending migration in order. Idempotent: a second run against
    /// an up-to-date database applies nothing and returns <see cref="MigrationResult.NoOp"/>.
    /// </summary>
    /// <exception cref="MigrationDriftException">
    /// An already-applied migration no longer matches the script in this binary.
    /// </exception>
    public async Task<MigrationResult> ApplyAsync(CancellationToken ct = default)
    {
        // Hold one connection for the whole run: the advisory lock is session-scoped,
        // so it must not be handed back to the pool between statements.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await ExecuteAsync(conn, $"SELECT pg_advisory_lock({AdvisoryLockKey})", BookkeepingTimeoutSeconds, ct);
        try
        {
            await ExecuteAsync(conn, CreateHistorySql, BookkeepingTimeoutSeconds, ct);

            var applied = await ReadHistoryAsync(conn, ct);
            VerifyChecksums(applied);

            var pending = _scripts.Where(s => !applied.ContainsKey(s.Version))
                                  .OrderBy(s => s.Version)
                                  .ToList();

            if (pending.Count == 0)
            {
                _logger.LogInformation("schema up to date ({Count} migration(s) applied)", applied.Count);
                return new MigrationResult([], applied.Keys.OrderBy(v => v).ToList());
            }

            var appliedNow = new List<int>(pending.Count);
            foreach (var script in pending)
            {
                await ApplyOneAsync(conn, script, ct);
                appliedNow.Add(script.Version);
            }

            _logger.LogInformation("applied {Count} migration(s): {Versions}",
                appliedNow.Count, string.Join(", ", appliedNow));

            return new MigrationResult(appliedNow, applied.Keys.OrderBy(v => v).ToList());
        }
        finally
        {
            // Best effort: a broken connection releases the lock on close anyway.
            try
            {
                await ExecuteAsync(conn, $"SELECT pg_advisory_unlock({AdvisoryLockKey})",
                    BookkeepingTimeoutSeconds, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "advisory unlock failed (connection closing anyway)");
            }
        }
    }

    /// <summary>Versions currently recorded in <c>schema_migrations</c>, ascending.</summary>
    public async Task<IReadOnlyList<int>> AppliedVersionsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await ExecuteAsync(conn, CreateHistorySql, BookkeepingTimeoutSeconds, ct);
        var history = await ReadHistoryAsync(conn, ct);
        return history.Keys.OrderBy(v => v).ToList();
    }

    private async Task ApplyOneAsync(NpgsqlConnection conn, MigrationScript script, CancellationToken ct)
    {
        // PostgreSQL DDL is transactional: the script and its history row commit
        // together, or neither does.
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = new NpgsqlCommand(script.Sql, conn, tx) { CommandTimeout = ScriptTimeoutSeconds })
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO schema_migrations (version, name, checksum) VALUES (@v, @n, @c)", conn, tx)
            { CommandTimeout = BookkeepingTimeoutSeconds })
            {
                cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Integer) { Value = script.Version });
                cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = script.Name });
                cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = script.Checksum });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _logger.LogInformation("migration {Version} ({Name}) applied", script.Version, script.Name);
        }
        catch (Exception ex)
        {
            await SafeRollbackAsync(tx);
            throw new InvalidOperationException(
                $"migration {script.Version} ({script.Name}) failed and was rolled back: {ex.Message}", ex);
        }
    }

    private async Task SafeRollbackAsync(NpgsqlTransaction tx)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "rollback failed (transaction already aborted)");
        }
    }

    private void VerifyChecksums(IReadOnlyDictionary<int, (string Name, string Checksum)> applied)
    {
        foreach (var script in _scripts)
        {
            if (!applied.TryGetValue(script.Version, out var row)) continue;
            if (row.Checksum == script.Checksum) continue;

            throw new MigrationDriftException(
                $"migration {script.Version} ({script.Name}) was modified after it was applied: " +
                $"database recorded {row.Checksum}, binary carries {script.Checksum}. " +
                "Migrations are immutable once shipped — revert the edit and add a new numbered migration instead.");
        }

        // A version in the database that this binary does not know about means the
        // database is ahead — an older binary rolled back onto a migrated DB. That
        // is a legitimate rollback, so warn rather than block startup.
        foreach (var version in applied.Keys)
        {
            if (_scripts.Any(s => s.Version == version)) continue;
            _logger.LogWarning(
                "database has migration {Version} ({Name}) which this binary does not contain -- " +
                "running an older build against a newer schema",
                version, applied[version].Name);
        }
    }

    private static async Task<Dictionary<int, (string Name, string Checksum)>> ReadHistoryAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        var result = new Dictionary<int, (string, string)>();

        await using var cmd = new NpgsqlCommand(
            "SELECT version, name, checksum FROM schema_migrations", conn)
        { CommandTimeout = BookkeepingTimeoutSeconds };

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetInt32(0)] = (reader.GetString(1), reader.GetString(2));
        }
        return result;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, int timeout, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = timeout };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Embedded resource loading ──

    private static IReadOnlyList<MigrationScript> LoadEmbedded()
    {
        var assembly = typeof(Migrator).Assembly;
        const string prefix = "GameServer.Persistence.Migrations.";

        var scripts = new List<MigrationScript>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".sql", StringComparison.Ordinal)) continue;

            string fileName = name[prefix.Length..^".sql".Length]; // "001_init"
            int underscore = fileName.IndexOf('_');
            if (underscore <= 0 || !int.TryParse(fileName[..underscore], out int version))
            {
                throw new InvalidOperationException(
                    $"migration resource '{name}' must be named <number>_<description>.sql");
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"migration resource '{name}' could not be opened");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string sql = reader.ReadToEnd();

            scripts.Add(new MigrationScript(version, fileName, sql, ComputeChecksum(sql)));
        }

        var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"duplicate migration version {duplicate.Key}: {string.Join(", ", duplicate.Select(s => s.Name))}");
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        return scripts;
    }

    /// <summary>
    /// <c>sha256:&lt;hex&gt;</c> of the normalized script.
    ///
    /// Normalization means a comment reword or re-indent does not invalidate a
    /// migration that is already applied in production, while any change to the
    /// actual statements does.
    /// </summary>
    public static string ComputeChecksum(string sql)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(sql)));
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Drop whole-line <c>--</c> comments and collapse whitespace runs to a single
    /// space. Only *leading* comment markers are removed, so a <c>--</c> inside a
    /// string literal is never touched.
    /// </summary>
    public static string Normalize(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        foreach (var rawLine in sql.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(line);
        }

        // Collapse any remaining internal whitespace runs (tabs, aligned columns).
        var collapsed = new StringBuilder(sb.Length);
        bool inWhitespace = false;
        foreach (char c in sb.ToString())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inWhitespace) collapsed.Append(' ');
                inWhitespace = true;
            }
            else
            {
                collapsed.Append(c);
                inWhitespace = false;
            }
        }
        return collapsed.ToString().Trim();
    }
}
