using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Registry;
using GameServer.Tests.Infrastructure;

namespace GameServer.Tests.Agones;

/// <summary>
/// The lifecycle contract from ADR-14: Ready is reported once the listener is bound and
/// <b>before</b> the server publishes itself into the registry, Allocate fires once when the
/// first player lands, Shutdown is reported after the registry entry is removed — and with
/// Agones disabled none of it happens at all, including the health loop.
///
/// <para>The ordering is the part worth pinning with a test. It is invisible in a log and
/// silently reversible by anyone reordering two awaits, and getting it wrong advertises an
/// address that Agones may be about to kill — which is precisely the disagreement between
/// the two writers (Agones over pod lifecycle, this server over its Redis entry) that
/// ADR-1's one-writer-per-datum rule exists to prevent.</para>
/// </summary>
public class AgonesLifecycleTests
{
    private const string JwtSecret = "agones-lifecycle-test-secret-32b!!";
    private const string ServerId = "gs-agones-test";

    /// <summary>Ready must be reported before the registry ever sees this server.</summary>
    [Fact]
    public async Task WhenEnabled_ReadyIsReportedBeforeRegistration()
    {
        var sdk = new RecordingAgonesSdk { IsEnabled = true };
        var registry = new RecordingRegistry(sdk.Clock);
        await using var server = new GameServerHost(NewOptions(sdk, registry));
        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (runTask, _) = await TestPorts.StartServerAsync(server, runCts.Token);
        try
        {
            await registry.Registered.Task.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(sdk.ReadySequence > 0, "ReadyAsync was never called");
            Assert.True(registry.RegisterSequence > 0, "the server never registered");
            Assert.True(sdk.ReadySequence < registry.RegisterSequence,
                $"registration (#{registry.RegisterSequence}) happened before Agones Ready " +
                $"(#{sdk.ReadySequence}); ADR-14 requires Ready first");
        }
        finally
        {
            runCts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    /// <summary>
    /// The reverse order on the way down: leave the registry first so the gateway stops
    /// handing clients to a server that is going away, and only then tell Agones.
    /// </summary>
    [Fact]
    public async Task WhenEnabled_DeregistrationHappensBeforeAgonesShutdown()
    {
        var sdk = new RecordingAgonesSdk { IsEnabled = true };
        var registry = new RecordingRegistry(sdk.Clock);
        var server = new GameServerHost(NewOptions(sdk, registry));
        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (runTask, _) = await TestPorts.StartServerAsync(server, runCts.Token);
        await registry.Registered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await server.ShutdownAsync();
        runCts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        await server.DisposeAsync();

        Assert.True(registry.DeregisterSequence > 0, "the server never deregistered");
        Assert.True(sdk.ShutdownSequence > 0, "Agones Shutdown was never reported");
        Assert.True(registry.DeregisterSequence < sdk.ShutdownSequence,
            $"Agones Shutdown (#{sdk.ShutdownSequence}) was reported before the registry " +
            $"entry was removed (#{registry.DeregisterSequence})");
    }

    /// <summary>The health loop must actually ping while the server runs.</summary>
    [Fact]
    public async Task WhenEnabled_HealthLoopPings()
    {
        var sdk = new RecordingAgonesSdk { IsEnabled = true };
        await using var server = new GameServerHost(NewOptions(sdk, registry: null));
        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (runTask, _) = await TestPorts.StartServerAsync(server, runCts.Token);
        try
        {
            await sdk.FirstHealth.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(sdk.HealthCalls >= 1);
        }
        finally
        {
            runCts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    /// <summary>
    /// Disabled is the default and must stay inert: no Ready, no Allocate, and above all no
    /// health loop — a loop against the no-op SDK logs "health loop started" and then
    /// reports nothing to anybody, which reads in a log exactly like a working liveness
    /// contract (ADR-14 decision 4). Registration still happens, at the same point.
    /// </summary>
    [Fact]
    public async Task WhenDisabled_NoReadyNoAllocateNoHealthLoop_ButStillRegisters()
    {
        var sdk = new RecordingAgonesSdk { IsEnabled = false };
        var registry = new RecordingRegistry(sdk.Clock);
        await using var server = new GameServerHost(NewOptions(sdk, registry));
        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (runTask, port) = await TestPorts.StartServerAsync(server, runCts.Token);
        try
        {
            await registry.Registered.Task.WaitAsync(TimeSpan.FromSeconds(20));

            // A player joins: the Allocate hook lives on this path, so this is what proves
            // the disabled build does not reach for the sidecar mid-game either.
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await using var stream = client.GetStream();
            await JoinAsync(stream, "disabled-user", runCts.Token);

            // Several health-loop intervals (2s each) — long enough that a running loop
            // would have pinged repeatedly.
            await Task.Delay(2500);

            Assert.Equal(0, sdk.HealthCalls);
            Assert.Equal(0, sdk.AllocateCalls);
            Assert.True(registry.RegisterSequence > 0, "the server did not register");
        }
        finally
        {
            runCts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }

        // ReadyAsync/ShutdownAsync are still invoked on the interface — they are no-ops on
        // NoopAgonesSdk, so nothing reaches a network — but nothing may reach a sidecar.
        Assert.False(new NoopAgonesSdk().IsEnabled);
    }

    /// <summary>
    /// Allocate fires on the first player and exactly once after that. Agones has no
    /// un-allocate, so a second call would be noise at best and a fight with the fleet's
    /// own state machine at worst.
    /// </summary>
    [Fact]
    public async Task WhenEnabled_AllocateIsReportedOnceOnTheFirstPlayer()
    {
        var sdk = new RecordingAgonesSdk { IsEnabled = true };
        await using var server = new GameServerHost(NewOptions(sdk, registry: null));
        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (runTask, port) = await TestPorts.StartServerAsync(server, runCts.Token);
        try
        {
            Assert.Equal(0, sdk.AllocateCalls);

            using var first = new TcpClient();
            await ConnectWithRetryAsync(first, port);
            await using var firstStream = first.GetStream();
            await JoinAsync(firstStream, "alloc-user-1", runCts.Token);

            await sdk.FirstAllocate.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, sdk.AllocateCalls);

            using var second = new TcpClient();
            await ConnectWithRetryAsync(second, port);
            await using var secondStream = second.GetStream();
            await JoinAsync(secondStream, "alloc-user-2", runCts.Token);
            await Task.Delay(500);

            Assert.Equal(1, sdk.AllocateCalls);
        }
        finally
        {
            runCts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    // ── Helpers ──

    private static ServerOptions NewOptions(IAgonesSdk sdk, IServerRegistry? registry) => new()
    {
        ServerAddr = ":0",
        ServerId = ServerId,
        MapId = "map_agones",
        Mode = "map",
        TickRate = 20,
        Capacity = 8,
        JwtSecret = JwtSecret,
        JoinTokenSecret = JwtSecret,
        SaveInterval = TimeSpan.FromSeconds(30),
        HoldTtl = TimeSpan.FromSeconds(30),
        PlayerStore = new MemoryPlayerStore(),
        AgonesSdk = sdk,
        ServerRegistry = registry,
        Registration = registry == null ? null : new RegistrationOptions
        {
            ServerId = ServerId,
            MapId = "map_agones",
            PublicAddr = "203.0.113.9:9200",
            Transport = "tcp",
            Capacity = 8,
            // Long TTL: the heartbeat is not what is under test, and a short one would
            // interleave writes with the ordering assertions.
            Ttl = TimeSpan.FromSeconds(60)
        },
        LoggerFactory = NullLoggerFactory.Instance
    };

    /// <summary>
    /// Monotonic call counter shared between the fake SDK and the fake registry, so
    /// "before" and "after" are one total order rather than two wall clocks compared.
    /// </summary>
    private sealed class SequenceClock
    {
        private int _next;
        public int Next() => Interlocked.Increment(ref _next);
    }

    private sealed class RecordingAgonesSdk : IAgonesSdk
    {
        public SequenceClock Clock { get; } = new();

        public bool IsEnabled { get; init; }

        public int ReadySequence { get; private set; }
        public int ShutdownSequence { get; private set; }

        private int _healthCalls;
        private int _allocateCalls;

        public int HealthCalls => Volatile.Read(ref _healthCalls);
        public int AllocateCalls => Volatile.Read(ref _allocateCalls);

        public readonly TaskCompletionSource FirstHealth =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FirstAllocate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadyAsync()
        {
            ReadySequence = Clock.Next();
            return Task.CompletedTask;
        }

        public Task ShutdownAsync()
        {
            ShutdownSequence = Clock.Next();
            return Task.CompletedTask;
        }

        public Task AllocateAsync()
        {
            Interlocked.Increment(ref _allocateCalls);
            FirstAllocate.TrySetResult();
            return Task.CompletedTask;
        }

        public Task HealthAsync()
        {
            Interlocked.Increment(ref _healthCalls);
            FirstHealth.TrySetResult();
            return Task.CompletedTask;
        }

        /// <summary>
        /// No assigned address, so the host keeps the configured one and the ordering
        /// assertions in this class stay about Ready/register alone. The address read is
        /// covered in <see cref="AgonesAddressRegistrationTests"/>.
        /// </summary>
        public Task<AgonesGameServerAddress?> GetAddressAsync() =>
            Task.FromResult<AgonesGameServerAddress?>(null);
    }

    /// <summary>
    /// In-memory <see cref="IServerRegistry"/> that records when it was written. Deliberately
    /// not Redis: the ordering under test is in-process, and a docker dependency would turn a
    /// structural assertion into a skippable one.
    /// </summary>
    private sealed class RecordingRegistry : IServerRegistry
    {
        private readonly SequenceClock _clock;
        public RecordingRegistry(SequenceClock clock) => _clock = clock;

        public readonly ConcurrentQueue<string> Calls = new();
        public readonly TaskCompletionSource Registered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RegisterSequence { get; private set; }
        public int DeregisterSequence { get; private set; }

        public Task RegisterAsync(ServerInfo info, CancellationToken ct)
        {
            if (RegisterSequence == 0) RegisterSequence = _clock.Next();
            Calls.Enqueue("register");
            Registered.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<bool> HeartbeatAsync(string serverId, CancellationToken ct)
            => Task.FromResult(true);

        public Task DeregisterAsync(string serverId, string mapId, CancellationToken ct)
        {
            if (DeregisterSequence == 0) DeregisterSequence = _clock.Next();
            Calls.Enqueue("deregister");
            return Task.CompletedTask;
        }

        public Task<bool> UpdatePlayerCountAsync(string serverId, int count, CancellationToken ct)
            => Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task JoinAsync(NetworkStream stream, string userId, CancellationToken ct)
    {
        var env = WireProtocol.NewEnvelope(
            MsgType.JoinToken,
            new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
            WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(env), ct);
        await stream.FlushAsync(ct);

        var respEnv = await WireProtocol.DecodeAsync(stream, ct);
        Assert.NotNull(respEnv);
        var resp = WireProtocol.GetPayload<JoinTokenResponse>(respEnv!);
        Assert.True(resp.Ok, resp.Error);
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100); // listener not up yet
            }
        }
        throw new TimeoutException($"game server never started listening on :{port}");
    }
}
