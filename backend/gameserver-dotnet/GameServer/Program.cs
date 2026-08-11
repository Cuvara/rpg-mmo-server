using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Agones;
using GameServer.Events;
using GameServer.Observability;
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
int tickRate = int.TryParse(GetArg(args, "--tick-rate") ?? Env("GAMESERVER_TICK_RATE"), out var tr) ? tr : 15;
// Delta snapshots between full keyframes. 0 or less = send a full snapshot every tick
// (pre-delta behaviour), the escape hatch for a client that cannot merge deltas.
int keyframeInterval = int.TryParse(GetArg(args, "--keyframe-interval") ?? Env("GAMESERVER_KEYFRAME_INTERVAL"), out var kf)
    ? kf : GameConstants.DefaultKeyframeInterval;
float mapWidth = float.TryParse(GetArg(args, "--map-width") ?? Env("GAMESERVER_MAP_WIDTH"),
    System.Globalization.CultureInfo.InvariantCulture, out var mw) && mw > 0f
    ? mw : GameConstants.DefaultMapWidth;
float mapHeight = float.TryParse(GetArg(args, "--map-height") ?? Env("GAMESERVER_MAP_HEIGHT"),
    System.Globalization.CultureInfo.InvariantCulture, out var mh) && mh > 0f
    ? mh : GameConstants.DefaultMapHeight;
bool useAgones = HasFlag(args, "--agones") || Env("AGONES_ENABLED") == "true";
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

// ── Logging ──

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger("Program");

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
logger.LogInformation("  TickRate:  {TickRate}Hz", tickRate);
logger.LogInformation("  Snapshots: {Mode}", keyframeInterval > 0
    ? $"delta, keyframe every {keyframeInterval} snapshots"
    : "full every tick (delta disabled)");
logger.LogInformation("  MapSize:   {Width}x{Height} world units (centered on origin)", mapWidth, mapHeight);
logger.LogInformation("  Agones:    {Agones}", useAgones);
if (useAgones)
{
    logger.LogWarning("--agones/AGONES_ENABLED is set but has NO effect: the C# server " +
                      "still uses the no-op Agones SDK (no Ready/Health/Shutdown is reported " +
                      "to the sidecar). Do not rely on Agones health checks for this server yet.");
}
logger.LogInformation("  Metrics:   {Metrics}", string.IsNullOrWhiteSpace(metricsAddr) ? "disabled" : metricsAddr);
logger.LogInformation("  GameDB:    {GameDb}",
    string.IsNullOrWhiteSpace(gameDbUrl) ? "memory" : PostgresPlayerStore.MaskDsn(gameDbUrl));
logger.LogInformation("  Registry:  {Registry}",
    string.IsNullOrWhiteSpace(redisAddr)
        ? "disabled (REDIS_ADDR unset -- the gateway will NOT find this server)"
        : $"redis {redisAddr}, advertising '{publicAddr}' ({transport})");
if (!string.IsNullOrWhiteSpace(redisAddr) && publicAddr == addr && addr.StartsWith(':'))
{
    logger.LogInformation(
        "  GAMESERVER_PUBLIC_ADDR is unset, so clients will be handed the listen address '{Addr}'. " +
        "That is correct for host deployments; in containers with a published port, set it to " +
        "<host>:<published-port> or clients will fail to connect.", addr);
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
    KeyframeInterval = keyframeInterval,
    MapBounds = MapBounds.FromSize(mapWidth, mapHeight),
    Capacity = capacity,
    JwtSecret = jwtSecret,
    JoinTokenSecret = joinTokenSecret,
    HoldTtl = mode == "dungeon" ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(30),
    SaveInterval = TimeSpan.FromSeconds(30),
    PlayerStore = playerStore,
    // Both branches are intentionally Noop: no real Agones SDK client exists for
    // the C# server yet, so --agones/AGONES_ENABLED currently changes nothing.
    // The flag is kept so deployment manifests do not have to change when the
    // real SDK lands. See backend/docs/ARCHITECTURE-DECISIONS.md, ADR-6.
    AgonesSdk = new NoopAgonesSdk(),
    // Always Noop: the C# server has no Redis client, so cross-server events are
    // generated (entity_killed) and then discarded. See ADR-5.
    EventStream = new NoopEventStream(),
    LoggerFactory = loggerFactory,
    Metrics = metrics,
    ServerRegistry = serverRegistry,
    Registration = registrationOptions
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
    // Order matters: drain the server (final save) before closing the DB pool.
    await server.DisposeAsync();
    if (postgresStore is not null) await postgresStore.DisposeAsync();
}

logger.LogInformation("GameServer .NET exited");
return Environment.ExitCode;

// ── Helpers ──

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name) return args[i + 1];
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
