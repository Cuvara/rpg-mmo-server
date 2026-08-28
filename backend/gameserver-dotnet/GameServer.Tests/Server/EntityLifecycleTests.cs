using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Server;
using GameServer.Tests.Infrastructure;

namespace GameServer.Tests.Server;

/// <summary>
/// Entities must not outlive the players they belong to.
///
/// <para>Nothing in the server ever removes a player entity except the reconnect-hold
/// task, so a hold that is never scheduled leaks that entity for the life of the
/// process — and every leaked entity is still scanned by AOI and diffed on every tick,
/// turning a bounded O(n²) per-tick cost into an unbounded one.</para>
///
/// <para>These use a very short hold TTL rather than sleeping out the real 30s.</para>
/// </summary>
public class EntityLifecycleTests
{
    private const string JwtSecret = "lifecycle-test-secret";
    private const string ServerId = "gs-lifecycle";
    private static readonly TimeSpan ShortHold = TimeSpan.FromMilliseconds(300);

    /// <summary>A clean join → disconnect leaves nothing behind once the hold expires.</summary>
    [Fact]
    public async Task CleanDisconnect_RemovesEntityAndZeroesTheGauge()
    {
        using var metrics = new GameMetrics("map_lifecycle", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        string userId = "user-clean";
        using (var client = await h.JoinAsync(userId))
        {
            Assert.Equal(1, h.Server.EntityCount);
            Assert.Equal(1, metrics.PlayersOnline);
        } // socket closes here

        await h.WaitForAsync(() => h.Server.EntityCount == 0);

        Assert.Equal(0, h.Server.EntityCount);
        Assert.Equal(0, metrics.PlayersOnline);
    }

    /// <summary>
    /// The regression. A client that vanishes between the entity being attached and the
    /// join response being written aborts <c>HandleConnectionAsync</c> mid-way. Before
    /// the fix that path never reached the hold scheduler, so the entity stayed in the
    /// world forever — matching the benchmark's "400 entities, 0 players, indefinitely".
    ///
    /// <para>Note the gauge that stayed correct: <c>players_online</c> is incremented
    /// after the write, so an abort before it leaves the player count right and only the
    /// entity count wrong. That asymmetry is what identified this path.</para>
    /// </summary>
    [Fact]
    public async Task ClientVanishingDuringJoin_StillReleasesItsEntity()
    {
        using var metrics = new GameMetrics("map_lifecycle", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        // Several attempts, not one: whether the server gets far enough to attach the
        // entity before the RST lands is a timing race, and a single attempt that lost
        // that race would assert "0 entities" against a server that never created one —
        // passing for the wrong reason. The peak assertion below makes the test state
        // plainly that at least one entity really was attached.
        const int vanishers = 8;
        for (int i = 0; i < vanishers; i++)
        {
            var client = new TcpClient();
            await ConnectWithRetryAsync(client, h.Port);
            await SendJoinAsync(client.GetStream(), $"user-vanisher-{i}", JwtSecret);
            client.Client.Close(0); // RST, not a graceful FIN
            client.Dispose();
        }

        // At least one join got far enough to attach an entity...
        await h.WaitForAsync(() => h.Server.EntityCount > 0);

        // ...and every one of them must be released again. Before the fix these were
        // never handed to the hold scheduler at all, so they stayed forever.
        await h.WaitForAsync(() => h.Server.EntityCount == 0);
        Assert.Equal(0, h.Server.EntityCount);

        // An aborted join must not decrement a counter it never incremented.
        Assert.Equal(0, metrics.PlayersOnline);
    }

    /// <summary>
    /// An aborted join must not corrupt the online count for players who really are
    /// connected — <c>players_online</c> is an independent counter, not derived from the
    /// connection table, so an unbalanced decrement is invisible until it goes wrong.
    /// </summary>
    [Fact]
    public async Task AbortedJoin_DoesNotDisturbAnotherPlayersOnlineCount()
    {
        using var metrics = new GameMetrics("map_lifecycle", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        using var stayer = await h.JoinAsync("user-stayer");
        // Wait, do not assert immediately: the server records PlayerJoined() AFTER
        // writing JoinTokenResp (deliberately, so an aborted write cannot increment a
        // counter the finally block would then have to unwind). So a client that has
        // read the response is racing the increment, with no happens-before between
        // them. Asserting straight away passed only because the write used to be
        // slower than the test; it failed on a fast CI runner once the JSON path
        // stopped round-tripping its own payload through JsonDocument.Parse.
        await h.WaitForAsync(() => metrics.PlayersOnline == 1);

        for (int i = 0; i < 5; i++)
        {
            var doomed = new TcpClient();
            await ConnectWithRetryAsync(doomed, h.Port);
            await SendJoinAsync(doomed.GetStream(), $"user-doomed-{i}", JwtSecret);
            doomed.Client.Close(0);
            doomed.Dispose();
        }

        // Only the aborted joins' entities go away; the stayer keeps both entity and count.
        await h.WaitForAsync(() => h.Server.EntityCount == 1);
        Assert.Equal(1, h.Server.EntityCount);
        Assert.Equal(1, metrics.PlayersOnline);
    }

    /// <summary>
    /// A cohort that joins and leaves together must return the world to empty — this is
    /// the shape the load test drives, and the one that used to pin the gauge at its peak.
    /// </summary>
    [Fact]
    public async Task CohortJoinAndLeave_ReturnsEntityCountToZero()
    {
        const int cohort = 25;
        using var metrics = new GameMetrics("map_lifecycle", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics, capacity: cohort + 5);

        var clients = new List<TcpClient>();
        for (int i = 0; i < cohort; i++)
        {
            clients.Add(await h.JoinAsync($"cohort-{i}"));
        }
        Assert.Equal(cohort, h.Server.EntityCount);
        Assert.Equal(cohort, metrics.PlayersOnline);

        foreach (var c in clients) c.Dispose();

        await h.WaitForAsync(() => h.Server.EntityCount == 0);
        Assert.Equal(0, h.Server.EntityCount);
        Assert.Equal(0, metrics.PlayersOnline);
    }

    /// <summary>
    /// Reconnecting inside the hold window keeps the entity: the fix must not turn the
    /// hold into an unconditional delete.
    /// </summary>
    [Fact]
    public async Task ReconnectWithinHold_KeepsTheEntity()
    {
        using var metrics = new GameMetrics("map_lifecycle", $"test.{Guid.NewGuid():N}");
        // Long enough that the reconnect lands comfortably inside the window.
        await using var h = await Harness.StartAsync(metrics, hold: TimeSpan.FromSeconds(10));

        string userId = "user-reconnector";
        (await h.JoinAsync(userId)).Dispose();

        // Wait for the server to actually observe the drop before reconnecting.
        // Reconnecting first would race the old connection's teardown and briefly
        // double-count the player — a test artefact, not a server bug.
        await h.WaitForAsync(() => h.Server.PendingHolds == 1);
        Assert.Equal(0, metrics.PlayersOnline);

        using var again = await h.JoinAsync(userId);
        Assert.Equal(1, h.Server.EntityCount);
        Assert.Equal(1, metrics.PlayersOnline);

        // And it still leaves properly afterwards.
        again.Dispose();
        await h.WaitForAsync(() => h.Server.EntityCount == 0, TimeSpan.FromSeconds(20));
        Assert.Equal(0, h.Server.EntityCount);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public required GameServerHost Server { get; init; }
        public required int Port { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task RunTask { get; init; }

        public static async Task<Harness> StartAsync(
            GameMetrics metrics, int capacity = 100, TimeSpan? hold = null)
        {
            var options = new ServerOptions
            {
                ServerAddr = ":0",
                ServerId = ServerId,
                MapId = "map_lifecycle",
                Mode = "map",
                Transport = TransportKind.Tcp,
                TickRate = 20,
                Capacity = capacity,
                JwtSecret = JwtSecret,
                JoinTokenSecret = JwtSecret,
                HoldTtl = hold ?? ShortHold,
                SaveInterval = TimeSpan.FromHours(1), // the periodic sweep is not under test
                Metrics = metrics,
                LoggerFactory = NullLoggerFactory.Instance
            };

            var server = new GameServerHost(options);
            var cts = new CancellationTokenSource();

            // Binds ":0" and reports the port it got — no guess to lose, and the wait for
            // the listener is the bind itself rather than a probe-and-retry loop.
            var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

            return new Harness { Server = server, Port = port, Cts = cts, RunTask = runTask };
        }

        /// <summary>Join and wait until the server reports the player as online.</summary>
        public async Task<TcpClient> JoinAsync(string userId)
        {
            var client = new TcpClient();
            await ConnectWithRetryAsync(client, Port);
            var stream = client.GetStream();
            await SendJoinAsync(stream, userId, JwtSecret);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var env = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(env);
            var resp = WireProtocol.GetPayload<JoinTokenResponse>(env!);
            Assert.True(resp.Ok, resp.Error);
            return client;
        }

        /// <summary>Poll until <paramref name="predicate"/> holds, or fail loudly.</summary>
        public async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
        {
            var limit = timeout ?? TimeSpan.FromSeconds(15);
            var deadline = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate()) return;
                await Task.Delay(25);
            }
            Assert.Fail(
                $"condition not met within {limit.TotalSeconds:F0}s " +
                $"(entities={Server.EntityCount}) — the entity was never released");
        }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Server.ShutdownAsync();
            try { await RunTask; } catch (OperationCanceledException) { /* expected */ }
            Cts.Dispose();
        }
    }

    /// <summary>
    /// The #229 regression. A client rejoins while its old TCP connection is still alive
    /// (a mobile blip: the socket died client-side, the server's heartbeat has not noticed
    /// yet). <c>ConnectionManager.Add</c> replaces and closes the old connection, whose
    /// handler teardown then runs — and before the identity check, that teardown removed
    /// whatever connection it found under the user id: the NEW one. The player was kicked
    /// milliseconds after a successful rejoin and the online gauge under-counted.
    /// </summary>
    [Fact]
    public async Task FastRejoin_WhileOldConnectionStillOpen_KeepsTheNewConnectionAlive()
    {
        using var metrics = new GameMetrics("map_lifecycle", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics, hold: TimeSpan.FromSeconds(10));

        string userId = "user-fast-rejoin";

        // First connection stays OPEN — this is the half-dead socket the server has not
        // noticed losing. Disposing it here would turn the test into the ordinary
        // disconnect-then-reconnect case, which was never broken.
        var stale = await h.JoinAsync(userId);

        // Rejoin under the same user while the stale connection is registered.
        using var fresh = await h.JoinAsync(userId);

        // The stale handler's teardown has now run (Add closed its transport). Give it a
        // moment to do its damage, then require the world settled on exactly one player —
        // the fresh one — with no hold scheduled: a hold parks the entity of a player who
        // is present.
        await h.WaitForAsync(() =>
            metrics.PlayersOnline == 1 && h.Server.PendingHolds == 0);
        Assert.Equal(1, h.Server.EntityCount);

        // The proof the fresh connection survived: the server still answers on it. Under
        // the bug it was closed by the stale teardown, so this read hits EOF instead.
        var stream = fresh.GetStream();
        var ping = WireProtocol.NewEnvelope(
            MsgType.Ping, new PingMessage { Timestamp = 42 }, WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(ping));
        await stream.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env); // any frame will do — snapshots and pongs both prove liveness

        stale.Dispose();
    }

    internal static async Task SendJoinAsync(Stream stream, string userId, string secret)
    {
        var env = WireProtocol.NewEnvelope(
            MsgType.JoinToken,
            new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, secret) },
            WireEncoding.Json);
        byte[] frame = WireProtocol.Encode(env);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    internal static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException($"server never started listening on :{port}");
    }

}
