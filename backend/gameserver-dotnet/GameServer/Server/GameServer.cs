using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Agones;

using GameServer.Events;
using GameServer.Input;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Registry;
using GameServer.Snapshot;
using GameServer.World;

namespace GameServer.Server;

/// <summary>Server configuration options.</summary>
public class ServerOptions
{
    public string ServerAddr { get; set; } = ":9000";
    /// <summary>
    /// Realtime transport the listener speaks: "tcp" (default) or "kcp". The same
    /// strings the Go side uses, because this value travels to clients through the
    /// registry and <c>EnterWorldResponse.Transport</c>.
    /// </summary>
    public string Transport { get; set; } = TransportKind.Tcp;
    /// <summary>
    /// Pre-shared AES-256 key for KCP (<c>TRANSPORT_KEY</c>). Empty means plaintext.
    /// Ignored for TCP, where TLS or the cluster network is the answer instead.
    /// </summary>
    public string TransportKey { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string MapId { get; set; } = "map_01";
    public string Mode { get; set; } = "map"; // "map" or "dungeon"
    /// <summary>
    /// Legacy single-rate setting. When <see cref="SimulationRates"/> is null every group
    /// runs at this rate, which is the pre-multi-rate server exactly.
    /// </summary>
    public int TickRate { get; set; } = GameConstants.DefaultTickRate;

    /// <summary>
    /// The multi-rate configuration. Null means "derive a uniform configuration from
    /// <see cref="TickRate"/>", which is what every existing test does and why none of them
    /// changed behaviour.
    /// </summary>
    public SimulationRates? SimulationRates { get; set; }

    public int Capacity { get; set; } = 100;
    /// <summary>
    /// HS256 secret (or comma-separated rotation list) for the Nakama-issued
    /// client auth token. The game server itself never sees that token; this is
    /// only the fallback for <see cref="JoinTokenSecret"/>.
    /// </summary>
    public string JwtSecret { get; set; } = "";

    /// <summary>
    /// HS256 secret (or comma-separated rotation list, "current,previous") that the
    /// gateway signs join tokens with — <c>JOIN_TOKEN_SECRET</c>. REQUIRED: the
    /// server refuses to start when this is empty.
    /// </summary>
    public string JoinTokenSecret { get; set; } = "";
    /// <summary>Play area for this map. Movement is clamped into these bounds.</summary>
    public MapBounds MapBounds { get; set; } = MapBounds.Default;

    /// <summary>
    /// Delta snapshots between full keyframes. 0 or less disables delta encoding
    /// (every snapshot is a full keyframe).
    /// </summary>
    public int KeyframeInterval { get; set; } = GameConstants.DefaultKeyframeInterval;

    public TimeSpan HoldTtl { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SaveInterval { get; set; } = TimeSpan.FromSeconds(30);
    public IPlayerStore? PlayerStore { get; set; }
    public IAgonesSdk? AgonesSdk { get; set; }

    /// <summary>
    /// Host to advertise instead of the Agones node address — <c>GAMESERVER_ADVERTISE_HOST</c> /
    /// <c>--advertise-host</c>. <b>Host only, no port</b>, and read <b>only</b> when Agones is
    /// enabled and its status read succeeded.
    ///
    /// <para>It exists because <c>status.address</c> is the node's address on the cluster
    /// network and a client outside that network cannot dial it: on k3d the status says
    /// <c>172.20.0.3</c> and the address that answers is <c>127.0.0.1</c>, published by the
    /// serverlb. Agones knows the port and cannot know the host; this supplies the host and
    /// never the port.</para>
    ///
    /// <para>Not to be confused with <c>GAMESERVER_PUBLIC_ADDR</c>
    /// (<see cref="RegistrationOptions.PublicAddr"/>), which is a full <c>host:port</c> and
    /// is what gets advertised when Agones is <b>off</b>. Exactly one of the two applies in
    /// any given deployment.</para>
    /// </summary>
    public string? AdvertiseHost { get; set; }

    /// <summary>
    /// Hold the registry entry back until this pod's Agones GameServer reaches
    /// <c>Allocated</c>, instead of publishing it right after Ready —
    /// <c>GAMESERVER_REGISTER_ON_ALLOCATED</c> / <c>--register-on-allocated</c>.
    ///
    /// <para><b>Default false, which is the behaviour that shipped.</b> A fleet that has not
    /// been migrated must not change behaviour because this code exists; the flag is the
    /// migration.</para>
    ///
    /// <para><b>Inert unless Agones is enabled.</b> Outside a cluster — docker-compose, a
    /// local run, every test — there is no allocation to wait for, so gating on one would
    /// mean never registering at all. <see cref="IAgonesSdk.IsEnabled"/> is checked before
    /// this flag is honoured.</para>
    ///
    /// <para>What it buys: a <c>Ready</c> but unallocated replica holds no registry entry, so
    /// it is genuinely spare and <c>FindServer</c> cannot hand live players a pod Agones is
    /// free to delete on the next scale-down (ADR-18 decision 4, mechanism 2). It narrows
    /// ADR-14 decision 3, which put registration after Ready — Ready is still a precondition,
    /// it is simply no longer sufficient.</para>
    /// </summary>
    public bool RegisterOnAllocated { get; set; }

    public IEventStream? EventStream { get; set; }
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Optional metric instrument set. When null the server runs uninstrumented.</summary>
    public GameMetrics? Metrics { get; set; }

    /// <summary>
    /// Registry this server publishes itself into. When null the server does not
    /// self-register — the in-memory / single-process default, and what every test
    /// that does not care about the registry gets.
    /// </summary>
    public IServerRegistry? ServerRegistry { get; set; }

    /// <summary>
    /// Builds the per-tick simulation phase this host runs, or null for a host that
    /// simulates nothing of its own and only relays whatever entities exist.
    ///
    /// <para>A factory rather than an instance because the phase needs the world, which
    /// the host owns and creates. A factory rather than a flag because the core must not
    /// name any particular gameplay: the composition root in <c>Program.cs</c> decides
    /// what the game is, and this type stays content-agnostic. See
    /// <see cref="ISimulationPhase"/>.</para>
    ///
    /// <para>Default null. Tests and custom scenarios opt in explicitly.</para>
    /// </summary>
    public Func<EcsWorld, ILoggerFactory, Action<SimulationGroup, long, long>?, ISimulationPhase>?
        SimulationPhaseFactory { get; set; }

    /// <summary>
    /// Supplies the number published as <c>enemies_alive</c> on the status endpoint.
    ///
    /// <para>Set by the composition root, which knows what the game is. Null means the
    /// server publishes 0 — a content-free build has nothing to count, which is the
    /// correct answer rather than a missing feature.</para>
    ///
    /// <para>This replaced an <c>ISimulationPhase.TrackedEntityCount</c> member. That put
    /// the number behind a content-agnostic interface whose only consumer immediately
    /// renamed it after the content, and forced every future phase to summarise itself as
    /// a single unlabelled int — which stops composing as soon as there are two.</para>
    /// </summary>
    public Func<EcsWorld, int>? StatusEntityCount { get; set; }

    /// <summary>Nakama HTTP API base URL (e.g. <c>http://rpg-nakama:7350</c>). Null disables economy integration.</summary>
    public string? NakamaUrl { get; set; }

    /// <summary>Nakama <c>runtime.http_key</c> for server-to-server RPC calls.</summary>
    public string NakamaHttpKey { get; set; } = "";

    /// <summary>
    /// How this server describes itself in the registry. Required when
    /// <see cref="ServerRegistry"/> is set; ignored otherwise.
    /// </summary>
    public RegistrationOptions? Registration { get; set; }
}

/// <summary>
/// Main game server. Accepts TCP connections, validates JWT tokens,
/// manages player entities with reconnect hold, runs the tick loop,
/// and periodically saves state.
/// Port of Go server/server.go.
/// </summary>
public sealed class GameServerHost : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly EcsWorld _world;
    private readonly ConnectionManager _connections;
    private readonly TickLoop _tickLoop;
    private readonly AsyncSaver _saver;
    private readonly InputHandler _inputHandler;
    private readonly IPlayerStore _playerStore;
    private readonly IAgonesSdk _agonesSdk;
    private readonly EventPublisher? _publisher;
    private readonly Nakama.NakamaClient? _nakamaClient;
    private readonly GameMetrics? _metrics;
    private readonly ILogger _logger;
    /// <summary>Keyring join tokens are verified against (JOIN_TOKEN_SECRET).</summary>
    private readonly JwtKeyring _joinKeys;
    /// <summary>Tracks consumed JTI values to prevent join token replay.</summary>
    private readonly JtiTracker _jtiTracker = new();
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// The resolved simulation rates. Held past the constructor because the join response
    /// has to tell the client the CRITICAL rate to predict at (#93); without it the client
    /// falls back to its own hardcoded guess and desyncs silently.
    /// </summary>
    private readonly SimulationRates _rates;

    private readonly RegistrationService? _registration;

    /// <summary>
    /// The background wait for <c>Allocated</c> and the registration that follows it, when
    /// <see cref="ServerOptions.RegisterOnAllocated"/> is armed. Null on every other path.
    /// Held so shutdown can observe it rather than leave it dangling.
    /// </summary>
    private Task? _gatedRegistration;

    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Completed with the bound address once the listener is up, so a caller can learn the
    /// port an ephemeral (":0") bind actually got. Faulted if the bind throws and cancelled
    /// if the server shuts down before binding, so a waiter can never hang.
    /// </summary>
    private readonly TaskCompletionSource<string> _listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The address the listener is bound to ("127.0.0.1:54321"), available as soon as the
    /// bind succeeds. Callers that listen on ":0" — tests, and any deployment that lets the
    /// kernel pick — must await this instead of choosing a port up front: picking a port,
    /// releasing it, and rebinding it later is a TOCTOU race another process can win.
    /// </summary>
    public Task<string> ListeningAddressAsync => _listening.Task;

    /// <summary>0 until the first ShutdownAsync caller wins the race, 1 afterwards.</summary>
    private int _shutdownStarted;

    /// <summary>0 until the first player join has reported Allocate to Agones, 1 afterwards.</summary>
    private int _allocateReported;

    /// <summary>Completed when the single real teardown finishes (or faults).</summary>
    private readonly TaskCompletionSource _shutdownComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Entity hold timers for reconnect (user ID -> hold CTS).</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _holds = new();

    /// <summary>
    /// Entities currently in the world — the exact value the <c>gameserver_entities</c>
    /// gauge publishes. Exposed so lifecycle tests assert the number an operator sees
    /// rather than a parallel count that could agree while the gauge lies.
    /// </summary>
    public int EntityCount => _world.EntityCount;

    /// <summary>Reconnect holds currently pending. Diagnostics and tests.</summary>
    public int PendingHolds => _holds.Count;

    /// <summary>Current simulation tick number.</summary>
    public ulong CurrentTick => _tickLoop.CurrentTick;

    /// <summary>Number of enemies currently alive.</summary>
    /// <summary>
    /// The number the status endpoint publishes as <c>enemies_alive</c>.
    ///
    /// <para>The core does not compute it and does not know what it counts. The
    /// composition root supplies <see cref="ServerOptions.StatusEntityCount"/> — it is
    /// the one place that is allowed to know what the game is — and the property name is
    /// kept because the Unity DOTS sample polls <c>/status</c> and reads that field. The
    /// name is a wire contract; what fills it is not.</para>
    /// </summary>
    public int EnemiesAlive => _options.StatusEntityCount?.Invoke(_world) ?? 0;

    public GameServerHost(ServerOptions options)
    {
        _options = options;
        _loggerFactory = options.LoggerFactory ?? Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            b.AddConsole().SetMinimumLevel(LogLevel.Information));
        _logger = _loggerFactory.CreateLogger<GameServerHost>();

        // Join tokens are verified against JOIN_TOKEN_SECRET (required, enforced
        // by Program.cs at startup). Both may be a "current,previous" rotation
        // list; every entry verifies.
        _joinKeys = JwtKeyring.Parse(options.JoinTokenSecret);
        if (!_joinKeys.IsValid)
        {
            // Fail closed, loudly: no key means no join can ever succeed.
            _logger.LogWarning(
                "No join-token secret configured (JOIN_TOKEN_SECRET is empty) -- " +
                "every join will be rejected");
        }

        _metrics = options.Metrics;
        _world = new EcsWorld();
        _metrics?.SetEntityCountProvider(() => _world.EntityCount);
        _connections = new ConnectionManager();
        _playerStore = options.PlayerStore ?? new MemoryPlayerStore();
        _agonesSdk = options.AgonesSdk ?? new NoopAgonesSdk();

        if (!string.IsNullOrWhiteSpace(options.NakamaUrl))
        {
            _nakamaClient = new Nakama.NakamaClient(
                options.NakamaUrl,
                options.NakamaHttpKey,
                _loggerFactory.CreateLogger<Nakama.NakamaClient>());
        }

        var eventStream = options.EventStream ?? new NoopEventStream();
        _publisher = new EventPublisher(eventStream, _loggerFactory.CreateLogger<EventPublisher>(), _metrics);

        SimulationRates rates = options.SimulationRates ?? SimulationRates.Uniform(options.TickRate);
        _rates = rates;

        _inputHandler = new InputHandler(
            _world,
            _loggerFactory.CreateLogger<InputHandler>(),
            OnEntityDeath,
            // MovementHz, which is also the value published as JoinTokenResponse.tick_rate:
            // the rate a client predicts at has to be the rate this handler integrates at,
            // and reading the same property in both places is what keeps them from drifting
            // (docs/API.md, "tick_rate is the client's prediction rate").
            //
            // It is the base-timeline rate for a second reason too: the attack cooldown is
            // counted in base ticks (Combat.CooldownUntilTick is compared against the base
            // tick), so AttackCooldownTicks must be derived from the rate that advances it.
            // Passing the world rate here would make a 500ms cooldown last 2s.
            rates.MovementHz,
            options.MapBounds);

        // The observer is handed to the phase at construction rather than set afterwards:
        // a phase must hold no mutable instance state (ADR-12), and a settable observer is
        // precisely that.
        GameMetrics? metrics = _metrics;
        Action<SimulationGroup, long, long>? onGroupRan = metrics == null
            ? null
            : (group, start, end) => metrics.RecordGroupRun(group, start, end);
        ISimulationPhase? simulationPhase =
            options.SimulationPhaseFactory?.Invoke(_world, _loggerFactory, onGroupRan);

        _tickLoop = new TickLoop(
            _world,
            _inputHandler,
            _connections,
            rates,
            GameConstants.DefaultAoiRadius,
            _loggerFactory.CreateLogger<TickLoop>(),
            _metrics,
            options.KeyframeInterval,
            simulationPhase);

        _saver = new AsyncSaver(
            _playerStore,
            _world,
            options.MapId,
            options.SaveInterval,
            _loggerFactory.CreateLogger<AsyncSaver>(),
            _metrics);

        if (options.ServerRegistry != null)
        {
            if (options.Registration == null)
            {
                throw new ArgumentException(
                    $"{nameof(ServerOptions.Registration)} is required when {nameof(ServerOptions.ServerRegistry)} is set",
                    nameof(options));
            }
            _registration = new RegistrationService(
                options.ServerRegistry,
                options.Registration,
                () => _connections.Count,
                _loggerFactory.CreateLogger<RegistrationService>());
        }
    }

    /// <summary>Start the server and listen for connections.</summary>
    public async Task RunAsync(string addr, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Bind the configured transport. TCP and KCP both yield a reliable ordered
        // stream, so everything above the listener is transport-agnostic.
        try
        {
            _listener = TransportFactory.Listen(_options.Transport, addr, _options.TransportKey, _logger);
        }
        catch (Exception ex)
        {
            // Anyone awaiting ListeningAddressAsync must observe the bind failure rather
            // than wait for an address that will never arrive.
            _listening.TrySetException(ex);
            throw;
        }

        // Log the actual bound address (important when port=0 for ephemeral port allocation)
        var actualAddr = _listener.LocalEndPoint;

        // Publish the bound address before anything else can await it. The socket is already
        // listening at this point, so a client that connects on this signal lands in the
        // accept backlog even if the accept loop has not started yet.
        _listening.TrySetResult(actualAddr);

        _logger.LogInformation(
            "Game server listening on {Addr} via {Transport} (mode={Mode}, map={MapId}, id={ServerId})",
            actualAddr, _listener.Kind, _options.Mode, _options.MapId, _options.ServerId);

        // Mark ready with Agones. The listener is already bound at this point, so Ready
        // never claims a server that is not accepting. HttpAgonesSdk swallows sidecar
        // failures, so this cannot throw and cannot delay the registration below by more
        // than its request timeout.
        await _agonesSdk.ReadyAsync();

        // Learn the address Agones gave us, and advertise THAT (ADR-15 decision 2, option A).
        //
        // Under `portPolicy: Dynamic` the host port is chosen by the scheduler, so no
        // configuration can name it: the fleet manifest passes `--addr=:9000` and sets no
        // GAMESERVER_PUBLIC_ADDR, and without this read the server registers the hostless
        // `:9000`, which the gateway hands to the client verbatim and the client cannot
        // dial. That — not the health loop — is why the Agones path has never carried a
        // player.
        //
        // The read sits HERE and nowhere else: after ReadyAsync, because the address only
        // exists once the pod is scheduled, and before StartAsync, because the first thing
        // written to Redis must already be the right value rather than a wrong one repaired
        // a heartbeat later.
        //
        // Every failure mode falls back to the configured address, which is what running
        // outside a cluster must keep doing. GetAddressAsync never throws and returns null
        // on anything it cannot use.
        if (_agonesSdk.IsEnabled && _registration != null)
        {
            var assigned = await _agonesSdk.GetAddressAsync();
            if (assigned != null)
            {
                var configured = _registration.PublicAddr;

                // The port is always Agones'. Only the host can be overridden, and it has to
                // be: status.address is the node address on the cluster network, which a
                // client outside that network cannot dial (measured on k3d — the status says
                // 172.20.0.3 and the address that answers is 127.0.0.1, published by the
                // serverlb). Configuration supplying the port instead would put us straight
                // back into the bug this whole path exists to fix.
                var hostOverride = AgonesGameServerAddress.NormalizeHostOverride(
                    _options.AdvertiseHost, _logger);
                var advertised = hostOverride == null ? assigned : assigned.WithHost(hostOverride);

                _registration.OverridePublicAddr(advertised.ToString());

                // One line, deliberately naming where each half came from. When this is
                // wrong in production it is wrong silently — the server runs, the registry
                // looks healthy, and only the client knows — so the diagnosis has to be
                // sitting in the log before anyone goes looking for it.
                _logger.LogInformation(
                    "Advertising {Advertised} (host from {HostSource}, port {Port} from Agones status); " +
                    "configured value '{Configured}' not used",
                    advertised,
                    hostOverride == null
                        ? "Agones status.address"
                        : "GAMESERVER_ADVERTISE_HOST",
                    advertised.Port,
                    configured);

                if (hostOverride == null)
                {
                    // Not an error — inside the cluster it is right — but it is the single
                    // most likely reason a client cannot connect to a working server.
                    _logger.LogInformation(
                        "GAMESERVER_ADVERTISE_HOST is unset, so clients are handed the Agones node " +
                        "address '{Host}'. That is correct only where the node itself is reachable; " +
                        "set it to the host clients actually dial (the load-balancer or ingress " +
                        "address) if they are outside the cluster network.",
                        advertised.Address);
                }
            }
            else
            {
                // GAMESERVER_ADVERTISE_HOST is deliberately NOT applied here. Without the
                // status read there is no Agones port to pair it with, and pairing the
                // override host with a CONFIGURED port would invent an address that was
                // never assigned to anything — a plausible-looking value pointing nowhere,
                // which is worse to debug than the honestly-wrong configured one.
                _logger.LogWarning(
                    "Agones is enabled but its GameServer status could not be read; advertising " +
                    "the configured address '{Configured}' unchanged. Under portPolicy: Dynamic " +
                    "that value is almost certainly not dialable by a client. " +
                    "GAMESERVER_ADVERTISE_HOST is not applied without a port from Agones.",
                    _registration.PublicAddr);
            }
        }

        // Publish ourselves into the server registry the gateway reads, and keep the
        // entry alive. Done after the listener is up so we never advertise an address
        // that is not accepting yet — and after the bind, so a port=0 ephemeral listen
        // advertises the port it actually got.
        //
        // It is also deliberately after ReadyAsync (ADR-14 decision 3): registering first
        // would advertise an address that Agones may still be about to kill, which is the
        // ordering that lets the two writers — Agones over pod lifecycle, this server over
        // the Redis `map_id -> server` entry — disagree about whether the server exists.
        // On shutdown the order reverses: deregister first, then Agones Shutdown.
        //
        // Under GAMESERVER_REGISTER_ON_ALLOCATED the entry is held back further still, until
        // Agones reports this GameServer as Allocated (ADR-18 decision 4, mechanism 2). That
        // narrows ADR-14 decision 3 rather than reversing it: Ready still comes first and is
        // still a precondition — it has simply stopped being sufficient, because on a fleet
        // whose replicas all carry one GAMESERVER_MAP_ID a second Ready pod is a second live
        // server for that map with no allocation involved.
        //
        // The wait runs OFF this path, in the background, and that is load-bearing in two
        // ways: the health loop below has to be pinging while a buffer pod sits unallocated
        // (a pod that blocks here never pings and Agones kills it), and the listener has to
        // be accepting, because a client that reaches a just-allocated pod directly should
        // not be refused while the state read is still in flight.
        if (_registration != null)
        {
            if (_options.RegisterOnAllocated && _agonesSdk.IsEnabled)
            {
                var gateLogger = _loggerFactory.CreateLogger("AgonesAllocationGate");
                var gateCt = _cts.Token;
                _gatedRegistration = Task.Run(async () =>
                {
                    try
                    {
                        if (!await AgonesAllocationGate.WaitForAllocatedAsync(
                                _agonesSdk, gateCt, gateLogger))
                        {
                            // Cancelled: shutting down before ever being allocated. Nothing
                            // was published, so there is nothing to take away.
                            return;
                        }

                        await _registration.StartAsync(gateCt);
                    }
                    catch (OperationCanceledException) { /* shutdown raced the gate */ }
                    catch (Exception ex)
                    {
                        // An unobserved exception here would take the process down at a GC
                        // rather than at the fault. Registration failure is already
                        // non-fatal everywhere else and stays so here.
                        _logger.LogError(ex,
                            "Gated registration failed; this server will not appear in the registry");
                    }
                }, CancellationToken.None);
            }
            else
            {
                if (_options.RegisterOnAllocated && !_agonesSdk.IsEnabled)
                {
                    // Honouring the flag here would mean waiting for an allocation that
                    // cannot happen, i.e. never registering. Say so rather than hanging.
                    _logger.LogWarning(
                        "GAMESERVER_REGISTER_ON_ALLOCATED is set but Agones is disabled, so it is " +
                        "IGNORED — there is no GameServer object to reach Allocated. Registering " +
                        "at startup as usual.");
                }

                await _registration.StartAsync(_cts.Token);
            }
        }

        // Start background tasks
        var tickTask = _tickLoop.RunAsync(_cts.Token);
        var saveTask = _saver.RunAsync(_cts.Token);

        // The health loop only runs against a real SDK (ADR-14 decision 4). Pinging
        // NoopAgonesSdk logged "health loop started" and then reported nothing to anyone,
        // which reads in a log exactly like a working liveness contract.
        //
        // 2s, against the dotnet fleet manifest's health periodSeconds: 5 — two pings per
        // window, so one dropped request is not a strike. Once the manifest's
        // `disabled: true` comes off, a tick loop that starves this task long enough is a
        // pod restart; ADR-13's overload path (drop the backlog, resynchronise) is what
        // keeps a merely-slow server from being killed as a dead one.
        var healthTask = _agonesSdk.IsEnabled
            ? AgonesHealthLoop.RunAsync(_agonesSdk, TimeSpan.FromSeconds(2), _cts.Token,
                _loggerFactory.CreateLogger("AgonesHealth"))
            : Task.CompletedTask;

        // Accept loop
        var acceptTask = AcceptLoopAsync(_cts.Token);

        // Wait for any to complete (usually cancellation)
        await Task.WhenAny(tickTask, saveTask, acceptTask);

        // Shut down
        await ShutdownAsync();
    }

    /// <summary>
    /// Graceful shutdown: stop accepting, close connections, final save, agones shutdown.
    /// Idempotent and safe to call concurrently: the first caller performs the teardown,
    /// every other caller awaits that same teardown and observes the same outcome.
    /// RunAsync calls this at its tail while the owner (test harness, SIGTERM handler,
    /// Agones drain) usually calls it too — without the guard the two racers both walk
    /// the hold table and one cancels a CancellationTokenSource the other already
    /// disposed, throwing ObjectDisposedException out of a pod that should have drained.
    /// </summary>
    public async Task ShutdownAsync()
    {
        // Only the first caller runs the teardown; the rest await its completion so
        // "shutdown returned" always means "final save finished", never "someone else
        // started one".
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            await _shutdownComplete.Task;
            return;
        }

        try
        {
            _logger.LogInformation("Shutting down game server...");

            // The CTS is never disposed before the run task completes (see DisposeAsync),
            // but a caller-supplied linked token can still be torn down underneath us.
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }

            // Closing the listener also tears down every live KCP session: unlike TCP
            // there is no socket per peer whose close the kernel would propagate.
            try { _listener?.Dispose(); } catch { /* ignore */ }

            // If we are shutting down before the bind ever completed, release anyone waiting
            // on the address instead of leaving them on a task that will never finish.
            _listening.TrySetCanceled();

            // Drain the hold table by removal so each CTS has exactly one owner. The
            // reconnect path (HandleConnectionAsync) races us for the same entries and
            // also takes ownership via TryRemove, so whoever wins disposes it once.
            foreach (var userId in _holds.Keys)
            {
                if (!_holds.TryRemove(userId, out var holdCts))
                    continue;
                try { holdCts.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
                holdCts.Dispose();
            }

            // Notify connected clients before closing: send MsgDisconnect with
            // reason "server_shutdown" so clients can reconnect to another server
            // rather than waiting for a timeout. The 2s grace period lets TCP drain
            // the send buffer; clients that have already disconnected will ignore it.
            await DrainClientsAsync();

            _connections.CloseAll();

            // Settle the allocation gate before deregistering. It is already cancelled by
            // the Cancel above, but it can be mid-flight between "state read said Allocated"
            // and "StartAsync", and a registration landing AFTER the deregistration would
            // leave an entry pointing at a server that is gone until the 15s TTL reaped it —
            // the gateway would hand clients a black hole for that whole window. Bounded, so
            // a wedged sidecar read cannot hold up a drain.
            if (_gatedRegistration != null)
            {
                try { await _gatedRegistration.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* cancelled, faulted or too slow; deregistration below covers it */ }
            }

            // Leave the registry before the final save: the point is to stop the
            // gateway handing new clients to a server that is going away, rather than
            // making them wait out the heartbeat TTL on a black hole.
            if (_registration != null)
            {
                await _registration.DeregisterAsync();
            }

            // Final save
            await _saver.SaveAllAsync();

            // Agones shutdown
            await _agonesSdk.ShutdownAsync();

            _logger.LogInformation("Game server shutdown complete");
            _shutdownComplete.TrySetResult();
        }
        catch (Exception ex)
        {
            // Propagate to concurrent callers too, so a failed drain is not silently
            // reported as success to whoever awaited us.
            _shutdownComplete.TrySetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Report Allocate to Agones the first time a player lands here (ADR-14).
    ///
    /// <para>Exactly once per process, and never on the join's critical path: Agones' own
    /// allocation call already moves the GameServer to Allocated when the gateway allocates
    /// through the API, so this is the self-allocating case (a client that reaches a Ready
    /// pod directly) and a slow sidecar must not delay the player's join response by even
    /// one request timeout.</para>
    ///
    /// <para>Not balanced by anything on the way down. Agones has no un-allocate: an
    /// Allocated GameServer leaves that state by being shut down, which is what
    /// <see cref="ShutdownAsync"/> reports. So a map server that empties stays Allocated
    /// until it is drained — correct for the dungeon lifecycle, and for a map server it
    /// means the fleet will not re-hand this pod out. Revisit alongside ADR-2's map-fleet
    /// allocator policy, which is a precondition of ADR-14 stage 5 anyway.</para>
    /// </summary>
    private void NotifyAgonesAllocatedOnce()
    {
        if (!_agonesSdk.IsEnabled)
            return;
        if (Interlocked.Exchange(ref _allocateReported, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _agonesSdk.AllocateAsync();
            }
            catch (Exception ex)
            {
                // HttpAgonesSdk does not throw; this guards a custom implementation from
                // taking the process down through an unobserved task exception.
                _logger.LogWarning(ex, "Agones Allocate failed");
            }
        });
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ITransportConnection accepted;
            try
            {
                accepted = await _listener!.AcceptAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (ChannelClosedException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Accept error");
                continue;
            }

            // Handle connection on a background task (fire-and-forget)
            _ = Task.Run(() => HandleConnectionAsync(accepted, ct), ct);
        }
    }

    private async Task HandleConnectionAsync(ITransportConnection accepted, CancellationToken ct)
    {
        Connection? conn = null;
        // The world holds an entity for this user and the reconnect hold owes it a
        // removal. Set once the entity is created or reattached; never cleared.
        bool entityAttached = false;
        // PlayerJoined() was recorded for this connection, so PlayerLeft() must balance
        // it — and must NOT be called otherwise, or an aborted join would decrement
        // another player's count.
        bool countedOnline = false;
        // userId is needed by the finally block, so it lives outside the try.
        string userId = "";
        try
        {
            // Use a temporary logger-only connection for the handshake
            var connLogger = _loggerFactory.CreateLogger<Connection>();
            var tempConn = new Connection("pending", accepted, connLogger);

            // Step 1: Read MsgJoinToken
            var env = await tempConn.ReadOneAsync();
            if (env == null || (MsgType)env.Type != MsgType.JoinToken)
            {
                await SendError(tempConn, "Expected JoinToken message");
                tempConn.Close();
                return;
            }

            var joinReq = WireProtocol.GetPayload<JoinTokenRequest>(env);

            // Step 2: Verify JWT
            var claims = _joinKeys.Verify(joinReq.Token);
            if (claims == null)
            {
                await SendError(tempConn, "Invalid or expired token");
                tempConn.Close();
                return;
            }

            // Step 3: Check server ID claim — mandatory, no empty bypass
            if (string.IsNullOrEmpty(claims.ServerId) || claims.ServerId != _options.ServerId)
            {
                await SendError(tempConn, "Token is for a different server");
                tempConn.Close();
                return;
            }


            // Step 3b: JTI replay protection
            if (string.IsNullOrEmpty(claims.Jti) || !_jtiTracker.TryConsume(claims.Jti))
            {
                await SendError(tempConn, "Token already used");
                tempConn.Close();
                return;
            }
            // Step 4: Check capacity
            if (_connections.Count >= _options.Capacity)
            {
                await SendError(tempConn, "Server is full");
                tempConn.Close();
                return;
            }

            userId = claims.UserId;

            // Cancel any pending entity hold for this user (reconnect)
            if (_holds.TryRemove(userId, out var holdCts))
            {
                holdCts.Cancel();
                holdCts.Dispose();
                _logger.LogInformation("Player {UserId} reconnected, hold cancelled", userId);
            }

            // Acquire or reattach entity
            var existing = _world.GetEntity(userId);
            if (existing == null)
            {
                // Load from store or create new. PlayerSpawn owns the policy: saved
                // coordinates are only reused when the row belongs to THIS map, because
                // player_states holds one row per player and a row written by another
                // map's server describes a position that does not exist here. HP and
                // max HP are map-independent and carry across either way.
                var saved = await _playerStore.LoadPlayerAsync(userId, ct);
                var spawn = PlayerSpawn.Resolve(saved, _options.MapId, _options.MapBounds);

                if (spawn.DiscardedMapId != null)
                {
                    _logger.LogInformation(
                        "Player {UserId} last saved on map {SavedMapId}; spawning at {MapId} spawn point " +
                        "instead of stale coordinates ({X:F2}, {Y:F2})",
                        userId, spawn.DiscardedMapId, _options.MapId, saved!.X, saved.Y);
                }

                var entity = new EntityState
                {
                    Id = userId,
                    Type = "player",
                    Position = spawn.Position,
                    Hp = spawn.Hp,
                    MaxHp = spawn.MaxHp,
                    Speed = ServerDefaults.DefaultPlayerSpeed,
                    Attack = ServerDefaults.DefaultPlayerAttack,
                    Defense = ServerDefaults.DefaultPlayerDefense
                };
                _world.AddEntity(entity);
            }

            // From here the world holds an entity for this user, so teardown is
            // MANDATORY on every exit path — see the finally block. Before this flag
            // existed, any throw between here and OnPlayerDisconnected (most easily the
            // WriteOneAsync below, against a client that gave up during the handshake)
            // left the entity in the world with no hold scheduled, i.e. leaked forever.
            entityAttached = true;

            // Create the real connection with the verified user ID, reusing the same
            // accepted transport connection (no reconnect, no second handshake).
            conn = new Connection(userId, accepted, connLogger, tempConn.Encoding);

            // Register connection
            _connections.Add(conn);

            // Record the join BEFORE sending the response: the TCP stack may
            // deliver the frame to the client before our FlushAsync Task
            // completes, so any observer that acts on the response (tests,
            // monitoring) must see the counters already updated.  If the
            // subsequent WriteOneAsync throws, the finally block calls
            // OnPlayerDisconnected which calls PlayerLeft — the counter stays
            // balanced.
            _metrics?.PlayerJoined();
            // players_online is an independent counter, not derived from _connections,
            // so it must be balanced exactly once and only if it was incremented.
            countedOnline = true;
            // Fire-and-forget: the gateway's capacity view should be fresh, but a slow
            // or down Redis must never delay a player entering the world.
            _registration?.NotifyPlayerCountChanged();
            NotifyAgonesAllocatedOnce();
            _logger.LogInformation("Player {UserId} joined (total: {Count})", userId, _connections.Count);

            // Step 5: Send JoinTokenResp
            //
            // TickRate is the CRITICAL rate, not the world rate: it is the cadence of
            // input, movement integration and combat, which is exactly what the client
            // predicts. The world rate governs the snapshot cadence, which the client
            // observes from the snapshots themselves and does not have to be told.
            //
            // It rides only the success path. A rejected join has no session to predict
            // in, and the caller has not proved it is entitled to anything, so answering
            // an unauthenticated peer with the server's simulation configuration would be
            // giving away tuning data for nothing. Absent means 0, which the schema
            // defines as "refuse to predict" — the correct answer to a failed join.
            var resp = WireProtocol.NewEnvelope(MsgType.JoinTokenResp,
                new JoinTokenResponse { Ok = true, UserId = userId, TickRate = (uint)_rates.MovementHz },
                conn.Encoding);
            await conn.WriteOneAsync(resp);

            // Step 6: Start read/write loops + heartbeat
            var writeTask = conn.WriteLoopAsync();
            var readTask = conn.ReadLoopAsync(OnMessageReceived);
            var heartbeatTask = conn.HeartbeatLoopAsync();

            await Task.WhenAny(readTask, writeTask, heartbeatTask);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection handler error");
        }
        finally
        {
            // Step 7: hold the entity for the reconnect window. This runs from the
            // finally block, not the happy path, because an abort after the entity was
            // attached leaks it otherwise: nothing else ever removes an entity, so a
            // missed hold is permanent, and every leaked entity keeps being scanned by
            // AOI and diffed on every tick for the life of the process.
            if (entityAttached)
            {
                OnPlayerDisconnected(userId, countedOnline);
            }
            conn?.Dispose();
        }
    }

    private Task OnMessageReceived(Connection conn, Envelope env)
    {
        switch ((MsgType)env.Type)
        {
            case MsgType.Input:
                var input = WireProtocol.GetPayload<InputMessage>(env);
                _world.PushInput(conn.UserId, new InputData(
                    input.Tick,
                    input.MoveX,
                    input.MoveY,
                    input.AttackTargetId));
                break;

            case MsgType.Resync:
                // Client lost or distrusts its reconstructed state: promote the next
                // snapshot for this connection to a full keyframe. Payload is ignored.
                conn.DeltaState.RequestFull();
                _metrics?.RecordResyncRequested();
                break;

            case MsgType.TransferMap:
                // Fire-and-forget: the transfer handler is async (save + respond),
                // but the read loop must not block on it.
                _ = HandleTransferMapAsync(conn, env);
                break;

            case MsgType.Ping:
                conn.HandlePing(WireProtocol.GetPayload<PingMessage>(env));
                break;

            case MsgType.Pong:
                conn.RecordPong();
                break;

            case MsgType.Disconnect:
                conn.Close();
                break;

            default:
                _logger.LogDebug("Unhandled message type {Type} from {UserId}",
                    env.Type, conn.UserId);
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handle a client request to transfer to a different map.
    /// </summary>
    /// <remarks>
    /// The flow is client-driven:
    /// <list type="number">
    ///   <item>Client sends MsgTransferMap(map_id) to the current game server.</item>
    ///   <item>Game server validates, saves state, responds, removes entity, closes connection.</item>
    ///   <item>Client sends MsgEnterWorld(new_map_id) to the gateway (existing flow).</item>
    ///   <item>Gateway assigns the new map server and mints a join token.</item>
    ///   <item>Client connects to the new server with the join token.</item>
    /// </list>
    /// The entity is removed immediately (not held) because this is an intentional
    /// transfer, not a disconnect: there is nothing to reconnect to.
    /// </remarks>
    private async Task HandleTransferMapAsync(Connection conn, Envelope env)
    {
        try
        {
            var req = WireProtocol.GetPayload<TransferMapRequest>(env);

            // Validate: the target map must differ from the current one.
            if (string.IsNullOrEmpty(req.MapId))
            {
                await SendTransferError(conn, "map_id is required");
                return;
            }
            if (req.MapId == _options.MapId)
            {
                await SendTransferError(conn, "already on this map");
                return;
            }

            // Force an immediate save so the destination server sees fresh state.
            await _saver.SavePlayerAsync(conn.UserId);

            // Tell the client the transfer is accepted.
            var resp = WireProtocol.NewEnvelope(MsgType.TransferMapResp,
                new TransferMapResponse { Ok = true }, conn.Encoding);
            await conn.WriteOneAsync(resp);

            _logger.LogInformation(
                "Player {UserId} transferring from {FromMap} to {ToMap}",
                conn.UserId, _options.MapId, req.MapId);

            // Clean removal: cancel any existing hold, remove the entity from the
            // world, and close the connection. No hold is scheduled because the
            // player is intentionally leaving, not disconnecting.
            if (_holds.TryRemove(conn.UserId, out var holdCts))
            {
                holdCts.Cancel();
                holdCts.Dispose();
            }
            _connections.Remove(conn.UserId);
            _world.RemoveEntity(conn.UserId);
            _metrics?.PlayerLeft();
            _registration?.NotifyPlayerCountChanged();
            conn.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transfer map failed for {UserId}", conn.UserId);
            try { await SendTransferError(conn, "internal error"); }
            catch { /* connection may already be dead */ }
        }
    }

    private async Task SendTransferError(Connection conn, string error)
    {
        var resp = WireProtocol.NewEnvelope(MsgType.TransferMapResp,
            new TransferMapResponse { Ok = false, Error = error }, conn.Encoding);
        await conn.WriteOneAsync(resp);
    }

    /// <param name="countedOnline">
    /// Whether <see cref="GameMetrics.PlayerJoined"/> was recorded for this connection.
    /// An aborted join never incremented it, and decrementing anyway would corrupt the
    /// count for the players who really are online.
    /// </param>
    private void OnPlayerDisconnected(string userId, bool countedOnline)
    {
        _connections.Remove(userId);
        if (countedOnline)
        {
            _metrics?.PlayerLeft();
        }
        _registration?.NotifyPlayerCountChanged();

        var holdTtl = _options.Mode == "dungeon"
            ? TimeSpan.FromSeconds(60)
            : _options.HoldTtl;

        var holdCts = new CancellationTokenSource();
        // Replace rather than overwrite: a superseded hold's CTS would otherwise never
        // be cancelled or disposed, and its timer would keep running to fire a removal
        // this one already owns.
        if (_holds.TryRemove(userId, out var superseded))
        {
            superseded.Cancel();
            superseded.Dispose();
        }
        _holds[userId] = holdCts;

        _logger.LogInformation("Player {UserId} disconnected, holding entity for {Ttl}",
            userId, holdTtl);

        // Fire-and-forget: remove entity after hold TTL unless reconnected
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(holdTtl, holdCts.Token);

                // Persist BEFORE removing: once the entity leaves the world the
                // periodic AsyncSaver can no longer see it, so skipping this
                // would discard everything the player did since the last save
                // tick (up to SaveInterval, 30s) instead of at most one tick.
                await _saver.SavePlayerAsync(userId);

                // Claim the removal atomically: TryRemove(KeyValuePair) only succeeds
                // while THIS hold is still the registered one. A reconnect during the
                // save above cancels and removes it first, and must not then have its
                // freshly reattached entity deleted out from under it.
                if (_holds.TryRemove(new KeyValuePair<string, CancellationTokenSource>(userId, holdCts))
                    && _connections.Get(userId) == null)
                {
                    _world.RemoveEntity(userId);
                    _logger.LogInformation("Entity hold expired for {UserId}, entity removed", userId);
                }
                else
                {
                    _logger.LogInformation(
                        "Entity hold for {UserId} superseded by a reconnect; entity kept", userId);
                }
            }
            catch (OperationCanceledException)
            {
                // Reconnected before hold expired
            }
            catch (Exception ex)
            {
                // The removal must not be lost to an unexpected failure: nothing else
                // ever removes an entity, so swallowing this silently would leak it.
                _logger.LogError(ex, "Entity hold for {UserId} failed; removing anyway", userId);
                _holds.TryRemove(new KeyValuePair<string, CancellationTokenSource>(userId, holdCts));
                if (_connections.Get(userId) == null)
                {
                    _world.RemoveEntity(userId);
                }
            }
            finally
            {
                holdCts.Dispose();
            }
        });
    }

    /// <summary>
    /// Send MsgDisconnect(reason="server_shutdown") to every connected client and
    /// wait briefly for the message to reach the wire before connections are torn
    /// down. Best effort: a client that already hung up silently fails the write.
    /// </summary>
    private async Task DrainClientsAsync()
    {
        var disconnectMsg = new RpgMmo.Wire.V1.DisconnectMessage { Reason = "server_shutdown" };

        _connections.ForEach(conn =>
        {
            try
            {
                var env = WireProtocol.NewEnvelope(MsgType.Disconnect, disconnectMsg, conn.Encoding);
                // Fire-and-forget per connection: WriteOneAsync may throw if the
                // peer is already gone, but that is fine — we are shutting down.
                _ = conn.WriteOneAsync(env);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send shutdown disconnect to {UserId}", conn.UserId);
            }
        });

        _logger.LogInformation(
            "Sent shutdown notification to {Count} client(s), waiting 2s for drain",
            _connections.Count);

        // Give TCP time to flush the send buffers before CloseAll tears them down.
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    private void OnEntityDeath(EntityState victim, EntityState killer)
    {
        if (_publisher != null)
        {
            var payload = new DeathPayload(
                victim.Id, victim.Type, killer.Id, _options.MapId, _options.ServerId);
            _ = _publisher.PublishDeathAsync("entity_killed", payload);
        }

        // Award gold + leaderboard score when a player kills a mob
        if (_nakamaClient != null && killer.Type == "player" && victim.Type == "mob")
        {
            _ = _nakamaClient.RewardKillAsync(killer.Id, victim.Id, _options.MapId);
            _ = _nakamaClient.SubmitKillAsync(killer.Id);
        }
    }

    private static async Task SendError(Connection conn, string error)
    {
        var resp = WireProtocol.NewEnvelope(MsgType.JoinTokenResp,
            new JoinTokenResponse { Ok = false, Error = error }, conn.Encoding);
        await conn.WriteOneAsync(resp);
    }

    private static (string host, int port) ParseAddr(string addr)
    {
        // Handle formats: ":9000", "0.0.0.0:9000", "localhost:9000"
        int colonIdx = addr.LastIndexOf(':');
        if (colonIdx < 0)
            return ("", int.Parse(addr));

        string host = addr[..colonIdx];
        int port = int.Parse(addr[(colonIdx + 1)..]);
        return (host, port);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        if (_registration != null)
        {
            await _registration.DisposeAsync();
        }
        _nakamaClient?.Dispose();
        // Disposed only here, once the run loop is guaranteed done with it — disposing
        // it inside ShutdownAsync would hand RunAsync's background tasks a dead token
        // source while they are still unwinding.
        _cts?.Dispose();
        _world.Dispose();
    }
}
