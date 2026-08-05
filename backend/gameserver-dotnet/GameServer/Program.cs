using Microsoft.Extensions.Logging;
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
bool useAgones = HasFlag(args, "--agones") || Env("AGONES_ENABLED") == "true";
string jwtSecret = GetArg(args, "--jwt-secret") ?? Env("JWT_SECRET") ?? "";
// Metrics listen address. Unset -> ":9101"; explicitly empty -> metrics disabled.
string metricsAddr = GetArg(args, "--metrics-addr")
    ?? Environment.GetEnvironmentVariable("METRICS_ADDR")
    ?? ":9101";

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
logger.LogInformation("  Agones:    {Agones}", useAgones);
logger.LogInformation("  Metrics:   {Metrics}", string.IsNullOrWhiteSpace(metricsAddr) ? "disabled" : metricsAddr);

// ── Validate ──

if (string.IsNullOrEmpty(jwtSecret))
{
    logger.LogWarning("JWT_SECRET not set -- token validation will reject all tokens in production");
}

// ── Metrics (OpenTelemetry -> Prometheus) ──

using var metrics = new GameMetrics(mapId);
await using var metricsEndpoint = MetricsEndpoint.TryStart(metricsAddr, metrics, serverId, logger);

// ── Build server options ──

var options = new ServerOptions
{
    ServerAddr = addr,
    ServerId = serverId,
    MapId = mapId,
    Mode = mode,
    TickRate = tickRate,
    Capacity = capacity,
    JwtSecret = jwtSecret,
    HoldTtl = mode == "dungeon" ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(30),
    SaveInterval = TimeSpan.FromSeconds(30),
    PlayerStore = new MemoryPlayerStore(),
    AgonesSdk = useAgones ? new NoopAgonesSdk() : new NoopAgonesSdk(), // Real SDK added later
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

await using var server = new GameServerHost(options);

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

logger.LogInformation("GameServer .NET exited");

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
