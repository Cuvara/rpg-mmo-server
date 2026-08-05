using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Agones;
using GameServer.Events;
using GameServer.Input;
using GameServer.Net;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Snapshot;
using GameServer.World;

namespace GameServer.Server;

/// <summary>Server configuration options.</summary>
public class ServerOptions
{
    public string ServerAddr { get; set; } = ":9000";
    public string ServerId { get; set; } = "";
    public string MapId { get; set; } = "map_01";
    public string Mode { get; set; } = "map"; // "map" or "dungeon"
    public int TickRate { get; set; } = GameConstants.DefaultTickRate;
    public int Capacity { get; set; } = 100;
    public string JwtSecret { get; set; } = "";
    /// <summary>Play area for this map. Movement is clamped into these bounds.</summary>
    public MapBounds MapBounds { get; set; } = MapBounds.Default;

    public TimeSpan HoldTtl { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SaveInterval { get; set; } = TimeSpan.FromSeconds(30);
    public IPlayerStore? PlayerStore { get; set; }
    public IAgonesSdk? AgonesSdk { get; set; }
    public IEventStream? EventStream { get; set; }
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Optional metric instrument set. When null the server runs uninstrumented.</summary>
    public GameMetrics? Metrics { get; set; }
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
    private readonly GameWorld _world;
    private readonly ConnectionManager _connections;
    private readonly TickLoop _tickLoop;
    private readonly AsyncSaver _saver;
    private readonly InputHandler _inputHandler;
    private readonly IPlayerStore _playerStore;
    private readonly IAgonesSdk _agonesSdk;
    private readonly EventPublisher? _publisher;
    private readonly GameMetrics? _metrics;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Entity hold timers for reconnect (user ID -> hold CTS).</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _holds = new();

    public GameServerHost(ServerOptions options)
    {
        _options = options;
        _loggerFactory = options.LoggerFactory ?? Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            b.AddConsole().SetMinimumLevel(LogLevel.Information));
        _logger = _loggerFactory.CreateLogger<GameServerHost>();

        _metrics = options.Metrics;
        _world = new GameWorld();
        _metrics?.SetEntityCountProvider(() => _world.EntityCount);
        _connections = new ConnectionManager();
        _playerStore = options.PlayerStore ?? new MemoryPlayerStore();
        _agonesSdk = options.AgonesSdk ?? new NoopAgonesSdk();

        var eventStream = options.EventStream ?? new NoopEventStream();
        _publisher = new EventPublisher(eventStream, _loggerFactory.CreateLogger<EventPublisher>(), _metrics);

        _inputHandler = new InputHandler(
            _world,
            _loggerFactory.CreateLogger<InputHandler>(),
            OnEntityDeath,
            options.TickRate,
            options.MapBounds);

        _tickLoop = new TickLoop(
            _world,
            _inputHandler,
            _connections,
            options.TickRate,
            GameConstants.DefaultAoiRadius,
            _loggerFactory.CreateLogger<TickLoop>(),
            _metrics);

        _saver = new AsyncSaver(
            _playerStore,
            _world,
            options.MapId,
            options.SaveInterval,
            _loggerFactory.CreateLogger<AsyncSaver>(),
            _metrics);
    }

    /// <summary>Start the server and listen for connections.</summary>
    public async Task RunAsync(string addr, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Parse address
        var (host, port) = ParseAddr(addr);
        _listener = new TcpListener(
            string.IsNullOrEmpty(host) ? IPAddress.Any : IPAddress.Parse(host),
            port);
        _listener.Start();

        // Log the actual bound address (important when port=0 for ephemeral port allocation)
        var actualAddr = _listener.LocalEndpoint.ToString()!;
        _logger.LogInformation("Game server listening on {Addr} (mode={Mode}, map={MapId}, id={ServerId})",
            actualAddr, _options.Mode, _options.MapId, _options.ServerId);

        // Mark ready with Agones
        await _agonesSdk.ReadyAsync();

        // Start background tasks
        var tickTask = _tickLoop.RunAsync(_cts.Token);
        var saveTask = _saver.RunAsync(_cts.Token);
        var healthTask = AgonesHealthLoop.RunAsync(_agonesSdk, TimeSpan.FromSeconds(2), _cts.Token,
            _loggerFactory.CreateLogger("AgonesHealth"));

        // Accept loop
        var acceptTask = AcceptLoopAsync(_cts.Token);

        // Wait for any to complete (usually cancellation)
        await Task.WhenAny(tickTask, saveTask, acceptTask);

        // Shut down
        await ShutdownAsync();
    }

    /// <summary>Graceful shutdown: stop accepting, close connections, final save, agones shutdown.</summary>
    public async Task ShutdownAsync()
    {
        _logger.LogInformation("Shutting down game server...");

        _cts?.Cancel();

        try { _listener?.Stop(); } catch { /* ignore */ }

        // Cancel all entity holds
        foreach (var kvp in _holds)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _holds.Clear();

        _connections.CloseAll();

        // Final save
        await _saver.SaveAllAsync();

        // Agones shutdown
        await _agonesSdk.ShutdownAsync();

        _logger.LogInformation("Game server shutdown complete");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Accept error");
                continue;
            }

            // Handle connection on a background task (fire-and-forget)
            _ = Task.Run(() => HandleConnectionAsync(tcp, ct), ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        Connection? conn = null;
        try
        {
            // Use a temporary logger-only connection for the handshake
            var connLogger = _loggerFactory.CreateLogger<Connection>();
            var tempConn = new Connection("pending", tcp, connLogger);

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
            var claims = JwtValidator.Verify(joinReq.Token, _options.JwtSecret);
            if (claims == null)
            {
                await SendError(tempConn, "Invalid or expired token");
                tempConn.Close();
                return;
            }

            // Step 3: Check server ID claim
            if (!string.IsNullOrEmpty(_options.ServerId) &&
                !string.IsNullOrEmpty(claims.ServerId) &&
                claims.ServerId != _options.ServerId)
            {
                await SendError(tempConn, "Token is for a different server");
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

            string userId = claims.UserId;

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
                // Load from store or create new
                var saved = await _playerStore.LoadPlayerAsync(userId, ct);
                var entity = new EntityState
                {
                    Id = userId,
                    Type = "player",
                    // Clamp restored/spawn positions: a map may have been resized since
                    // the state was saved, and an out-of-bounds entity must not persist.
                    Position = _options.MapBounds.Clamp(new Vec2(saved?.X ?? 0, saved?.Y ?? 0)),
                    Hp = saved?.Hp ?? ServerDefaults.DefaultPlayerHp,
                    MaxHp = saved?.MaxHp ?? ServerDefaults.DefaultPlayerHp,
                    Speed = ServerDefaults.DefaultPlayerSpeed,
                    Attack = ServerDefaults.DefaultPlayerAttack,
                    Defense = ServerDefaults.DefaultPlayerDefense
                };
                _world.AddEntity(entity);
            }

            // Create the real connection with the verified user ID, reusing the same TcpClient
            conn = new Connection(userId, tcp, connLogger);

            // Register connection
            _connections.Add(conn);

            // Step 5: Send JoinTokenResp
            var resp = WireProtocol.NewEnvelope(MsgType.JoinTokenResp,
                new JoinTokenResponse { Ok = true, UserId = userId });
            await conn.WriteOneAsync(resp);

            _metrics?.PlayerJoined();
            _logger.LogInformation("Player {UserId} joined (total: {Count})", userId, _connections.Count);

            // Step 6: Start read/write loops
            var writeTask = conn.WriteLoopAsync();
            var readTask = conn.ReadLoopAsync(OnMessageReceived);

            await Task.WhenAny(readTask, writeTask);

            // Step 7: On disconnect, hold entity for reconnect window
            OnPlayerDisconnected(userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection handler error");
        }
        finally
        {
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

    private void OnPlayerDisconnected(string userId)
    {
        _connections.Remove(userId);
        _metrics?.PlayerLeft();

        var holdTtl = _options.Mode == "dungeon"
            ? TimeSpan.FromSeconds(60)
            : _options.HoldTtl;

        var holdCts = new CancellationTokenSource();
        _holds[userId] = holdCts;

        _logger.LogInformation("Player {UserId} disconnected, holding entity for {Ttl}",
            userId, holdTtl);

        // Fire-and-forget: remove entity after hold TTL unless reconnected
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(holdTtl, holdCts.Token);

                // Hold expired, remove entity
                _world.RemoveEntity(userId);
                _holds.TryRemove(userId, out _);
                _logger.LogInformation("Entity hold expired for {UserId}, entity removed", userId);
            }
            catch (OperationCanceledException)
            {
                // Reconnected before hold expired
            }
        });
    }

    private void OnEntityDeath(EntityState victim, EntityState killer)
    {
        if (_publisher == null) return;

        var payload = new DeathPayload(
            victim.Id, victim.Type, killer.Id, _options.MapId, _options.ServerId);

        // Fire-and-forget
        _ = _publisher.PublishDeathAsync("entity_killed", payload);
    }

    private static async Task SendError(Connection conn, string error)
    {
        var resp = WireProtocol.NewEnvelope(MsgType.JoinTokenResp,
            new JoinTokenResponse { Ok = false, Error = error });
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
        _world.Dispose();
    }
}
