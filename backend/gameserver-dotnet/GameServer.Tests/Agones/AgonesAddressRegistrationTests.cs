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

    // ── GAMESERVER_ADVERTISE_HOST ──
    //
    // status.address is the NODE address, and a client outside the cluster network cannot
    // dial it. Measured on k3d (k3s v1.31.5, Agones 1.59.0): the status reports 172.20.0.3
    // and a connection to 172.20.0.3:7008 is refused from WSL2 and reported unreachable from
    // Windows, where the Unity client runs — while 127.0.0.1:7008, published by the k3d
    // serverlb, answers from both. So the port is right and only the host is wrong, and the
    // override replaces exactly the host.

    /// <summary>Host from the override, port from Agones. The whole point, in one assertion.</summary>
    [Fact]
    public async Task WithAnAdvertiseHost_TheHostIsOverriddenAndTheAgonesPortIsKept()
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = true,
            // Exactly what the k3d cluster reports.
            Address = new AgonesGameServerAddress("172.20.0.3", 7008)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry, advertiseHost: "127.0.0.1");

        Assert.Equal("127.0.0.1:7008", registry.FirstRegistered!.Addr);
    }

    /// <summary>
    /// Unset override keeps the pre-override behaviour exactly: the node address from the
    /// status. Pinned so adding the knob cannot have quietly changed the default.
    /// </summary>
    [Fact]
    public async Task WithoutAnAdvertiseHost_TheStatusAddressIsStillUsed()
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = true,
            Address = new AgonesGameServerAddress("172.20.0.3", 7008)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry, advertiseHost: null);

        Assert.Equal("172.20.0.3:7008", registry.FirstRegistered!.Addr);
    }

    /// <summary>
    /// With Agones off the override does nothing at all. It is Agones-specific by
    /// construction: there is no assigned port for it to pair with, and the compose path
    /// must not gain a second way to produce a wrong address.
    /// </summary>
    [Fact]
    public async Task WithAgonesDisabled_TheAdvertiseHostIsIgnored()
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = false,
            Address = new AgonesGameServerAddress("172.20.0.3", 7008)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(
            sdk, registry, configuredAddr: "203.0.113.9:9200", advertiseHost: "127.0.0.1");

        Assert.Equal(0, sdk.GetAddressCalls);
        Assert.Equal("203.0.113.9:9200", registry.FirstRegistered!.Addr);
    }

    /// <summary>
    /// A failed status read must NOT compose the override host with the configured port.
    /// That would invent an address that was never assigned to anything — a plausible-looking
    /// value pointing nowhere, which is harder to diagnose than an honestly wrong one.
    /// </summary>
    [Fact]
    public async Task WhenTheReadFails_TheAdvertiseHostIsNotComposedWithAConfiguredPort()
    {
        var sdk = new RecordingAgonesSdk { IsEnabled = true, Address = null };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(
            sdk, registry, configuredAddr: "203.0.113.9:9200", advertiseHost: "127.0.0.1");

        var registered = registry.FirstRegistered!.Addr;
        Assert.Equal("203.0.113.9:9200", registered);
        Assert.DoesNotContain("127.0.0.1", registered, StringComparison.Ordinal);
    }

    /// <summary>A hostname, not just an IP: this is what a real ingress looks like.</summary>
    [Fact]
    public async Task AnAdvertiseHostMayBeAHostname()
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = true,
            Address = new AgonesGameServerAddress("172.20.0.3", 7008)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry, advertiseHost: "gs.example.com");

        Assert.Equal("gs.example.com:7008", registry.FirstRegistered!.Addr);
    }

    /// <summary>Blank and whitespace are "unset", not a host that happens to be empty.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankAdvertiseHost_IsTreatedAsUnset(string host)
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = true,
            Address = new AgonesGameServerAddress("172.20.0.3", 7008)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry, advertiseHost: host);

        Assert.Equal("172.20.0.3:7008", registry.FirstRegistered!.Addr);
    }

    /// <summary>
    /// Someone confusing this with GAMESERVER_PUBLIC_ADDR and setting a full host:port gets
    /// the host honoured and the port ignored — never a configured port on the wire.
    /// </summary>
    [Fact]
    public async Task AnAdvertiseHostCarryingAPort_KeepsTheAgonesPort()
    {
        var sdk = new RecordingAgonesSdk
        {
            IsEnabled = true,
            Address = new AgonesGameServerAddress("172.20.0.3", 7008)
        };
        var registry = new RecordingRegistry(sdk.Clock);

        await RunUntilRegisteredAsync(sdk, registry, advertiseHost: "127.0.0.1:9999");

        Assert.Equal("127.0.0.1:7008", registry.FirstRegistered!.Addr);
    }

    // ── Host normalisation, directly ──

    /// <summary>Unset stays unset, so the caller keeps <c>status.address</c>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[")]
    [InlineData("[]")]
    public void NormalizeHostOverride_UnusableValues_AreNull(string? raw)
    {
        Assert.Null(AgonesGameServerAddress.NormalizeHostOverride(raw));
    }

    /// <summary>Plain hosts pass through, trimmed.</summary>
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("  10.0.0.5  ", "10.0.0.5")]
    [InlineData("gs.example.com", "gs.example.com")]
    // A bare IPv6 literal is a host, not host:port — checked before the colon heuristic.
    [InlineData("::1", "::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    // Bracketed IPv6, with and without a port.
    [InlineData("[::1]", "::1")]
    [InlineData("[2001:db8::1]:7000", "2001:db8::1")]
    // Confused with GAMESERVER_PUBLIC_ADDR: host honoured, port dropped.
    [InlineData("127.0.0.1:9999", "127.0.0.1")]
    [InlineData("gs.example.com:9999", "gs.example.com")]
    public void NormalizeHostOverride_ReturnsTheHostPart(string raw, string expected)
    {
        Assert.Equal(expected, AgonesGameServerAddress.NormalizeHostOverride(raw));
    }

    /// <summary>
    /// An IPv6 host is bracketed when composed, because the gateway hands the string to
    /// clients verbatim and a bare <c>::1:7008</c> does not parse as an endpoint.
    /// </summary>
    [Fact]
    public void AnIpv6Host_IsBracketedInTheAdvertisedString()
    {
        var addr = new AgonesGameServerAddress("172.20.0.3", 7008).WithHost("2001:db8::1");
        Assert.Equal("[2001:db8::1]:7008", addr.ToString());
    }

    /// <summary>The port survives a host replacement. It is the half only Agones can supply.</summary>
    [Fact]
    public void WithHost_ReplacesOnlyTheHost()
    {
        var original = new AgonesGameServerAddress("172.20.0.3", 7008);
        var moved = original.WithHost("127.0.0.1");

        Assert.Equal(7008, moved.Port);
        Assert.Equal("127.0.0.1", moved.Address);
        Assert.Equal(7008, original.Port);
        Assert.Equal("172.20.0.3", original.Address);
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
        RecordingAgonesSdk sdk, RecordingRegistry registry,
        string configuredAddr = ConfiguredAddr, string? advertiseHost = null)
    {
        await using var server = new GameServerHost(
            NewOptions(sdk, registry, configuredAddr, advertiseHost));
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
        IAgonesSdk sdk, IServerRegistry registry, string configuredAddr,
        string? advertiseHost = null) => new()
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
        AdvertiseHost = advertiseHost,
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
