using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Agones;
using GameServer.Content;
using GameServer.Events;
using GameServer.Observability;
using GameServer.Scaffolding;
using GameServer.Persistence;
using GameServer.Net.Transport;
using GameServer.Registry;
using GameServer.Server;

// ── Parse command-line args and environment variables ──

string mode = GetArg(args, "--mode") ?? Env("GAMESERVER_MODE") ?? "map";
string addr = GetArg(args, "--addr") ?? Env("GAMESERVER_ADDR") ?? ":9000";
string mapId = GetArg(args, "--map-id") ?? Env("GAMESERVER_MAP_ID") ?? "map_01";
string serverId = GetArg(args, "--server-id") ?? Env("GAMESERVER_ID") ?? Env("POD_NAME") ?? $"gs-{Guid.NewGuid():N}"[..12];
int capacity = int.TryParse(GetArg(args, "--capacity") ?? Env("GAMESERVER_CAPACITY"), out var cap) ? cap : 100;
// Falls back to the shared constant, not to a literal. The client derives its own
// integration step from the same constant, and it is compiled into both sides, so a
// literal here means bumping GameConstants.DefaultTickRate moves the client and leaves
// the server behind — a desync with no error on either side, which reads to a player as
// rubber-banding rather than as a misconfiguration. The flag and the env var can still
// move the server alone, but that is no longer silent: the critical rate now rides the
// join response (`JoinTokenResponse.tick_rate`), so a client is told what to predict at
// instead of assuming — #93.
int tickRate = int.TryParse(GetArg(args, "--tick-rate") ?? Env("GAMESERVER_TICK_RATE"), out var tr)
    ? tr : GameConstants.DefaultTickRate;
// Multi-rate simulation. The three groups are named for what they do, not for how fast
// they run, because how fast they run is configuration: SIM_CRITICAL_HZ / SIM_WORLD_HZ /
// SIM_BACKGROUND_HZ, injected by the deployment (GitHub Environment -> .env -> compose, or
// the Agones fleet env block). Changing a rate must never require touching a gameplay
// system, which is why no system reads these.
//
// GAMESERVER_TICK_RATE still works and still means what it meant: when it is set and the
// SIM_* variables are not, every group runs at that one rate and the server behaves exactly
// as it did before this existed.
// Checks flags as well as environment variables: `--tick-rate 30 --sim-world-hz 15`
// must mean the same thing as the equivalent env vars, or the two configuration channels
// would disagree about which one wins.
bool anySimVar = GetArg(args, "--sim-critical-hz") != null || Env("SIM_CRITICAL_HZ") != null
              || GetArg(args, "--sim-world-hz") != null || Env("SIM_WORLD_HZ") != null
              || GetArg(args, "--sim-background-hz") != null || Env("SIM_BACKGROUND_HZ") != null;
bool tickRateSet = (GetArg(args, "--tick-rate") ?? Env("GAMESERVER_TICK_RATE")) != null;
int criticalHz = int.TryParse(GetArg(args, "--sim-critical-hz") ?? Env("SIM_CRITICAL_HZ"), out var chz)
    ? chz
    : (tickRateSet && !anySimVar ? tickRate : SimulationRates.DefaultCriticalHz);
int worldHz = int.TryParse(GetArg(args, "--sim-world-hz") ?? Env("SIM_WORLD_HZ"), out var whz)
    ? whz
    : (tickRateSet && !anySimVar ? tickRate : SimulationRates.DefaultWorldHz);
int backgroundHz = int.TryParse(GetArg(args, "--sim-background-hz") ?? Env("SIM_BACKGROUND_HZ"), out var bhz)
    ? bhz
    : (tickRateSet && !anySimVar ? tickRate : SimulationRates.DefaultBackgroundHz);
// Delta snapshots between full keyframes. 0 or less = send a full snapshot every tick
// (pre-delta behaviour), the escape hatch for a client that cannot merge deltas.
int keyframeInterval = int.TryParse(GetArg(args, "--keyframe-interval") ?? Env("GAMESERVER_KEYFRAME_INTERVAL"), out var kf)
    ? kf : GameConstants.DefaultKeyframeInterval;
// AOI-gather worker threads. 1 = serial, the default and the pre-pool behaviour.
// Only takes effect above TickLoop.GatherParallelMinViewers viewers -- see
// ServerOptions.GatherWorkers for why it is opt-in.
int gatherWorkers = int.TryParse(GetArg(args, "--gather-workers") ?? Env("GAMESERVER_GATHER_WORKERS"), out var gw) && gw > 0
    ? gw : 1;
float mapWidth = float.TryParse(GetArg(args, "--map-width") ?? Env("GAMESERVER_MAP_WIDTH"),
    System.Globalization.CultureInfo.InvariantCulture, out var mw) && mw > 0f
    ? mw : GameConstants.DefaultMapWidth;
float mapHeight = float.TryParse(GetArg(args, "--map-height") ?? Env("GAMESERVER_MAP_HEIGHT"),
    System.Globalization.CultureInfo.InvariantCulture, out var mh) && mh > 0f
    ? mh : GameConstants.DefaultMapHeight;
bool useAgones = HasFlag(args, "--agones") || Env("AGONES_ENABLED") == "true";
bool enableEnemySpawner = Env("GAMESERVER_ENEMIES") != "false"; // on by default, opt out with GAMESERVER_ENEMIES=false
int loadTestEntities = int.TryParse(
    GetArg(args, "--loadtest-entities") ?? Env("LOADTEST_ENTITIES"), out var lte) ? lte : 0;
// Nakama integration: server-to-server RPC for economy + leaderboard
string? nakamaUrl = Env("NAKAMA_URL"); // e.g. http://rpg-nakama:7350
string nakamaHttpKey = Env("NAKAMA_HTTP_KEY") ?? "defaulthttpkey";
string jwtSecret = GetArg(args, "--jwt-secret") ?? Env("JWT_SECRET") ?? "";
// Secret the GATEWAY signs join tokens with. Deliberately NOT JWT_SECRET: this value
// is distributed to every game-server pod, so a compromised pod must not be able to
// mint Nakama-style auth tokens. REQUIRED (fatal if unset).
// Comma-separated ("current,previous") to rotate without dropping joins.
string joinTokenSecret = GetArg(args, "--join-token-secret") ?? Env("JOIN_TOKEN_SECRET") ?? "";
// Metrics listen address. Unset -> ":9101"; explicitly empty -> metrics disabled.
string metricsAddr = GetArg(args, "--metrics-addr")
    ?? Environment.GetEnvironmentVariable("METRICS_ADDR")
    ?? ":9101";
// Game-state database DSN. Unset -> in-memory player store (state lost on restart).
string? gameDbUrl = GetArg(args, "--game-db-url") ?? Env("GAME_DB_URL");
// Migrate-only mode: apply pending schema migrations, then exit without listening.
// CD runs this before the deploy step so migrations happen at a deterministic point.
bool migrateOnly = HasFlag(args, "--migrate-only") || Env("GAMESERVER_MIGRATE_ONLY") == "true";
// Redis holding the server registry the gateway reads. Unset -> no self-registration
// (single-process / test default), and the gateway will not find this server.
string? redisAddr = GetArg(args, "--redis") ?? Env("REDIS_ADDR");
string? redisPassword = GetArg(args, "--redis-password") ?? Env("REDIS_PASSWORD");
// Realtime transport for the gameplay hop: "tcp" (default) or "kcp". Matches Go's
// --transport flag; the value is also what gets advertised to clients through the
// registry, so it must describe what the listener actually speaks.
string transport = TransportKind.Normalize(GetArg(args, "--transport") ?? Env("GAMESERVER_TRANSPORT"));
// Pre-shared AES-256 key for KCP. The SAME variable and the same derivation as the
// Go side (backend/shared/transport): 64 hex chars are used verbatim, anything else
// is stretched with HKDF-SHA256. Empty = plaintext.
string transportKey = Env(TransportKind.KeyEnvVar) ?? "";
// Address ADVERTISED TO CLIENTS. The gateway returns it verbatim in
// MsgEnterWorldResp.ServerAddr, so it must be dialable BY THE CLIENT — which is not
// the listen address whenever a container maps ports (listen :9000, clients reach
// <host>:9200). Falls back to the listen address, which is correct for host mode.
string publicAddr = GetArg(args, "--public-addr") ?? Env("GAMESERVER_PUBLIC_ADDR") ?? addr;
// HOST ONLY, and Agones only. Replaces the host part of the address read from the Agones
// GameServer status while the PORT still comes from that status, because under
// portPolicy: Dynamic only Agones knows the port.
//
// Why this is not just GAMESERVER_PUBLIC_ADDR: status.address is the NODE address on the
// cluster network. Measured on k3d — the status reports 172.20.0.3 and a client cannot
// reach it (refused from WSL2, Test-NetConnection False from Windows), while 127.0.0.1,
// which the k3d serverlb publishes, answers from both. The host is a deployment fact the
// cluster cannot know; the port is one only the cluster knows. Hence two knobs:
//
//   GAMESERVER_PUBLIC_ADDR   full host:port, used when Agones is OFF
//   GAMESERVER_ADVERTISE_HOST  host only,    used when Agones is ON and the status read worked
//
// Exactly one of them applies to any given deployment. Setting this one with Agones off
// does nothing at all — see the start-up warning below.
string? advertiseHost = GetArg(args, "--advertise-host") ?? Env("GAMESERVER_ADVERTISE_HOST");
// Hold the registry entry back until Agones reports this GameServer Allocated, instead of
// publishing it right after Ready. OFF by default: a fleet that has not been migrated must
// behave exactly as it did before this option existed. Agones only — with no sidecar there
// is no allocation to wait for, and gating on one would mean never registering (the server
// logs and ignores it in that case).
bool registerOnAllocated =
    HasFlag(args, "--register-on-allocated") || Env("GAMESERVER_REGISTER_ON_ALLOCATED") == "true";

// ── Logging ──

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger("Program");

// Resolved once so a bad AGONES_SDK_HTTP_PORT warns once rather than in both the
// start-up banner and the SDK constructor. Meaningless when useAgones is false.
int agonesPort = useAgones ? HttpAgonesSdk.ResolvePort(logger) : HttpAgonesSdk.DefaultPort;

logger.LogInformation("GameServer .NET starting");
logger.LogInformation("  Mode:      {Mode}", mode);
logger.LogInformation("  Address:   {Addr}", addr);
logger.LogInformation("  Transport: {Transport}{Encryption}", transport,
    transport == TransportKind.Kcp
        ? string.IsNullOrWhiteSpace(transportKey) ? " (UNENCRYPTED)" : " (AES-256)"
        : "");
logger.LogInformation("  MapId:     {MapId}", mapId);
logger.LogInformation("  ServerId:  {ServerId}", serverId);
logger.LogInformation("  Capacity:  {Capacity}", capacity);
logger.LogInformation("  SimRates:  {Rates}", $"critical={criticalHz}Hz world={worldHz}Hz background={backgroundHz}Hz");
logger.LogInformation("  Snapshots: {Mode}", keyframeInterval > 0
    ? $"delta, keyframe every {keyframeInterval} snapshots"
    : "full every tick (delta disabled)");
logger.LogInformation("  MapSize:   {Width}x{Height} world units (centered on origin)", mapWidth, mapHeight);
logger.LogInformation("  Agones:    {Agones}", useAgones
    ? $"HTTP sidecar at localhost:{agonesPort}"
    : "disabled (no-op SDK)");
if (useAgones)
{
    logger.LogInformation("  Advertise: {Advertise}",
        string.IsNullOrWhiteSpace(advertiseHost)
            ? "host from the Agones GameServer status (GAMESERVER_ADVERTISE_HOST unset), port from Agones"
            : $"host '{advertiseHost}' (GAMESERVER_ADVERTISE_HOST), port from Agones");
}
logger.LogInformation("  Register:  {Register}",
    registerOnAllocated && useAgones
        ? "on Agones Allocated (a Ready-but-unallocated pod holds no registry entry)"
        : registerOnAllocated
            ? "at startup (GAMESERVER_REGISTER_ON_ALLOCATED set but Agones is disabled -- IGNORED)"
            : "at startup, right after Ready (default)");
logger.LogInformation("  Nakama:    {Nakama}", string.IsNullOrWhiteSpace(nakamaUrl) ? "disabled (NAKAMA_URL unset)" : nakamaUrl);
logger.LogInformation("  Metrics:   {Metrics}", string.IsNullOrWhiteSpace(metricsAddr) ? "disabled" : metricsAddr);
logger.LogInformation("  GameDB:    {GameDb}",
    string.IsNullOrWhiteSpace(gameDbUrl) ? "memory" : PostgresPlayerStore.MaskDsn(gameDbUrl));
logger.LogInformation("  Registry:  {Registry}",
    string.IsNullOrWhiteSpace(redisAddr)
        ? "disabled (REDIS_ADDR unset -- the gateway will NOT find this server)"
        : $"redis {redisAddr}, advertising '{publicAddr}' ({transport})");
logger.LogInformation("  Events:    {Events}",
    string.IsNullOrWhiteSpace(redisAddr)
        ? "noop (REDIS_ADDR unset -- cross-server events are discarded)"
        : $"redis streams {redisAddr} -> {RedisEventStream.KeyPrefix}{EventStreams.Game}");
logger.LogInformation("  Kicks:     {Kicks}",
    string.IsNullOrWhiteSpace(redisAddr)
        ? "disabled (REDIS_ADDR unset -- duplicate-login supersede events are never acted on)"
        : $"consuming {RedisKickConsumer.StreamKey} as group {RedisKickConsumer.GroupPrefix}{serverId}");
// A HOSTLESS advertised address (empty, 0.0.0.0, :: or [::] as the host part) is
// not dialable by a client: the gateway hands the value back verbatim, so only
// clients that rewrite it to loopback themselves will connect (a C# TcpClient
// throws outright). Two shapes reach here and both deserve a word, but they are
// not equally wrong:
//   - unset, so publicAddr fell back to the listen address. Correct for host-mode
//     deploys, wrong in a container with a published port. Informational.
//   - explicitly SET and still hostless. The operator meant to advertise a mapped
//     port but omitted the host, so it is wrong in every topology. A real warning.
// Never fatal either way: the comment on publicAddr above documents that a bare
// listen address IS correct for host mode, so refusing to start or to register
// would break a supported topology.
// Under Agones the hostless value is EXPECTED and is not the value that gets registered:
// the host server reads the assigned address from the sidecar between Ready and
// registration and advertises that instead (ADR-15 decision 2, option A). Saying
// "clients will fail to connect" here would be wrong and would train operators to
// ignore the line in the one topology where it still means something.
// Set but inert: GAMESERVER_ADVERTISE_HOST only ever applies on the Agones path. Silence
// here would leave an operator believing they had configured the advertised address while
// the server advertised something else entirely.
if (!string.IsNullOrWhiteSpace(advertiseHost) && !useAgones)
{
    logger.LogWarning(
        "  GAMESERVER_ADVERTISE_HOST is set to '{Host}' but Agones is disabled, so it is " +
        "IGNORED — it only replaces the host of an address read from the Agones GameServer " +
        "status. Without Agones the advertised address is GAMESERVER_PUBLIC_ADDR " +
        "(currently '{PublicAddr}'), which takes a full host:port.",
        advertiseHost, publicAddr);
}

if (!string.IsNullOrWhiteSpace(redisAddr) && IsHostlessAddr(publicAddr) && useAgones)
{
    logger.LogInformation(
        "  The advertised address '{Addr}' has no host part, which is expected under Agones: " +
        "the address handed to clients is read from the GameServer status after Ready and " +
        "registered in its place. If that read fails, this value is registered instead and " +
        "clients will not be able to dial it.", publicAddr);
}
else if (!string.IsNullOrWhiteSpace(redisAddr) && IsHostlessAddr(publicAddr))
{
    if (publicAddr == addr)
    {
        logger.LogInformation(
            "  GAMESERVER_PUBLIC_ADDR is unset, so clients will be handed the listen address '{Addr}'. " +
            "That is correct for host deployments; in containers with a published port, set it to " +
            "<host>:<published-port> or clients will fail to connect.", publicAddr);
    }
    else
    {
        logger.LogWarning(
            "  GAMESERVER_PUBLIC_ADDR is set to '{PublicAddr}', which has no host part. Clients are handed " +
            "that value verbatim and cannot dial it. Set it to <host>:<published-port> -- " +
            "'127.0.0.1:{Port}' for a local stack, '<public-host>:{Port}' on a VPS.",
            publicAddr, AddrPort(publicAddr), AddrPort(publicAddr));
    }
}

// ── Migrate-only mode (CD schema step) ──
//
// Applies pending migrations and exits. No listener, no tick loop, no metrics —
// this is meant to run as a one-shot step before the servers are (re)started, so
// the schema is already current by the time any of them boot.

if (migrateOnly)
{
    if (string.IsNullOrWhiteSpace(gameDbUrl))
    {
        logger.LogCritical("--migrate-only requires --game-db-url / GAME_DB_URL");
        return 2;
    }

    try
    {
        await using var migrateStore = await PostgresPlayerStore.ConnectAsync(gameDbUrl, CancellationToken.None);
        var result = await migrateStore.MigrateAsync(CancellationToken.None, logger);
        logger.LogInformation("migrate-only complete ({Applied} applied, {Existing} already present)",
            result.Applied.Count, result.AlreadyApplied.Count);
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "migration failed ({Dsn})", PostgresPlayerStore.MaskDsn(gameDbUrl));
        return 1;
    }
}

// ── Validate ──

if (!SimulationRates.TryCreate(criticalHz, worldHz, backgroundHz, out SimulationRates? simRates, out string? simError))
{
    // Fail fast, do not fall back. A server that silently substitutes a default for an
    // unusable rate is a server whose behaviour does not match its configuration, and the
    // operator has no way to find out — the same class of silent divergence as the tick
    // rate never reaching the client (#93).
    logger.LogCritical("invalid simulation rate configuration: {Error}", simError);
    return 2;
}

// TryCreate guarantees this on success; the local makes that guarantee visible to the
// compiler instead of asserting it at each use site.
SimulationRates simulationRates = simRates!;

if (!TransportKind.IsValid(transport))
{
    logger.LogCritical("unknown transport {Transport} (want {Tcp} or {Kcp})",
        transport, TransportKind.Tcp, TransportKind.Kcp);
    return 2;
}

// Mirror of the Go listener's warning (backend/shared/transport/transport.go): KCP
// without a key puts the join token and every snapshot on the wire in cleartext UDP,
// which is fine for local dev and not for anything reachable from the internet.
if (transport == TransportKind.Kcp && string.IsNullOrWhiteSpace(transportKey))
{
    logger.LogWarning(
        "KCP listener is UNENCRYPTED -- join tokens and gameplay traffic are in cleartext; set {KeyVar} " +
        "(32-byte hex) before exposing this port (addr={Addr}, transport={Transport})",
        TransportKind.KeyEnvVar, addr, TransportKind.Kcp);
}
if (transport == TransportKind.Tcp && !string.IsNullOrWhiteSpace(transportKey))
{
    logger.LogWarning(
        "{KeyVar} is set but the transport is TCP, which has no packet encryption -- the key is IGNORED. " +
        "Use --transport kcp, or terminate TLS in front of this listener.", TransportKind.KeyEnvVar);
}

if (string.IsNullOrEmpty(jwtSecret))
{
    logger.LogWarning("JWT_SECRET not set -- token validation will reject all tokens in production");
}

// JOIN_TOKEN_SECRET is mandatory: a game server that cannot verify join tokens
// must not start, otherwise every client that tries to join gets a cryptic rejection.
if (string.IsNullOrEmpty(joinTokenSecret))
{
    logger.LogCritical("JOIN_TOKEN_SECRET is required but not set -- refusing to start. " +
                       "Set JOIN_TOKEN_SECRET to a dedicated secret (and the matching value on the gateway). " +
                       "Do NOT reuse JWT_SECRET: a compromised game-server pod must not be able to forge auth tokens.");
    return 2;
}
var joinKeyring = JwtKeyring.Parse(joinTokenSecret);
if (!joinKeyring.IsValid)
{
    logger.LogCritical("JOIN_TOKEN_SECRET contains no usable secrets -- refusing to start");
    return 2;
}
if (joinTokenSecret == jwtSecret)
{
    logger.LogWarning("JOIN_TOKEN_SECRET and JWT_SECRET are the same value -- a leak of either " +
                      "secret compromises both the Nakama auth hop and the game-server hop. Use distinct secrets before launch.");
}
logger.LogInformation("  JoinToken: JOIN_TOKEN_SECRET, {Count} key(s){Rotating}",
    joinKeyring.Count,
    joinKeyring.Count > 1 ? " -- rotation in progress, previous key(s) still accepted" : "");

// ── Metrics (OpenTelemetry -> Prometheus) ──

using var metrics = new GameMetrics(mapId);
await using var metricsEndpoint = MetricsEndpoint.TryStart(metricsAddr, metrics, serverId, logger);

// ── Game content (items, and whatever content types follow) ──
//
// Loaded and validated BEFORE the listener opens. A server that cannot vouch for its
// content refuses to start rather than serving an unknowable subset of the intended
// game: every downstream symptom — a missing item, a wrong stat, a loot table pointing
// at nothing — would otherwise be attributed to whichever system noticed first instead
// of to the file that was wrong.
//
// When nothing is configured the directory is found by walking up from the BINARY, not
// from the working directory. A relative default is resolved against the working
// directory, which belongs to whoever launched the process rather than to the deployment:
// it works under `dotnet run` from the module directory and fails everywhere else. It
// failed every integration test on first contact, because those launch the server from
// their own directory.
string? contentDir = GetArg(args, "--content-dir") ?? Env("CONTENT_DIR")
    ?? ContentLoader.ResolveDefaultDirectory();
LoadedContent content;
if (contentDir == null)
{
    logger.LogCritical(
        "No content directory was configured and none was found by searching upward from {Base}. " +
        "Set --content-dir or CONTENT_DIR to the directory holding {File}.",
        AppContext.BaseDirectory, ContentLoader.ItemsFileName);
    return 1;
}

try
{
    content = ContentLoader.Load(contentDir);
    logger.LogInformation(
        "Content loaded from {Dir}: {Items} items, hash {Hash}",
        contentDir, content.Database.ItemCount, content.Hash);
}
catch (ContentLoadException ex)
{
    // The message already enumerates every problem found, so it is logged as-is rather
    // than wrapped: re-describing it here would push the detail an author needs down
    // below a summary that says less.
    logger.LogCritical("{Message}", ex.Message);
    return 1;
}

// ── Player store (postgres when GAME_DB_URL is set, otherwise in-memory) ──

IPlayerStore playerStore = new MemoryPlayerStore();
PostgresPlayerStore? postgresStore = null;

if (!string.IsNullOrWhiteSpace(gameDbUrl))
{
    // Fail fast: a configured-but-unreachable database must not silently degrade
    // to a memory store, which would lose player state without any signal.
    try
    {
        postgresStore = await PostgresPlayerStore.ConnectAsync(gameDbUrl, CancellationToken.None);
        await postgresStore.MigrateAsync(CancellationToken.None);
        playerStore = postgresStore;
        logger.LogInformation("using postgres player store ({Dsn})", PostgresPlayerStore.MaskDsn(gameDbUrl));
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "postgres player store unavailable ({Dsn}) -- refusing to start",
            PostgresPlayerStore.MaskDsn(gameDbUrl));
        if (postgresStore is not null) await postgresStore.DisposeAsync();
        return 1;
    }
}
else
{
    logger.LogInformation("using in-memory player store (GAME_DB_URL unset -- state is lost on restart)");
}

// ── Server registry (self-registration + heartbeat) ──
//
// Connecting never blocks startup and never fails it: the multiplexer is built with
// AbortOnConnectFail=false, so a server booting while Redis is down still comes up
// and registers itself as soon as Redis is reachable.

RedisServerRegistry? serverRegistry = null;
RegistrationOptions? registrationOptions = null;
if (!string.IsNullOrWhiteSpace(redisAddr))
{
    try
    {
        serverRegistry = await RedisServerRegistry.ConnectAsync(
            redisAddr, redisPassword, RegistryDefaults.HeartbeatTtl,
            loggerFactory.CreateLogger<RedisServerRegistry>());
        registrationOptions = new RegistrationOptions
        {
            ServerId = serverId,
            MapId = mapId,
            PublicAddr = publicAddr,
            Transport = transport,
            Capacity = capacity,
            Ttl = RegistryDefaults.HeartbeatTtl
        };
    }
    catch (Exception ex)
    {
        // Deliberately non-fatal: a registry outage must not take the map offline for
        // players already connected, and the heartbeat loop cannot retry a connection
        // that was never created — so log loudly and run unregistered.
        logger.LogError(ex,
            "Could not connect to Redis at {Addr}; running WITHOUT self-registration, " +
            "so the gateway will not hand any client to this server", redisAddr);
    }
}

// ── Event stream (cross-server events, ADR-5) ──
//
// Redis-backed when REDIS_ADDR is configured — the same selector as the registry above,
// because it is the same Redis (deploy manifests pass one REDIS_ADDR/REDIS_PASSWORD pair
// to the gameserver container). Noop otherwise. The stream holds its OWN multiplexer
// rather than sharing the registry's: the registry deregisters during host drain while
// the event stream flushes after it, and independent lifetimes cost one idle connection.
// Like the registry, a failure here is deliberately non-fatal: events are telemetry/feed
// (the authoritative reward path is the kill batcher), so the map must not stay offline
// for want of them.

RedisEventStream? redisEventStream = null;
if (!string.IsNullOrWhiteSpace(redisAddr))
{
    try
    {
        redisEventStream = await RedisEventStream.ConnectAsync(
            redisAddr, redisPassword,
            loggerFactory.CreateLogger<RedisEventStream>(), metrics);
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Could not build the Redis event stream for {Addr}; falling back to Noop, " +
            "so cross-server events from this server are DISCARDED", redisAddr);
    }
}

// ── Agones SDK ──

// ADR-14 decision 1: the real client speaks the sidecar's HTTP interface, not gRPC, so the
// module keeps its NativeAOT and no-external-dependencies rules. Off by default and
// selected only by --agones / AGONES_ENABLED=true; the no-op branch is byte-for-byte the
// behaviour that shipped before, including no health loop (the host skips it when the SDK
// reports IsEnabled == false).
IAgonesSdk agonesSdk = useAgones
    ? new HttpAgonesSdk(loggerFactory.CreateLogger<HttpAgonesSdk>(), $"http://localhost:{agonesPort}/")
    : new NoopAgonesSdk();

// ── Build server options ──

var options = new ServerOptions
{
    ServerAddr = addr,
    Transport = transport,
    TransportKey = transportKey,
    ServerId = serverId,
    MapId = mapId,
    Mode = mode,
    TickRate = tickRate,
    SimulationRates = simulationRates,
    KeyframeInterval = keyframeInterval,
    GatherWorkers = gatherWorkers,
    MapBounds = MapBounds.FromSize(mapWidth, mapHeight),
    Capacity = capacity,
    JwtSecret = jwtSecret,
    JoinTokenSecret = joinTokenSecret,
    HoldTtl = mode == "dungeon" ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(30),
    SaveInterval = TimeSpan.FromSeconds(30),
    PlayerStore = playerStore,
    AgonesSdk = agonesSdk,
    AdvertiseHost = advertiseHost,
    RegisterOnAllocated = registerOnAllocated,
    // Redis Streams when REDIS_ADDR is configured (RedisEventStream XADDs into
    // `events:game`, the stream the gateway's relay consumes — ADR-5); Noop
    // otherwise, and cross-server events are generated (entity_killed) and then
    // discarded.
    EventStream = (IEventStream?)redisEventStream ?? new NoopEventStream(),
    LoggerFactory = loggerFactory,
    Metrics = metrics,
    ServerRegistry = serverRegistry,
    Registration = registrationOptions,
    // The composition root decides what the game is. The core host only knows it has
    // a phase to tick; see ISimulationPhase.
    SimulationPhaseFactory = loadTestEntities > 0
        ? (world, loggerFactory, onGroupRan) => new GameServer.Scaffolding.LoadTestSpawner(world, simulationRates, loadTestEntities, loggerFactory.CreateLogger<GameServer.Scaffolding.LoadTestSpawner>(), onGroupRan)
        : enableEnemySpawner
            ? (world, loggerFactory, onGroupRan) => new EnemySpawner(world, simulationRates, loggerFactory.CreateLogger<EnemySpawner>(), onGroupRan)
            : null,
    // The composition root is the one place allowed to know what the game is, so it is
    // where the status endpoint's entity count comes from. The JSON field stays
    // `enemies_alive` — the Unity DOTS sample polls /status and reads it.
    StatusEntityCount = (loadTestEntities > 0 || enableEnemySpawner)
        ? static world => world.CountWith<GameServer.World.Components.EnemyAi>()
        : null,
    NakamaUrl = nakamaUrl,
    NakamaHttpKey = nakamaHttpKey
};

// ── Graceful shutdown on SIGINT / SIGTERM ──

using var cts = new CancellationTokenSource();

// SIGTERM is the signal that actually matters: it is what Docker, Kubernetes and
// an Agones drain send. It used to be handled through AppDomain.ProcessExit, which
// cancels the token but does NOT wait for Main to unwind — the runtime terminates
// the process while shutdown is still in flight, so on SIGTERM the final save never
// ran and (now) the registry entry was never removed. Only SIGINT (Ctrl-C, via
// Console.CancelKeyPress) shut down properly, which is the one signal production
// never sends.
//
// PosixSignalRegistration handles both, and setting Cancel = true suppresses the
// runtime's default terminate-now behaviour so Main really does get to finish:
// drain connections, deregister, final save, then exit. It is NativeAOT-safe.
void RequestShutdown(PosixSignalContext ctx)
{
    ctx.Cancel = true; // we own the shutdown; do not let the runtime kill us mid-drain
    logger.LogInformation("Received {Signal}, shutting down...", ctx.Signal);
    cts.Cancel();
}

using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestShutdown);
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestShutdown);

// ── Run ──

var server = new GameServerHost(options);

// ── Duplicate-login kick consumer (events:kick, ADR-5) ──
//
// The other half of the gateway's supersede publish: without it a user who logs in
// again keeps their old game connection alive here forever. Same REDIS_ADDR selector
// and same non-fatal failure policy as the registry and event stream above — a map
// must not stay offline for want of the kick path, but the failure is loud because
// running without it silently re-opens the two-live-logins gap.
RedisKickConsumer? kickConsumer = null;
if (!string.IsNullOrWhiteSpace(redisAddr))
{
    try
    {
        kickConsumer = await RedisKickConsumer.ConnectAsync(
            redisAddr, redisPassword, serverId,
            p => server.KickPlayerAsync(p.UserId, p.Jti),
            loggerFactory.CreateLogger<RedisKickConsumer>());
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Could not start the duplicate-login kick consumer for {Addr}; supersede " +
            "events for this server will NOT be acted on (a re-logging-in user keeps " +
            "their old connection here)", redisAddr);
    }
}
// Monotonic, not DateTime.UtcNow. `current_tick / uptime_seconds` is how an observer
// derives an achieved tick rate, and the tick loop is paced by Stopwatch (CLOCK_MONOTONIC);
// dividing it by a wall-clock duration measures the host's clock drift as well as the
// server. On this box that drift is 10-17% and it already produced one filed-and-closed
// non-defect (#147, diagnosed in #153). Both terms of that quotient now come from the same
// clock. See ServerStatus.UptimeSeconds.
var uptime = System.Diagnostics.Stopwatch.StartNew();

// Serve the content set clients need. The canonical bytes are handed over verbatim —
// see SetContentProvider for why this is not a re-serialisation.
metricsEndpoint?.SetContentProvider(() => (content.CanonicalBytes, content.Hash));

// Wire up the /status JSON endpoint with live server state.
metricsEndpoint?.SetStatusProvider(() =>
{
    // Rates come from the RESOLVED SimulationRates, never from the legacy `tickRate`
    // scalar. `tickRate` is `--tick-rate` / GAMESERVER_TICK_RATE, which no deployment
    // sets, so reading it here reported the compiled-in default forever regardless of
    // what the server was running — #144.
    var status = new ServerStatus
    {
        Ok = true,
        // MEASURED, from Stopwatch. Published alongside the configured rates precisely so
        // that no reader has to divide current_tick by uptime_seconds to get one — that
        // arithmetic mixed a monotonic counter with a wall clock and produced #147.
        AchievedTickHz = server.AchievedTickHz,
        CurrentTick = server.CurrentTick,
        PlayersOnline = metrics.PlayersOnline,
        Capacity = capacity,
        Entities = server.EntityCount,
        EnemiesAlive = server.EnemiesAlive,
        AttacksReceived = server.AttackStats.Received,
        AttacksUnresolved = server.AttackStats.Unresolved,
        AttacksRejected = server.AttackStats.Rejected,
        AttacksAccepted = server.AttackStats.Accepted,
        AttackKills = server.AttackStats.Kills,
        LastAttackRejection = server.AttackStats.LastRejection,
        Redis = serverRegistry != null ? "connected" : "disconnected",
        EventStream = redisEventStream != null ? "redis" : "noop",
        EventsDropped = redisEventStream?.Dropped ?? 0,
        EventPublishFailures = redisEventStream?.PublishFailures ?? 0,
        KickConsumer = kickConsumer != null ? "redis" : "disabled",
        PlayersKicked = metrics.PlayersKicked,
        Postgres = postgresStore != null ? "connected" : "disconnected",
        UptimeSeconds = (long)uptime.Elapsed.TotalSeconds
    };
    status.ApplyRates(simulationRates);
    return status;
});

try
{
    await server.RunAsync(addr, cts.Token);
}
catch (OperationCanceledException)
{
    // Normal shutdown
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Game server crashed");
    Environment.ExitCode = 1;
}
finally
{
    // The kick consumer first: its handler calls into the server, so no supersede
    // event may land mid-teardown. Graceful dispose also destroys this server's
    // consumer group in Redis (see RedisKickConsumer).
    if (kickConsumer is not null) await kickConsumer.DisposeAsync();
    // Order matters: drain the server (final save) before closing the DB pool — and before
    // disposing the Agones client, whose HttpClient the drain's ShutdownAsync still needs.
    await server.DisposeAsync();
    // After the server: its dispose drains EventPublisher's queue INTO the event
    // stream's queue, and this flushes that queue to Redis (bounded window).
    if (redisEventStream is not null) await redisEventStream.DisposeAsync();
    if (agonesSdk is IDisposable disposableAgones) disposableAgones.Dispose();
    if (postgresStore is not null) await postgresStore.DisposeAsync();
}

logger.LogInformation("GameServer .NET exited");
return Environment.ExitCode;

// ── Helpers ──

/// <summary>
/// Read <c>--name value</c> or <c>--name=value</c> from the command line.
///
/// <para>The <c>=</c> form used to be dropped silently: the loop only compared
/// <c>args[i] == name</c> and took the next element, so <c>--map-id=map_02</c> matched
/// nothing and the server started on the default map with no warning. That is not
/// hypothetical — <c>deploy/agones/fleet-map-dotnet-dev.yaml</c> passes
/// <c>--mode=map --map-id=map_01 --addr=:9000</c>, all three of which have been ignored
/// for as long as the manifest has existed. It went unnoticed only because those values
/// happen to equal the defaults; the first fleet with a non-default map would have
/// silently served the wrong one.</para>
/// </summary>
static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == name)
        {
            return i + 1 < args.Length ? args[i + 1] : null;
        }

        if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
        {
            return args[i][(name.Length + 1)..];
        }
    }
    return null;
}

static bool HasFlag(string[] args, string name)
{
    return args.Contains(name);
}

static string? Env(string name)
{
    var val = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrEmpty(val) ? null : val;
}

/// <summary>
/// True when <paramref name="addr"/> carries no usable host part, i.e. it is a
/// listen-style address rather than something a client can dial. The host list
/// matches the Go reference client's NormalizeDialAddr
/// (backend/smoketest/smoke/helpers.go:231-241), so both sides agree on which
/// addresses are "listen-style".
/// </summary>
static bool IsHostlessAddr(string addr)
{
    int i = addr.LastIndexOf(':');
    if (i < 0) return false; // no port at all -- not a listen-style address
    var host = addr[..i].Trim('[', ']');
    return host is "" or "0.0.0.0" or "::";
}

/// <summary>
/// Port part of a host:port pair, or the whole string when it has no colon.
/// Used only to build a corrective suggestion in log messages.
/// </summary>
static string AddrPort(string addr)
{
    int i = addr.LastIndexOf(':');
    return i < 0 ? addr : addr[(i + 1)..];
}
