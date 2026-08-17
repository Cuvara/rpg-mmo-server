using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Registry;
using GameServer.Tests.Infrastructure;

namespace GameServer.Tests.Agones;

/// <summary>
/// What the server actually writes into the registry when Agones assigns its address
/// (ADR-15 decision 2, option A).
///
/// <para>The bug being pinned is not subtle once seen and is invisible until a client tries
/// to connect: under <c>portPolicy: Dynamic</c> the fleet passes <c>--addr=:9000</c> and no
/// <c>GAMESERVER_PUBLIC_ADDR</c>, so without the status read the server registers the
/// hostless <c>:9000</c>, the gateway copies it into <c>MsgEnterWorldResp.ServerAddr</c>
/// verbatim, and the client dials nothing. Every assertion below is on the value that
/// reaches <see cref="IServerRegistry.RegisterAsync"/> — the first value, not a corrected
/// one, because a wrong first write is a live window in which the gateway hands out a dead
/// address.</para>
///
/// <para>The ordering matters as much as the value and is silently reversible by anyone
/// moving two awaits: the read must land AFTER Ready (the address does not exist until the
/// pod is scheduled) and BEFORE registration (or the wrong value goes to Redis first).</para>
/// </summary>
public class AgonesAddressRegistrationTests
{
    private const string JwtSecret = "agones-address-test-secret-32byte!";
    private const string ServerId = "gs-agones-address-test";
    private const string MapId = "map_agones_address";

    /// <summary>What the fleet manifest effectively configures today: hostless and undialable.</summary>
    private const string ConfiguredAddr = ":9000";

    /// <summary>The assigned address wins, and it is the FIRST thing the registry sees.</summary>
    [Fact]
    public async Task WhenAgonesReportsAnAddress_ThatAddressIsRegistered()
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = true,
            Address = new AgonesGameServerAddress("192.168.65.3", 7691)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry);

        Assert.Equal("192.168.65.3:7691", registry.FirstRegistered!.Addr);
        Assert.All(registry.Registered, info => Assert.Equal("192.168.65.3:7691", info.Addr));
    }

    /// <summary>
    /// The read happens after Ready and before registration. Both halves are load-bearing:
    /// reading first races the scheduler, registering first publishes the wrong address.
    /// </summary>
    [Fact]
    public async Task TheAddressReadHappensAfterReadyAndBeforeRegistration()
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = true,
            Address = new AgonesGameServerAddress("192.168.65.3", 7691)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry);

        Assert.True(sdk.ReadySequence > 0, "ReadyAsync was never called");
        Assert.True(sdk.GetAddressSequence > 0, "GetAddressAsync was never called");
        Assert.True(registry.RegisterSequence > 0, "the server never registered");

        Assert.True(sdk.ReadySequence < sdk.GetAddressSequence,
            $"the address was read (#{sdk.GetAddressSequence}) before Agones Ready " +
            $"(#{sdk.ReadySequence}); the address does not exist until the pod is scheduled");
        Assert.True(sdk.GetAddressSequence < registry.RegisterSequence,
            $"registration (#{registry.RegisterSequence}) happened before the address read " +
            $"(#{sdk.GetAddressSequence}); the first registry write must already be correct");
    }

    /// <summary>
    /// A sidecar that cannot be read falls back to the configured address, byte for byte.
    /// Non-fatal on purpose: a server with an undialable address still serves the players
    /// already on it, and a crash loop serves nobody.
    /// </summary>
    [Fact]
    public async Task WhenTheAddressReadFails_TheConfiguredAddressIsRegistered()
    {
        var sdk = new RecordingAgonesSdk { IsEnabled = true, Address = null };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry);

        Assert.True(sdk.GetAddressCalls >= 1, "the server did not even try to read the address");
        Assert.Equal(ConfiguredAddr, registry.FirstRegistered!.Addr);
    }

    /// <summary>
    /// Agones off is the default and must be byte-for-byte what shipped before: the sidecar
    /// is never asked anything, and the configured address is registered unchanged. This is
    /// every local run, every test host and every docker-compose deploy.
    /// </summary>
    [Fact]
    public async Task WhenAgonesIsDisabled_NothingIsReadAndTheConfiguredAddressIsRegistered()
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = false,
            // Deliberately non-null: if the disabled path ever consulted the SDK, this
            // address would appear in the registry and the assertion below would catch it.
            Address = new AgonesGameServerAddress("192.168.65.3", 7691)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry);

        Assert.Equal(0, sdk.GetAddressCalls);
        Assert.Equal(ConfiguredAddr, registry.FirstRegistered!.Addr);
    }

    /// <summary>
    /// A non-hostless configured address is still replaced when Agones reports one. The
    /// scheduler's answer is authoritative: an operator-set GAMESERVER_PUBLIC_ADDR cannot
    /// know the port Agones picked, so a stale-looking value must not win over the live one.
    /// </summary>
    [Fact]
    public async Task AnExplicitConfiguredAddress_IsStillReplacedByTheAssignedOne()
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = true,
            Address = new AgonesGameServerAddress("192.168.65.3", 7691)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry, configuredAddr: "203.0.113.9:9200");

        Assert.Equal("192.168.65.3:7691", registry.FirstRegistered!.Addr);
    }

    // ── RegistrationService, directly ──

    /// <summary>
    /// The override is only legal before registration starts. Applying it afterwards would
    /// leave the entry already in Redis pointing at the old address until a heartbeat
    /// repaired it — a window in which the gateway hands out a dead address.
    /// </summary>
    [Fact]
    public async Task OverridePublicAddr_AfterStart_Throws()
    {
        var registry = new RecordingRegistry(new SequenceClock());
        await using var service = new RegistrationService(
            registry, NewRegistrationOptions(ConfiguredAddr), () => 0,
            NullLogger<RegistrationService>.Instance, TimeSpan.FromMinutes(5));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        Assert.Throws<InvalidOperationException>(() => service.OverridePublicAddr("10.0.0.1:1234"));
        cts.Cancel();
    }

    /// <summary>An empty override is refused rather than quietly advertised.</summary>
    [Fact]
    public void OverridePublicAddr_WithEmptyValue_Throws()
    {
        var registry = new RecordingRegistry(new SequenceClock());
        var service = new RegistrationService(
            registry, NewRegistrationOptions(ConfiguredAddr), () => 0,
            NullLogger<RegistrationService>.Instance, TimeSpan.FromMinutes(5));

        Assert.Throws<ArgumentException>(() => service.OverridePublicAddr(""));
        Assert.Throws<ArgumentException>(() => service.OverridePublicAddr("   "));
        Assert.Equal(ConfiguredAddr, service.PublicAddr);
    }

    // ── Helpers ──

    private static async Task RunUntilRegisteredAsync(
        RecordingAgonesSdk sdk, RecordingRegistry registry, string configuredAddr = ConfiguredAddr)
    {
        await using var server = new GameServerHost(NewOptions(sdk, registry, configuredAddr));
        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (runTask, _) = await TestPorts.StartServerAsync(server, runCts.Token);
        try
        {
            await registry.Registered1.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            runCts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    private static RegistrationOptions NewRegistrationOptions(string publicAddr) => new()
    {
        ServerId = ServerId,
        MapId = MapId,
        PublicAddr = publicAddr,
        Transport = "tcp",
        Capacity = 8,
        // Long TTL: the heartbeat is not what is under test, and a short one would
        // interleave writes with the ordering assertions.
        Ttl = TimeSpan.FromSeconds(60)
    };

    private static ServerOptions NewOptions(
        IAgonesSdk sdk, IServerRegistry registry, string configuredAddr) => new()
    {
        ServerAddr = ":0",
        ServerId = ServerId,
        MapId = MapId,
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
        Registration = NewRegistrationOptions(configuredAddr),
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

        /// <summary>What the fake sidecar reports. Null is every failure mode at once.</summary>
        public AgonesGameServerAddress? Address { get; init; }

        public int ReadySequence { get; private set; }
        public int GetAddressSequence { get; private set; }

        private int _getAddressCalls;
        public int GetAddressCalls => Volatile.Read(ref _getAddressCalls);

        public Task ReadyAsync()
        {
            ReadySequence = Clock.Next();
            return Task.CompletedTask;
        }

        public Task ShutdownAsync() => Task.CompletedTask;
        public Task AllocateAsync() => Task.CompletedTask;
        public Task HealthAsync() => Task.CompletedTask;

        public Task<AgonesGameServerAddress?> GetAddressAsync()
        {
            if (GetAddressSequence == 0) GetAddressSequence = Clock.Next();
            Interlocked.Increment(ref _getAddressCalls);
            return Task.FromResult(Address);
        }
    }

    /// <summary>
    /// In-memory <see cref="IServerRegistry"/> that keeps every <see cref="ServerInfo"/> it
    /// was handed. Deliberately not Redis: what is under test is in-process ordering and the
    /// value of one field, and a docker dependency would turn a structural assertion into a
    /// skippable one.
    /// </summary>
    private sealed class RecordingRegistry : IServerRegistry
    {
        private readonly SequenceClock _clock;
        public RecordingRegistry(SequenceClock clock) => _clock = clock;

        public readonly ConcurrentQueue<ServerInfo> Registered = new();

        /// <summary>Completes on the first registration.</summary>
        public readonly TaskCompletionSource Registered1 =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RegisterSequence { get; private set; }

        /// <summary>The first entry written — the one a gateway could read before any repair.</summary>
        public ServerInfo? FirstRegistered => Registered.TryPeek(out var info) ? info : null;

        public Task RegisterAsync(ServerInfo info, CancellationToken ct)
        {
            if (RegisterSequence == 0) RegisterSequence = _clock.Next();
            Registered.Enqueue(info);
            Registered1.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<bool> HeartbeatAsync(string serverId, CancellationToken ct)
            => Task.FromResult(true);

        public Task DeregisterAsync(string serverId, string mapId, CancellationToken ct)
            => Task.CompletedTask;

        public Task<bool> UpdatePlayerCountAsync(string serverId, int count, CancellationToken ct)
            => Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
