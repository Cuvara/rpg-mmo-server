using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Agones;
using GameServer.Events;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Server;

// ── Parse command-line args and environment variables ──

string mode = GetArg(args, "--mode") ?? Env("GAMESERVER_MODE") ?? "map";
string addr = GetArg(args, "--addr") ?? Env("GAMESERVER_ADDR") ?? ":9000";
string mapId = GetArg(args, "--map-id") ?? Env("GAMESERVER_MAP_ID") ?? "map_01";
string serverId = GetArg(args, "--server-id") ?? Env("GAMESERVER_ID") ?? Env("POD_NAME") ?? $"gs-{Guid.NewGuid():N}"[..12];
int capacity = int.TryParse(GetArg(args, "--capacity") ?? Env("GAMESERVER_CAPACITY"), out var cap) ? cap : 100;
int tickRate = int.TryParse(GetArg(args, "--tick-rate") ?? Env("GAMESERVER_TICK_RATE"), out var tr) ? tr : 15;
float mapWidth = float.TryParse(GetArg(args, "--map-width") ?? Env("GAMESERVER_MAP_WIDTH"),
    System.Globalization.CultureInfo.InvariantCulture, out var mw) && mw > 0f
    ? mw : GameConstants.DefaultMapWidth;
float mapHeight = float.TryParse(GetArg(args, "--map-height") ?? Env("GAMESERVER_MAP_HEIGHT"),
    System.Globalization.CultureInfo.InvariantCulture, out var mh) && mh > 0f
    ? mh : GameConstants.DefaultMapHeight;
bool useAgones = HasFlag(args, "--agones") || Env("AGONES_ENABLED") == "true";
string jwtSecret = GetArg(args, "--jwt-secret") ?? Env("JWT_SECRET") ?? "";
// Metrics listen address. Unset -> ":9101"; explicitly empty -> metrics disabled.
string metricsAddr = GetArg(args, "--metrics-addr")
    ?? Environment.GetEnvironmentVariable("METRICS_ADDR")
    ?? ":9101";
// Game-state database DSN. Unset -> in-memory player store (state lost on restart).
string? gameDbUrl = GetArg(args, "--game-db-url") ?? Env("GAME_DB_URL");
// Migrate-only mode: apply pending schema migrations, then exit without listening.
// CD runs this before the deploy step so migrations happen at a deterministic point.
bool migrateOnly = HasFlag(args, "--migrate-only") || Env("GAMESERVER_MIGRATE_ONLY") == "true";

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
logger.LogInformation("  MapId:     {MapId}", mapId);
logger.LogInformation("  ServerId:  {ServerId}", serverId);
logger.LogInformation("  Capacity:  {Capacity}", capacity);
logger.LogInformation("  TickRate:  {TickRate}Hz", tickRate);
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

if (string.IsNullOrEmpty(jwtSecret))
{
    logger.LogWarning("JWT_SECRET not set -- token validation will reject all tokens in production");
}

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

// ── Build server options ──

var options = new ServerOptions
{
    ServerAddr = addr,
    ServerId = serverId,
    MapId = mapId,
    Mode = mode,
    TickRate = tickRate,
    MapBounds = MapBounds.FromSize(mapWidth, mapHeight),
    Capacity = capacity,
    JwtSecret = jwtSecret,
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
    Metrics = metrics
};

// ── Graceful shutdown on SIGINT / SIGTERM ──

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    logger.LogInformation("Received SIGINT, shutting down...");
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    logger.LogInformation("Received SIGTERM, shutting down...");
    cts.Cancel();
};

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
