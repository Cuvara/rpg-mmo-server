using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace GameServer.Tests.Persistence;

/// <summary>
/// A throwaway PostgreSQL container used by the persistence tests.
///
/// Tests run against a REAL postgres — no fakes, no in-memory shims — because the
/// store's behaviour (upsert semantics, real/int type mapping, DDL idempotency)
/// only means something against the actual engine.
///
/// The container binds a random free loopback port so it never collides with the
/// developer's live game-state database on :5433. When docker is unavailable the
/// factory returns null and the tests skip cleanly.
/// </summary>
internal sealed class EphemeralPostgres : IAsyncDisposable
{
    private const string Image = "postgres:16.4-alpine";
    private const string User = "gstest";
    private const string Password = "gstest";
    private const string Database = "gamestate_test";

    private readonly string _docker;

    /// <summary>Container name (also usable with <c>docker exec</c>).</summary>
    public string ContainerName { get; }

    /// <summary>Host port the container's 5432 is published on.</summary>
    public int Port { get; }

    /// <summary>libpq URL DSN pointing at the container.</summary>
    public string Dsn => $"postgres://{User}:{Password}@127.0.0.1:{Port}/{Database}?sslmode=disable";

    private EphemeralPostgres(string docker, string name, int port)
    {
        _docker = docker;
        ContainerName = name;
        Port = port;
    }

    /// <summary>
    /// Start a container and wait until it accepts connections.
    /// Returns null when docker is not usable on this machine (test should skip).
    /// </summary>
    public static async Task<EphemeralPostgres?> TryStartAsync(CancellationToken ct = default)
    {
        string? docker = FindDocker();
        if (docker is null) return null;

        int port = FreeTcpPort();
        string name = $"rpg-gs-test-pg-{Guid.NewGuid():N}"[..30];

        var run = Exec(docker,
            $"run -d --name {name} " +
            $"-e POSTGRES_USER={User} -e POSTGRES_PASSWORD={Password} -e POSTGRES_DB={Database} " +
            $"-p 127.0.0.1:{port}:5432 {Image}",
            TimeSpan.FromMinutes(5));

        if (run.ExitCode != 0)
        {
            Console.WriteLine($"[EphemeralPostgres] docker run failed: {run.StdErr.Trim()}");
            return null;
        }

        var pg = new EphemeralPostgres(docker, name, port);
        if (!await pg.WaitReadyAsync(TimeSpan.FromSeconds(90), ct))
        {
            Console.WriteLine("[EphemeralPostgres] container never became ready");
            await pg.DisposeAsync();
            return null;
        }
        return pg;
    }

    /// <summary>Force-remove the container. Never throws.</summary>
    public void Kill() => Exec(_docker, $"rm -f {ContainerName}", TimeSpan.FromSeconds(60));

    private async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        int consecutiveSuccesses = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var probe = Exec(_docker, $"exec {ContainerName} pg_isready -U {User} -d {Database}",
                TimeSpan.FromSeconds(15));

            // The postgres entrypoint boots a temporary local-only server to run the
            // init scripts, then restarts it. pg_isready answers "up" during that
            // window, so a single successful handshake is not proof of readiness —
            // require two in a row before handing the DSN to a test.
            if (probe.ExitCode == 0 && await CanQueryAsync(ct))
            {
                if (++consecutiveSuccesses >= 2) return true;
            }
            else
            {
                consecutiveSuccesses = 0;
            }

            await Task.Delay(750, ct);
        }
        return false;
    }

    /// <summary>Full protocol-level probe: connect from the host and run a query.</summary>
    private async Task<bool> CanQueryAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(
                $"Host=127.0.0.1;Port={Port};Database={Database};Username={User};Password={Password};" +
                "SSL Mode=Disable;Timeout=5;Pooling=false");
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn) { CommandTimeout = 5 };
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindDocker()
    {
        foreach (var candidate in new[] { "docker", "docker.exe" })
        {
            try
            {
                if (Exec(candidate, "version --format {{.Server.Version}}", TimeSpan.FromSeconds(30)).ExitCode == 0)
                    return candidate;
            }
            catch
            {
                // Binary not on PATH — try the next candidate.
            }
        }
        return null;
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Run a process and capture its output. Never throws: a launch failure is
    /// reported as a non-zero exit code so callers degrade into "docker unavailable"
    /// (test skips) instead of failing with an environment error.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) Exec(string file, string args, TimeSpan timeout)
    {
        // Spawning processes can fail transiently under memory pressure
        // (Windows: "The paging file is too small for this operation to complete").
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return ExecOnce(file, args, timeout);
            }
            catch (Exception ex) when (attempt < 2)
            {
                Console.WriteLine($"[EphemeralPostgres] '{file} {args}' failed to start ({ex.Message}); retrying");
                Thread.Sleep(2000);
            }
            catch (Exception ex)
            {
                return (-127, "", ex.Message);
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) ExecOnce(string file, string args, TimeSpan timeout)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();

        // Reads are started but never blocked on — see TestDocker.ExecOnce for why a
        // synchronous ReadToEnd() here makes the timeout below unreachable.
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (-1, Drain(stdout), "timed out");
        }
        return (proc.ExitCode, Drain(stdout), Drain(stderr));
    }

    private static string Drain(Task<string> read)
    {
        try { return read.Wait(TimeSpan.FromSeconds(10)) ? read.Result : ""; }
        catch { return ""; }
    }

    public ValueTask DisposeAsync()
    {
        Kill();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Shared container for the whole persistence test collection — starting one
/// postgres per test would dominate the suite runtime.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    internal EphemeralPostgres? Container { get; private set; }

    /// <summary>True when a real postgres is available for the tests to use.</summary>
    public bool Available => Container is not null;

    /// <summary>DSN of the shared container (empty when unavailable).</summary>
    public string Dsn => Container?.Dsn ?? "";

    /// <summary>
    /// Connect a store to the shared container with retries. The Windows
    /// docker port proxy occasionally resets a fresh connection under full-suite
    /// load; production keeps fail-fast semantics, tests absorb the transient.
    /// </summary>
    public async Task<GameServer.Persistence.PostgresPlayerStore> ConnectStoreAsync(int attempts = 4)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                return await GameServer.Persistence.PostgresPlayerStore.ConnectAsync(Dsn);
            }
            catch when (i < attempts)
            {
                await Task.Delay(400 * i);
            }
        }
    }

    public async Task InitializeAsync() => Container = await EphemeralPostgres.TryStartAsync();

    public async Task DisposeAsync()
    {
        if (Container is not null) await Container.DisposeAsync();
    }

    /// <summary>
    /// Skip the calling test — as a REAL xUnit skip — when docker is missing.
    ///
    /// This used to be a soft skip: the test returned early and xUnit recorded it as
    /// PASSED, so a docker-less run reported exactly the same totals as a full run
    /// and absence of coverage was indistinguishable from coverage. The only honest
    /// signal was per-test duration. Now the runner reports Skipped, so the summary
    /// cannot lie. CI (ubuntu-latest) always has docker, so a skip there is a real
    /// signal rather than a silent pass.
    /// </summary>
    public void SkipUnlessAvailable(string testName)
    {
        Skip.IfNot(Available, $"{testName}: docker unavailable, no postgres to test against");
    }
}

/// <summary>Collection definition binding the shared postgres container.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
