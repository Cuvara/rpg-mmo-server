using System.Data;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Persistence;

/// <summary>
/// PostgreSQL-backed <see cref="IPlayerStore"/> for the game-state database.
///
/// Port of the Go <c>shared/storage/pgstore</c> package (player.go / migrate.go).
/// Semantics preserved 1:1:
/// <list type="bullet">
///   <item>Saves are upserts keyed by <c>user_id</c>, so the async batch saver can
///         call <see cref="SavePlayerAsync"/> unconditionally with no existence check.</item>
///   <item>Loading an unknown user returns <c>null</c> (the Go store returned
///         <c>storage.ErrNotFound</c>; the C# interface signals absence with null,
///         matching <see cref="MemoryPlayerStore"/>).</item>
///   <item><see cref="MigrateAsync"/> is idempotent and safe on every boot.</item>
/// </list>
///
/// NativeAOT notes: this type deliberately uses raw <see cref="NpgsqlCommand"/>s with
/// explicitly typed parameters and positional <see cref="NpgsqlDataReader"/> accessors.
/// No ORM, no reflection-based mapping, no dynamic type inference — everything the
/// AOT compiler needs is statically resolvable. Npgsql itself is trim/AOT-annotated.
/// </summary>
public sealed class PostgresPlayerStore : IPlayerStore, IAsyncDisposable
{
    /// <summary>Default per-command timeout, in seconds.</summary>
    public const int DefaultCommandTimeoutSeconds = 5;

    private const string UpsertPlayerSql = """
        INSERT INTO player_states (user_id, map_id, x, y, hp, max_hp, updated_at)
        VALUES (@user_id, @map_id, @x, @y, @hp, @max_hp, now())
        ON CONFLICT (user_id) DO UPDATE SET
            map_id     = EXCLUDED.map_id,
            x          = EXCLUDED.x,
            y          = EXCLUDED.y,
            hp         = EXCLUDED.hp,
            max_hp     = EXCLUDED.max_hp,
            updated_at = now()
        """;

    private const string LoadPlayerSql = """
        SELECT user_id, map_id, x, y, hp, max_hp
        FROM player_states
        WHERE user_id = @user_id
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;

    private PostgresPlayerStore(NpgsqlDataSource dataSource, int commandTimeoutSeconds)
    {
        _dataSource = dataSource;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>
    /// Build a pooled store for <paramref name="dsn"/> and verify the server is
    /// reachable (round-trips a <c>SELECT 1</c>). Throws when the database cannot be
    /// reached — callers are expected to fail fast rather than silently degrade to
    /// a memory store.
    /// </summary>
    /// <param name="dsn">
    /// Either a libpq URL (<c>postgres://user:pw@host:5432/db?sslmode=disable</c>) or a
    /// native Npgsql keyword connection string.
    /// </param>
    /// <param name="ct">Cancellation token for the connectivity probe.</param>
    /// <param name="commandTimeoutSeconds">Per-command timeout applied to every query.</param>
    public static async Task<PostgresPlayerStore> ConnectAsync(
        string dsn,
        CancellationToken ct = default,
        int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(dsn))
            throw new ArgumentException("empty DSN", nameof(dsn));

        string connectionString = BuildConnectionString(dsn, commandTimeoutSeconds);
        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();

        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn) { CommandTimeout = commandTimeoutSeconds };
            await cmd.ExecuteScalarAsync(ct);
        }
        catch (Exception ex)
        {
            await dataSource.DisposeAsync();
            throw new InvalidOperationException(
                $"postgres connect failed ({MaskDsn(dsn)}): {ex.Message}", ex);
        }

        return new PostgresPlayerStore(dataSource, commandTimeoutSeconds);
    }

    /// <summary>
    /// Apply every pending numbered migration (see <see cref="Migrator"/>).
    /// Idempotent — safe on every process start; concurrent servers are serialised
    /// by an advisory lock.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="logger">Optional destination for per-migration progress lines.</param>
    /// <exception cref="MigrationDriftException">
    /// An already-applied migration was edited after it shipped.
    /// </exception>
    public Task<MigrationResult> MigrateAsync(CancellationToken ct = default, ILogger? logger = null)
        => new Migrator(_dataSource, logger ?? NullLogger.Instance).ApplyAsync(ct);

    /// <summary>Verify the connection is usable (health / readiness probes).</summary>
    public async Task PingAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT 1");
        cmd.CommandTimeout = _commandTimeoutSeconds;
        await cmd.ExecuteScalarAsync(ct);
    }

    /// <inheritdoc />
    public async Task SavePlayerAsync(PlayerState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrEmpty(state.UserId))
            throw new ArgumentException("empty user id", nameof(state));

        await using var cmd = _dataSource.CreateCommand(UpsertPlayerSql);
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.Parameters.Add(new NpgsqlParameter("user_id", NpgsqlDbType.Text) { Value = state.UserId });
        cmd.Parameters.Add(new NpgsqlParameter("map_id", NpgsqlDbType.Text) { Value = state.MapId ?? string.Empty });
        cmd.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.Real) { Value = state.X });
        cmd.Parameters.Add(new NpgsqlParameter("y", NpgsqlDbType.Real) { Value = state.Y });
        cmd.Parameters.Add(new NpgsqlParameter("hp", NpgsqlDbType.Integer) { Value = state.Hp });
        cmd.Parameters.Add(new NpgsqlParameter("max_hp", NpgsqlDbType.Integer) { Value = state.MaxHp });

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"pgstore save player {state.UserId}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>Returns <c>null</c> when no row exists, matching <see cref="MemoryPlayerStore"/>.</remarks>
    public async Task<PlayerState?> LoadPlayerAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        await using var cmd = _dataSource.CreateCommand(LoadPlayerSql);
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.Parameters.Add(new NpgsqlParameter("user_id", NpgsqlDbType.Text) { Value = userId });

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct)) return null; // not found

            return new PlayerState(
                UserId: reader.GetString(0),
                X: reader.GetFloat(2),
                Y: reader.GetFloat(3),
                Hp: reader.GetInt32(4),
                MaxHp: reader.GetInt32(5),
                MapId: reader.GetString(1));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"pgstore load player {userId}: {ex.Message}", ex);
        }
    }

    /// <summary>Remove a player row. Deleting a missing player is a no-op.</summary>
    public async Task DeletePlayerAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("DELETE FROM player_states WHERE user_id = @user_id");
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.Parameters.Add(new NpgsqlParameter("user_id", NpgsqlDbType.Text) { Value = userId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Translate a libpq-style DSN into an Npgsql keyword connection string.
    /// Keyword connection strings are passed through (with the timeout applied).
    /// </summary>
    public static string BuildConnectionString(string dsn, int commandTimeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        NpgsqlConnectionStringBuilder builder;
        bool connectTimeoutSet;

        if (LooksLikeUrl(dsn))
        {
            var uri = new Uri(dsn);
            builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
            };

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                builder.Username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length == 2) builder.Password = Uri.UnescapeDataString(parts[1]);
            }

            connectTimeoutSet = ApplyQuery(builder, uri.Query);
        }
        else
        {
            builder = new NpgsqlConnectionStringBuilder(dsn);
            connectTimeoutSet = builder.ContainsKey("Timeout");
        }

        if (string.IsNullOrEmpty(builder.Database))
            throw new ArgumentException($"DSN has no database name ({MaskDsn(dsn)})", nameof(dsn));

        builder.CommandTimeout = commandTimeoutSeconds;
        // Keep the connect timeout bounded so boot fails fast on an unreachable DB.
        if (!connectTimeoutSet) builder.Timeout = 10;

        return builder.ConnectionString;
    }

    /// <summary>
    /// Redact the password from a DSN so it can be safely logged.
    /// Handles both URL (<c>user:pw@host</c>) and keyword (<c>Password=pw</c>) forms.
    /// </summary>
    public static string MaskDsn(string? dsn)
    {
        if (string.IsNullOrEmpty(dsn)) return "";

        if (LooksLikeUrl(dsn))
        {
            int schemeEnd = dsn.IndexOf("://", StringComparison.Ordinal) + 3;
            int at = dsn.IndexOf('@', schemeEnd);
            if (at < 0) return dsn;

            string userInfo = dsn[schemeEnd..at];
            int colon = userInfo.IndexOf(':');
            if (colon < 0) return dsn;

            return string.Concat(dsn.AsSpan(0, schemeEnd + colon + 1), "****", dsn.AsSpan(at));
        }

        var sb = new StringBuilder();
        foreach (var segment in dsn.Split(';'))
        {
            if (segment.Length == 0) continue;
            int eq = segment.IndexOf('=');
            string key = (eq < 0 ? segment : segment[..eq]).Trim();
            bool secret = key.Equals("password", StringComparison.OrdinalIgnoreCase)
                          || key.Equals("pwd", StringComparison.OrdinalIgnoreCase);
            if (sb.Length > 0) sb.Append(';');
            sb.Append(secret ? $"{key}=****" : segment);
        }
        return sb.ToString();
    }

    private static bool LooksLikeUrl(string dsn)
        => dsn.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
           || dsn.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Apply libpq query-string options. Returns true if connect_timeout was set.</summary>
    private static bool ApplyQuery(NpgsqlConnectionStringBuilder builder, string query)
    {
        if (string.IsNullOrEmpty(query)) return false;
        bool connectTimeoutSet = false;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(kv[0]).Trim();
            string value = kv.Length == 2 ? Uri.UnescapeDataString(kv[1]).Trim() : "";
            if (key.Length == 0) continue;

            switch (key.ToLowerInvariant())
            {
                case "sslmode":
                    builder.SslMode = ParseSslMode(value);
                    break;
                case "connect_timeout":
                    if (int.TryParse(value, out int t)) { builder.Timeout = t; connectTimeoutSet = true; }
                    break;
                case "application_name":
                    builder.ApplicationName = value;
                    break;
                case "dbname":
                    builder.Database = value;
                    break;
                default:
                    // Unknown libpq keys are ignored rather than fatal — the DSN may be
                    // shared with psql/pgx tooling that understands more options than Npgsql.
                    break;
            }
        }

        return connectTimeoutSet;
    }

    private static SslMode ParseSslMode(string value) => value.ToLowerInvariant() switch
    {
        "disable" => SslMode.Disable,
        "allow" => SslMode.Allow,
        "prefer" => SslMode.Prefer,
        "require" => SslMode.Require,
        "verify-ca" or "verify_ca" => SslMode.VerifyCA,
        "verify-full" or "verify_full" => SslMode.VerifyFull,
        _ => throw new ArgumentException($"unknown sslmode '{value}'")
    };

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
