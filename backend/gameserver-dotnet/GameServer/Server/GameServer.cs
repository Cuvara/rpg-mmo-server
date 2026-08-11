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
    public int TickRate { get; set; } = GameConstants.DefaultTickRate;
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
    private readonly GameMetrics? _metrics;
    private readonly ILogger _logger;
    /// <summary>Keyring join tokens are verified against (JOIN_TOKEN_SECRET).</summary>
    private readonly JwtKeyring _joinKeys;
    /// <summary>Tracks consumed JTI values to prevent join token replay.</summary>
    private readonly JtiTracker _jtiTracker = new();
    private readonly ILoggerFactory _loggerFactory;

    private readonly RegistrationService? _registration;

    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>0 until the first ShutdownAsync caller wins the race, 1 afterwards.</summary>
    private int _shutdownStarted;

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
            _metrics,
            options.KeyframeInterval);

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
        _listener = TransportFactory.Listen(_options.Transport, addr, _options.TransportKey, _logger);

        // Log the actual bound address (important when port=0 for ephemeral port allocation)
        var actualAddr = _listener.LocalEndPoint;
        _logger.LogInformation(
            "Game server listening on {Addr} via {Transport} (mode={Mode}, map={MapId}, id={ServerId})",
            actualAddr, _listener.Kind, _options.Mode, _options.MapId, _options.ServerId);

        // Mark ready with Agones
        await _agonesSdk.ReadyAsync();

        // Publish ourselves into the server registry the gateway reads, and keep the
        // entry alive. Done after the listener is up so we never advertise an address
        // that is not accepting yet — and after the bind, so a port=0 ephemeral listen
        // advertises the port it actually got.
        if (_registration != null)
        {
            await _registration.StartAsync(_cts.Token);
        }

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

            // Step 5: Send JoinTokenResp
            var resp = WireProtocol.NewEnvelope(MsgType.JoinTokenResp,
                new JoinTokenResponse { Ok = true, UserId = userId }, conn.Encoding);
            await conn.WriteOneAsync(resp);

            _metrics?.PlayerJoined();
            // players_online is an independent counter, not derived from _connections,
            // so it must be balanced exactly once and only if it was incremented.
            countedOnline = true;
            // Fire-and-forget: the gateway's capacity view should be fresh, but a slow
            // or down Redis must never delay a player entering the world.
            _registration?.NotifyPlayerCountChanged();
            _logger.LogInformation("Player {UserId} joined (total: {Count})", userId, _connections.Count);

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
        if (_publisher == null) return;

        var payload = new DeathPayload(
            victim.Id, victim.Type, killer.Id, _options.MapId, _options.ServerId);

        // Fire-and-forget
        _ = _publisher.PublishDeathAsync("entity_killed", payload);
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
        // Disposed only here, once the run loop is guaranteed done with it — disposing
        // it inside ShutdownAsync would hand RunAsync's background tasks a dead token
        // source while they are still unwinding.
        _cts?.Dispose();
        _world.Dispose();
    }
}
