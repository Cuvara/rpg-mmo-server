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
    /// The outcome of trying to start the container, kept apart from the container itself
    /// because <b>the two ways this can fail are not the same kind of event</b> and used to
    /// be collapsed into one nullable return.
    /// <para>
    /// <see cref="DockerUsable"/> false means there is no docker daemon answering here — a
    /// genuinely absent dependency, and a legitimate skip. <see cref="DockerUsable"/> true
    /// with a null <see cref="Container"/> means docker answered, <c>docker run</c> was
    /// attempted, and the container still never became usable: an <b>infrastructure
    /// failure</b>. Reporting the second as "docker unavailable" is what let 11 registry
    /// tests vanish from a green run under load (see issue #175).
    /// </para>
    /// </summary>
    internal readonly record struct StartOutcome(
        EphemeralRedis? Container, bool DockerUsable, string? Failure);

    /// <summary>
    /// Start a container and wait until it answers PING.
    /// <para>
    /// Never throws; the caller decides whether the outcome is a skip or a failure. See
    /// <see cref="StartOutcome"/> — the distinction is the point of the return type.
    /// </para>
    /// </summary>
    public static async Task<EphemeralRedis?> TryStartAsync(CancellationToken ct = default) =>
        (await StartAsync(ct)).Container;

    internal static async Task<StartOutcome> StartAsync(CancellationToken ct = default)
    {
        // TestDocker.Find() runs `docker version --format {{.Server.Version}}`, which needs
        // a LIVE daemon, not merely the binary on PATH. So null here covers both "docker is
        // not installed" and "the daemon is not running" — the two states that are honestly
        // an absent dependency on a dev box — and non-null means the daemon answered, which
        // makes everything after this point our problem rather than the environment's.
        string? docker = TestDocker.Find();
        if (docker is null)
        {
            return new StartOutcome(null, DockerUsable: false,
                Failure: "no docker daemon answered `docker version`");
        }

        string name = $"rpg-gs-test-redis-{Guid.NewGuid():N}"[..30];

        // A FIXED published port, unlike EphemeralPostgres, and deliberately so: this
        // fixture exposes Stop()/Start() to simulate a Redis outage, and a container
        // published on ":0" gets a DIFFERENT host port every time it starts. The address
        // handed to RegistrationService is captured once and has to survive the restart —
        // publishing ephemerally made RedisOutage_DoesNotKillTheService fail on the
        // reconnect, every run, because the service was reconnecting to a port that had
        // moved. So the port is leased instead: held until the instant before `docker run`
        // binds it. That is a narrower window than the old release-immediately helper, not
        // a closed one — see TestPorts.Lease. So the run is retried on a fresh lease when
        // the port was taken in that gap. That retry is not cosmetic any more: with the
        // skip/fail split below, a lost port race would otherwise turn a transient
        // collision into a hard suite failure.
        int port = 0;
        (int ExitCode, string StdOut, string StdErr) run = (-1, "", "never attempted");

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using (var lease = new TestPorts.Lease()) { port = lease.Port; }

            run = TestDocker.Exec(docker,
                $"run -d --name {name} -p 127.0.0.1:{port}:6379 {Image}",
                TimeSpan.FromMinutes(5));

            if (run.ExitCode == 0) break;

            bool portTaken = run.StdErr.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || run.StdErr.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase);
            if (!portTaken || attempt == 3) break;

            Console.WriteLine(
                $"[EphemeralRedis] port {port} was taken between the lease and `docker run`; retrying");
            TestDocker.Exec(docker, $"rm -f {name}", TimeSpan.FromSeconds(60));
        }

        if (run.ExitCode != 0)
        {
            string why = $"`docker run` failed (exit {run.ExitCode}): {run.StdErr.Trim()}";
            Console.WriteLine($"[EphemeralRedis] {why}");
            return new StartOutcome(null, DockerUsable: true, Failure: why);
        }

        var redis = new EphemeralRedis(docker, name, port);
        if (!await redis.WaitReadyAsync(TimeSpan.FromSeconds(60), ct))
        {
            // Capture the evidence BEFORE disposing. The old code disposed first and reported
            // "the container started but never answered PING", which named neither the
            // container nor what it had been doing — a failing run could be caught with full
            // detailed logging and still not be diagnosable afterwards (#201).
            string state = TestDocker.Exec(docker, $"inspect {name} --format {{{{.State.Status}}}}|{{{{.State.ExitCode}}}}|{{{{.State.OOMKilled}}}}",
                TimeSpan.FromSeconds(30)).StdOut.Trim();
            string logs = TestDocker.Exec(docker, $"logs --tail 20 {name}", TimeSpan.FromSeconds(30)).StdOut.Trim();

            string why =
                $"{redis.LastReadyFailure ?? "the container started but never became ready"}. " +
                $"container={name} state={(string.IsNullOrWhiteSpace(state) ? "<none>" : state)}";

            Console.WriteLine($"[EphemeralRedis] {why}");
            if (!string.IsNullOrWhiteSpace(logs))
            {
                Console.WriteLine($"[EphemeralRedis] docker logs {name}:\n{logs}");
            }

            await redis.DisposeAsync();
            return new StartOutcome(null, DockerUsable: true, Failure: why);
        }
        return new StartOutcome(redis, DockerUsable: true, Failure: null);
    }

    private async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        // Stopwatch, not DateTime.UtcNow. This host's CLOCK_REALTIME runs 10-17% fast and
        // has been observed stepping BACKWARDS (#153, and #175 pins a step arithmetically),
        // so a wall-clock deadline is not the budget it claims to be: at 17% fast a "60s"
        // budget expires after ~51s of real time, and a forward step ends it outright.
        // That turned a slow container start under load into "docker unavailable" and
        // silently removed 11 registry tests from the run. Stopwatch is monotonic, so the
        // 60s below is 60 real seconds regardless of what the clock does.
        // Two phases, because they answer different questions and the old single-phase
        // loop could not tell them apart (#201).
        //
        // Phase 1 asks "is the socket accepting?" with a bare TCP connect. Measured on this
        // host, a container reaches that point in ~0.8-1.4s even under a full parallel suite
        // at load 9.66 — no slower than idle.
        //
        // Phase 2 asks "does Redis answer?" with ONE multiplexer, polled. The old loop built
        // and disposed a ConnectionMultiplexer every 250ms, up to 240 times. That object is
        // heavyweight and starts its own threads, and with AbortOnConnectFail=false it
        // returns BEFORE it has connected, leaving IsConnected false while it retries in the
        // background. Under a saturated thread pool that retry can be starved for the whole
        // budget while the socket has been open since the first second — which is what a
        // 60s "never answered PING" against a ~1s-ready container looks like.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout && !await SocketAcceptsAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
        }

        if (elapsed.Elapsed >= timeout)
        {
            LastReadyFailure = $"the published port {Addr} never accepted a TCP connection " +
                               $"within {timeout.TotalSeconds:F0}s";
            return false;
        }

        TimeSpan socketOpenAt = elapsed.Elapsed;

        // Phase 2 speaks RESP on a bare socket rather than using StackExchange.Redis.
        //
        // Measured, not assumed. With a ConnectionMultiplexer here the block of 11 registry
        // tests failed ~2 runs in 10 with: port accepted a connection after ~1s, Redis's own
        // log saying "Ready to accept connections tcp", and PING never landing inside 60s.
        // The container was healthy every time. A multiplexer starts background threads and
        // with AbortOnConnectFail=false returns before connecting, so under a saturated
        // thread pool its connect completion can be starved for the whole budget while the
        // socket has been open since the first second (#201).
        //
        // A readiness probe must not depend on the thing it is trying to schedule around.
        // PING/+PONG over the socket is four bytes out and seven back, with no pool, no
        // background thread and nothing to starve.
        while (elapsed.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            if (await PingOverRawSocketAsync(ct))
            {
                return true;
            }

            await Task.Delay(250, ct);
        }

        LastReadyFailure =
            $"the published port {Addr} accepted a connection after " +
            $"{socketOpenAt.TotalMilliseconds:F0}ms, but never answered RESP PING within " +
            $"{timeout.TotalSeconds:F0}s";
        return false;
    }

    /// <summary>
    /// PING over a bare socket, spelled in RESP. Returns true on <c>+PONG</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not a Redis client. The point is to ask the question with the smallest
    /// machinery that can ask it, so a positive answer means Redis is serving and a negative
    /// one cannot be an artefact of the client library's scheduling.
    /// </remarks>
    private async Task<bool> PingOverRawSocketAsync(CancellationToken ct)
    {
        var parts = Addr.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
        {
            return false;
        }

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));

            await client.ConnectAsync(parts[0], port, attempt.Token);
            await using var stream = client.GetStream();

            byte[] ping = System.Text.Encoding.ASCII.GetBytes("PING\r\n");
            await stream.WriteAsync(ping, attempt.Token);
            await stream.FlushAsync(attempt.Token);

            var buffer = new byte[16];
            int read = await stream.ReadAsync(buffer, attempt.Token);
            return read >= 7 &&
                   System.Text.Encoding.ASCII.GetString(buffer, 0, read)
                         .StartsWith("+PONG", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Why the last <see cref="WaitReadyAsync"/> gave up. Null while it has not.</summary>
    internal string? LastReadyFailure { get; private set; }

    /// <summary>
    /// A bare TCP connect to the published port. Deliberately not a Redis client: this asks
    /// only whether docker has finished publishing the port, and answering it with a full
    /// client conflates "the port is not up" with "the client could not get scheduled".
    /// </summary>
    private async Task<bool> SocketAcceptsAsync(CancellationToken ct)
    {
        var parts = Addr.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
        {
            return false;
        }

        try
        {
            using var socket = new System.Net.Sockets.TcpClient();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(parts[0], port, attempt.Token);
            return socket.Connected;
        }
        catch
        {
            return false;
        }
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

    /// <summary>True when a docker daemon answered — so a missing container is our fault.</summary>
    private bool _dockerUsable;

    /// <summary>Why the container is missing, when it is.</summary>
    private string? _failure;

    /// <summary>True when a real Redis is available for the tests to use.</summary>
    public bool Available => Container is not null;

    /// <summary>Address of the shared container (empty when unavailable).</summary>
    public string Addr => Container?.Addr ?? "";

    public async Task InitializeAsync()
    {
        var outcome = await EphemeralRedis.StartAsync();
        Container = outcome.Container;
        _dockerUsable = outcome.DockerUsable;
        _failure = outcome.Failure;
    }

    public async Task DisposeAsync()
    {
        if (Container is not null) await Container.DisposeAsync();
    }

    /// <summary>
    /// Skip the calling test — as a REAL xUnit skip — when docker is genuinely absent, and
    /// FAIL it when docker is there but the container is not.
    ///
    /// <para>
    /// A skip must never be a silent early `return`: a soft skip is recorded as Passed, so a
    /// run with no docker reports the same totals as a full run and absence of coverage
    /// becomes indistinguishable from coverage. CI always has docker, so a skip there is a
    /// genuine signal.
    /// </para>
    /// <para>
    /// <b>The same argument is why not every missing container is a skip.</b> This used to
    /// collapse every cause into one message reading "docker unavailable", including the
    /// case where docker was plainly available — Postgres-gated tests ran in the same run —
    /// and only the readiness probe had timed out under load. The effect was that these 11
    /// registry tests disappeared from a green run, reporting the one cause that had not
    /// happened, under exactly the load that the timing tests they sit beside exist to
    /// survive (issue #175). So the two states are now separated:
    /// </para>
    /// <list type="bullet">
    /// <item><b>No docker daemon</b> — an absent dependency. A legitimate skip: a dev box
    /// without docker cannot run these and should not pretend to.</item>
    /// <item><b>Docker answered, container never became usable</b> — an infrastructure
    /// failure, and it FAILS. There is nothing absent to excuse it, the coverage was
    /// expected to run, and a skip here is the run telling itself a comfortable lie.</item>
    /// </list>
    /// </summary>
    public void SkipUnlessAvailable(string testName)
    {
        if (Available) return;

        // Docker genuinely absent (binary missing, or daemon not running): a real skip.
        Skip.IfNot(_dockerUsable,
            $"{testName}: no docker daemon on this machine, so there is no redis to test against");

        // Docker answered but we could not get a container. Not an absent dependency —
        // an environment we broke. Fail, loudly, with the cause that actually happened.
        throw new InvalidOperationException(
            $"{testName}: docker IS available, but the redis test container never became usable " +
            $"— {_failure}. This is an infrastructure failure, not a missing dependency, so it " +
            "fails rather than skipping: skipping here reported green over 11 unrun registry " +
            "tests and named a cause that did not happen (issue #175).");
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
