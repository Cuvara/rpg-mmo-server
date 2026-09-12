using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Agones;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Registry;
using GameServer.Server;
using GameServer.Tests.Infrastructure;

namespace GameServer.Tests.Server;

/// <summary>
/// The two mode-gated lifecycle behaviours of a dungeon instance (ADR-26 decisions 6
/// and 8): it never joins the map index, and it ends its own process once the party has
/// left for good.
///
/// <para>These use a short hold TTL rather than sleeping out the real 60s dungeon window.
/// That is possible because the window now comes from <c>ServerOptions.HoldTtl</c> alone —
/// it used to be hardcoded to 60s for dungeon mode, which made this untestable.</para>
/// </summary>
public class DungeonInstanceTests
{
    private const string JwtSecret = "dungeon-test-secret";
    private const string ServerId = "gs-dungeon";
    private static readonly TimeSpan ShortHold = TimeSpan.FromMilliseconds(400);

    // ── Decision 8: registration scope ──────────────────────────────────────

    /// <summary>
    /// A dungeon host narrows its own registration, whatever the options say. The mode is
    /// the authority, not the composition root: an instance in the map index would be
    /// handed to an unrelated player by <c>FindServer</c>.
    /// </summary>
    [Fact]
    public async Task DungeonHost_RegistersTheHashOnly()
    {
        var registry = new RecordingRegistry();
        await using var h = await Harness.StartAsync(mode: "dungeon", registry: registry);

        await h.WaitForAsync(() => registry.LastScope != null);
        Assert.Equal(RegistrationScope.HashOnly, registry.LastScope);
    }

    /// <summary>The control: a map server still indexes itself, the same as ever.</summary>
    [Fact]
    public async Task MapHost_StillRegistersIntoTheMapIndex()
    {
        var registry = new RecordingRegistry();
        await using var h = await Harness.StartAsync(mode: "map", registry: registry);

        await h.WaitForAsync(() => registry.LastScope != null);
        Assert.Equal(RegistrationScope.MapIndexed, registry.LastScope);
    }

    /// <summary>
    /// A dungeon pod still publishes the hash with its address — the gateway allocates the
    /// pod and then waits for exactly this entry to learn where to send the party.
    /// </summary>
    [Fact]
    public async Task DungeonHost_StillPublishesItsAddress()
    {
        var registry = new RecordingRegistry();
        await using var h = await Harness.StartAsync(mode: "dungeon", registry: registry);

        await h.WaitForAsync(() => registry.Registered.Count > 0);
        Assert.True(registry.Registered.TryPeek(out var info));
        Assert.Equal(ServerId, info!.ServerId);
        Assert.False(string.IsNullOrWhiteSpace(info.Addr));
    }

    // ── Decision 6: the shutdown rule, as a pure function ────────────────────

    /// <summary>
    /// Every condition of the rule, including the three that are races against a live
    /// host. <c>true</c> only for a dungeon that has had a player, has none now, and has
    /// no reconnect hold still running.
    /// </summary>
    [Theory]
    // isDungeon, everHadPlayer, connections, pendingHolds, expected
    [InlineData(true, true, 0, 0, true)]    // the one case that shuts down
    [InlineData(false, true, 0, 0, false)]  // a map server is long-lived by definition
    [InlineData(true, false, 0, 0, false)]  // never had a player: a pod waiting for its party
    [InlineData(true, true, 1, 0, false)]   // somebody is still playing
    [InlineData(true, true, 0, 1, false)]   // somebody is still inside their hold window
    [InlineData(true, true, 2, 3, false)]
    [InlineData(false, false, 0, 0, false)]
    public void ShouldShutdownEmptyInstance_IsTrueOnlyForAFinishedDungeon(
        bool isDungeon, bool everHadPlayer, int connections, int pendingHolds, bool expected)
    {
        Assert.Equal(
            expected,
            GameServerHost.ShouldShutdownEmptyInstance(isDungeon, everHadPlayer, connections, pendingHolds));
    }

    [Theory]
    [InlineData("dungeon", true)]
    [InlineData("Dungeon", true)]
    [InlineData("DUNGEON", true)]
    [InlineData("map", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDungeonMode_RecognisesTheModeCaseInsensitively(string? mode, bool expected) =>
        Assert.Equal(expected, GameServerHost.IsDungeonMode(mode));

    // ── Decision 6: the rule against a live host ─────────────────────────────

    /// <summary>
    /// The last member leaves, their hold expires with no reconnect, and the pod reports
    /// <c>Shutdown</c> to the sidecar and ends its own run. A pod that outlived its party
    /// could never be allocated again — the gateway's key is the party.
    /// </summary>
    [Fact]
    public async Task LastMemberLeaving_ShutsTheInstanceDown()
    {
        var agones = new RecordingAgonesSdk();
        await using var h = await Harness.StartAsync(mode: "dungeon", agones: agones);

        (await h.JoinAsync("delver-1")).Dispose();

        await h.WaitForShutdownAsync();
        Assert.Equal(1, agones.ShutdownCalls);
    }

    /// <summary>
    /// A map server in the same shape stays up. This is what stops the test above passing
    /// against a server that simply shuts down whenever it empties.
    /// </summary>
    [Fact]
    public async Task MapServerEmptying_DoesNotShutDown()
    {
        var agones = new RecordingAgonesSdk();
        await using var h = await Harness.StartAsync(mode: "map", agones: agones);

        (await h.JoinAsync("wanderer-1")).Dispose();
        await h.WaitForAsync(() => h.Server.EntityCount == 0);

        await h.AssertStaysUpAsync();
        Assert.Equal(0, agones.ShutdownCalls);
    }

    /// <summary>
    /// A pod that has never had a player must sit still. It was allocated for a party that
    /// has not dialled in yet; shutting down at boot would race the party to its own
    /// instance.
    /// </summary>
    [Fact]
    public async Task InstanceThatNeverHadAPlayer_DoesNotShutDownAtBoot()
    {
        var agones = new RecordingAgonesSdk();
        await using var h = await Harness.StartAsync(mode: "dungeon", agones: agones);

        Assert.False(h.Server.EverHadPlayer);
        await h.AssertStaysUpAsync();
        Assert.Equal(0, agones.ShutdownCalls);

        // And it is still serving: the party can still arrive.
        using var late = await h.JoinAsync("late-arrival");
        Assert.Equal(1, h.Server.EntityCount);
    }

    /// <summary>
    /// Reconnecting inside the hold window cancels the shutdown — a dropped mobile
    /// connection must not destroy the party's instance.
    /// </summary>
    [Fact]
    public async Task ReconnectInsideTheHold_CancelsTheShutdown()
    {
        var agones = new RecordingAgonesSdk();
        await using var h = await Harness.StartAsync(
            mode: "dungeon", agones: agones, hold: TimeSpan.FromSeconds(10));

        (await h.JoinAsync("delver-flaky")).Dispose();
        await h.WaitForAsync(() => h.Server.PendingHolds == 1);

        using var again = await h.JoinAsync("delver-flaky");
        Assert.Equal(0, h.Server.PendingHolds);

        await h.AssertStaysUpAsync();
        Assert.Equal(0, agones.ShutdownCalls);
        Assert.Equal(1, h.Server.EntityCount);
    }

    /// <summary>
    /// Two members leaving together, staggered so that the FIRST hold expires while the
    /// second is still running. At that moment the instance has no connections at all and
    /// one pending hold — the only thing that may keep it alive is the hold — and it must
    /// stay up. Then the second hold expires and the pod finishes.
    ///
    /// <para>The stagger is half the hold window, so the first expiry lands a full half
    /// window before the second and the assertion is not a photo finish.</para>
    /// </summary>
    [Fact]
    public async Task FirstHoldExpiring_DoesNotShutDownWhileTheSecondIsStillHeld()
    {
        var agones = new RecordingAgonesSdk();
        // Wide, because both assertions below are about WHEN something happens and this
        // suite shares a box with the load-bearing integration tests.
        var hold = TimeSpan.FromSeconds(5);
        await using var h = await Harness.StartAsync(mode: "dungeon", agones: agones, hold: hold);

        var first = await h.JoinAsync("delver-a");
        var second = await h.JoinAsync("delver-b");

        first.Dispose();
        await h.WaitForAsync(() => h.Server.PendingHolds == 1);

        // Stagger the second departure by half a hold window. Without this the two holds
        // are armed milliseconds apart and expire milliseconds apart, and the moment under
        // test — one member out, one still held — is too short to observe.
        await Task.Delay(hold / 2);

        // Both are now gone from the server's point of view: no connections, two holds.
        second.Dispose();
        await h.WaitForAsync(() => h.Server.PendingHolds == 2);

        // The first member's hold expires and takes their entity with it...
        await h.WaitForAsync(() => h.Server.EntityCount == 1, TimeSpan.FromSeconds(60));

        // ...and here is the case under test: nobody is connected, but the second member
        // is still inside their reconnect window, so the instance must NOT end.
        Assert.Equal(1, h.Server.PendingHolds);
        Assert.Equal(0, agones.ShutdownCalls);
        Assert.False(h.Server.ShutdownStarted);
        Assert.False(h.RunTaskCompleted);

        // Once that window lapses too, the pod finishes on its own.
        await h.WaitForShutdownAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, agones.ShutdownCalls);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public required GameServerHost Server { get; init; }
        public required int Port { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task RunTask { get; init; }

        /// <summary>Whether the server's own run loop has finished.</summary>
        public bool RunTaskCompleted => RunTask.IsCompleted;

        public static async Task<Harness> StartAsync(
            string mode,
            RecordingAgonesSdk? agones = null,
            RecordingRegistry? registry = null,
            TimeSpan? hold = null)
        {
            var options = new ServerOptions
            {
                ServerAddr = ":0",
                ServerId = ServerId,
                MapId = mode == "dungeon" ? "dungeon_test" : "map_test",
                Mode = mode,
                Transport = TransportKind.Tcp,
                TickRate = 20,
                Capacity = 8,
                JwtSecret = JwtSecret,
                JoinTokenSecret = JwtSecret,
                HoldTtl = hold ?? ShortHold,
                SaveInterval = TimeSpan.FromHours(1),
                AgonesSdk = agones,
                ServerRegistry = registry,
                Registration = registry == null ? null : new RegistrationOptions
                {
                    ServerId = ServerId,
                    MapId = mode == "dungeon" ? "dungeon_test" : "map_test",
                    PublicAddr = "127.0.0.1:9999",
                },
                LoggerFactory = NullLoggerFactory.Instance,
            };

            var server = new GameServerHost(options);
            var cts = new CancellationTokenSource();
            var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

            return new Harness { Server = server, Port = port, Cts = cts, RunTask = runTask };
        }

        /// <summary>Join and wait until the server accepts the player.</summary>
        public async Task<TcpClient> JoinAsync(string userId)
        {
            var client = new TcpClient();
            for (int attempt = 0; attempt < 60; attempt++)
            {
                try { await client.ConnectAsync(IPAddress.Loopback, Port); break; }
                catch (SocketException) { await Task.Delay(100); }
            }

            var stream = client.GetStream();
            var env = WireProtocol.NewEnvelope(
                MsgType.JoinToken,
                new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
                WireEncoding.Json);
            await stream.WriteAsync(WireProtocol.Encode(env));
            await stream.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var reply = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(reply);
            var resp = WireProtocol.GetPayload<JoinTokenResponse>(reply!);
            Assert.True(resp.Ok, resp.Error);
            return client;
        }

        /// <summary>Poll until <paramref name="predicate"/> holds, or fail loudly.</summary>
        public async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
        {
            var limit = timeout ?? TimeSpan.FromSeconds(20);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < limit)
            {
                if (predicate()) return;
                await Task.Delay(25);
            }
            Assert.Fail(
                $"condition not met within {limit.TotalSeconds:F0}s " +
                $"(entities={Server.EntityCount}, holds={Server.PendingHolds}, " +
                $"runTaskCompleted={RunTask.IsCompleted})");
        }

        /// <summary>Wait for the server's own run loop to finish — the instance ended itself.</summary>
        public async Task WaitForShutdownAsync(TimeSpan? timeout = null)
        {
            var limit = timeout ?? TimeSpan.FromSeconds(20);
            var completed = await Task.WhenAny(RunTask, Task.Delay(limit));
            if (completed != RunTask)
            {
                Assert.Fail(
                    $"the instance did not shut itself down within {limit.TotalSeconds:F0}s " +
                    $"(entities={Server.EntityCount}, holds={Server.PendingHolds})");
            }
            await RunTask;
        }

        /// <summary>
        /// Assert the server is STILL running after comfortably longer than a hold window.
        /// A fixed wait, because the proposition under test is the absence of an event.
        /// </summary>
        public async Task AssertStaysUpAsync()
        {
            await Task.Delay(ShortHold + ShortHold + TimeSpan.FromMilliseconds(600));
            // ShutdownStarted, not RunTask.IsCompleted: a teardown takes a further 2s to
            // drain its clients, so waiting on the task would let a server that HAD
            // decided to stop still look alive at this point.
            Assert.False(Server.ShutdownStarted, "the server shut itself down and should not have");
            Assert.False(RunTask.IsCompleted, "the server shut itself down and should not have");
        }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Server.ShutdownAsync();
            try { await RunTask; } catch (OperationCanceledException) { /* expected */ }
            Cts.Dispose();
        }
    }

    /// <summary>Records the Agones lifecycle calls; never talks to a sidecar.</summary>
    private sealed class RecordingAgonesSdk : IAgonesSdk
    {
        private int _shutdownCalls;

        /// <summary>How many times <c>Shutdown</c> was reported.</summary>
        public int ShutdownCalls => Volatile.Read(ref _shutdownCalls);

        // False, exactly like NoopAgonesSdk: enabling it would start the health loop and
        // the allocate report, neither of which is under test here.
        public bool IsEnabled => false;

        public Task ReadyAsync() => Task.CompletedTask;

        public Task ShutdownAsync()
        {
            Interlocked.Increment(ref _shutdownCalls);
            return Task.CompletedTask;
        }

        public Task AllocateAsync() => Task.CompletedTask;
        public Task HealthAsync() => Task.CompletedTask;
        public Task<AgonesGameServerAddress?> GetAddressAsync() => Task.FromResult<AgonesGameServerAddress?>(null);
        public Task<string?> GetStateAsync() => Task.FromResult<string?>(null);
    }

    /// <summary>In-memory registry that remembers the scope it was registered under.</summary>
    private sealed class RecordingRegistry : IServerRegistry
    {
        public readonly ConcurrentQueue<ServerInfo> Registered = new();

        /// <summary>The scope of the most recent registration, or null if never registered.</summary>
        public RegistrationScope? LastScope { get; private set; }

        public Task RegisterAsync(ServerInfo info, RegistrationScope scope, CancellationToken ct)
        {
            Registered.Enqueue(info);
            LastScope = scope;
            return Task.CompletedTask;
        }

        public Task<bool> HeartbeatAsync(string serverId, CancellationToken ct) => Task.FromResult(true);
        public Task DeregisterAsync(string serverId, string mapId, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> UpdatePlayerCountAsync(string serverId, int count, CancellationToken ct)
            => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
