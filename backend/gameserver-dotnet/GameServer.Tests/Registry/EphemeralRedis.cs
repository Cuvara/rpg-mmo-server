using GameServer.Tests.Infrastructure;
using StackExchange.Redis;

namespace GameServer.Tests.Registry;

/// <summary>
/// A throwaway Redis container for the registry tests.
///
/// The registry tests run against a REAL Redis: the whole point of the code under
/// test is that the entry survives, expires and reappears exactly as Redis makes
/// it, and a fake would prove nothing about TTL semantics or about surviving a
/// server restart. The container binds a random free loopback port so it never
/// touches the developer's live Redis on :6379.
/// </summary>
internal sealed class EphemeralRedis : IAsyncDisposable
{
    private const string Image = "redis:7-alpine";

    private readonly string _docker;

    /// <summary>Container name (also usable with <c>docker exec</c>).</summary>
    public string ContainerName { get; }

    /// <summary>Host port the container's 6379 is published on.</summary>
    public int Port { get; }

    /// <summary>Address in the Go <c>host:port</c> style the server config uses.</summary>
    public string Addr => $"127.0.0.1:{Port}";

    private EphemeralRedis(string docker, string name, int port)
    {
        _docker = docker;
        ContainerName = name;
        Port = port;
    }

    /// <summary>
    /// Start a container and wait until it answers PING.
    /// Returns null when docker is not usable here (the test then skips).
    /// </summary>
    public static async Task<EphemeralRedis?> TryStartAsync(CancellationToken ct = default)
    {
        string? docker = TestDocker.Find();
        if (docker is null) return null;

        int port = TestDocker.FreeTcpPort();
        string name = $"rpg-gs-test-redis-{Guid.NewGuid():N}"[..30];

        var run = TestDocker.Exec(docker,
            $"run -d --name {name} -p 127.0.0.1:{port}:6379 {Image}",
            TimeSpan.FromMinutes(5));

        if (run.ExitCode != 0)
        {
            Console.WriteLine($"[EphemeralRedis] docker run failed: {run.StdErr.Trim()}");
            return null;
        }

        var redis = new EphemeralRedis(docker, name, port);
        if (!await redis.WaitReadyAsync(TimeSpan.FromSeconds(60), ct))
        {
            Console.WriteLine("[EphemeralRedis] container never became ready");
            await redis.DisposeAsync();
            return null;
        }
        return redis;
    }

    private async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var options = ConfigurationOptions.Parse(Addr);
                options.AbortOnConnectFail = false;
                options.ConnectTimeout = 2000;
                await using var mux = await ConnectionMultiplexer.ConnectAsync(options);
                if (mux.IsConnected && (await mux.GetDatabase().PingAsync()) >= TimeSpan.Zero)
                {
                    return true;
                }
            }
            catch
            {
                // Not up yet.
            }
            await Task.Delay(250, ct);
        }
        return false;
    }

    /// <summary>Stop the container without removing it — simulates a Redis outage.</summary>
    public void Stop() => TestDocker.Exec(_docker, $"stop -t 0 {ContainerName}", TimeSpan.FromSeconds(60));

    /// <summary>Start a stopped container again, with its data gone (no volume).</summary>
    public void Start() => TestDocker.Exec(_docker, $"start {ContainerName}", TimeSpan.FromSeconds(60));

    /// <summary>Run a redis-cli command inside the container and return stdout.</summary>
    public string Cli(string args)
    {
        var res = TestDocker.Exec(_docker, $"exec {ContainerName} redis-cli {args}", TimeSpan.FromSeconds(30));
        return res.StdOut.Trim();
    }

    /// <summary>Force-remove the container. Never throws.</summary>
    public void Kill() => TestDocker.Exec(_docker, $"rm -f {ContainerName}", TimeSpan.FromSeconds(60));

    public ValueTask DisposeAsync()
    {
        Kill();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Shared Redis container for the registry test collection — one container per
/// test would dominate the suite runtime.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    internal EphemeralRedis? Container { get; private set; }

    /// <summary>True when a real Redis is available for the tests to use.</summary>
    public bool Available => Container is not null;

    /// <summary>Address of the shared container (empty when unavailable).</summary>
    public string Addr => Container?.Addr ?? "";

    public async Task InitializeAsync() => Container = await EphemeralRedis.TryStartAsync();

    public async Task DisposeAsync()
    {
        if (Container is not null) await Container.DisposeAsync();
    }

    /// <summary>
    /// Skip the calling test — as a REAL xUnit skip — when docker is unavailable.
    ///
    /// This must never be a silent early `return`: a soft skip is recorded as
    /// Passed, so a run with no docker reports the same totals as a full run and
    /// absence of coverage becomes indistinguishable from coverage. CI always has
    /// docker, so a skip there is a genuine signal.
    /// </summary>
    public void SkipUnlessAvailable(string testName)
    {
        Skip.IfNot(Available, $"{testName}: docker unavailable, no redis to test against");
    }

    /// <summary>Wipe every key, simulating a fresh/flushed Redis.</summary>
    public void Flush() => Container?.Cli("FLUSHALL");
}

/// <summary>Collection definition binding the shared redis container.</summary>
[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
