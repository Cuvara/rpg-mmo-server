using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.AI;
using GameServer.Input;
using GameServer.Net;
using GameServer.Observability;
using GameServer.Snapshot;
using GameServer.World;

namespace GameServer.Server;

/// <summary>
/// Fixed-rate simulation tick loop. Drains inputs, processes them, then
/// broadcasts AOI-filtered snapshots to all connected players.
/// Port of Go server/tick.go.
/// <para>
/// Snapshots are delta-encoded per connection: each client gets a keyframe on join
/// and then only the entities whose visible state changed, plus explicit despawns.
/// Every snapshot carries <c>ack_tick</c> — that client's newest accepted input tick —
/// so the client can reconcile its prediction.
/// </para>
/// </summary>
public sealed class TickLoop
{
    private readonly EcsWorld _world;
    private readonly InputHandler _handler;
    private readonly ConnectionManager _connections;
    private readonly EnemySpawner? _enemySpawner;
    private readonly int _tickRate;
    private readonly float _aoiRadius;
    private readonly int _keyframeInterval;
    private readonly ILogger _logger;
    private readonly GameMetrics? _metrics;
    private ulong _currentTick;
    private int _snapshotsThisTick;

    /// <summary>
    /// Scratch map (entity -> index of the newest input in this tick's drained batch).
    /// Reused across ticks so input coalescing allocates nothing in the hot path.
    /// <para>
    /// Keyed by <see cref="EntityHandle"/> rather than by user id: the id was already
    /// resolved on the network thread at ingest, so grouping is an integer hash instead
    /// of a string hash per input on the simulation thread.
    /// </para>
    /// </summary>
    private readonly Dictionary<EntityHandle, int> _newestInputIndex = new();

    /// <summary>
    /// Scratch list the input queue drains into. Reused so the drain allocates nothing;
    /// it used to hand back a freshly built list every tick.
    /// </summary>
    private readonly List<PendingInput> _inputs = new();

    /// <summary>Current simulation tick.</summary>
    public ulong CurrentTick => _currentTick;

    /// <summary>Number of enemies currently alive, or 0 if the spawner is disabled.</summary>
    public int EnemiesAlive => _enemySpawner?.AliveCount ?? 0;

    public TickLoop(
        EcsWorld world,
        InputHandler handler,
        ConnectionManager connections,
        int tickRate,
        float aoiRadius,
        ILogger logger,
        GameMetrics? metrics = null,
        int keyframeInterval = GameConstants.DefaultKeyframeInterval,
        EnemySpawner? enemySpawner = null)
    {
        _world = world;
        _handler = handler;
        _connections = connections;
        _enemySpawner = enemySpawner;
        _tickRate = tickRate;
        _aoiRadius = aoiRadius;
        _logger = logger;
        _metrics = metrics;
        _keyframeInterval = keyframeInterval;
    }

    /// <summary>Run the tick loop until cancellation.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        int tickMs = 1000 / _tickRate;
        _logger.LogInformation("Tick loop starting: {TickRate}Hz ({TickMs}ms budget)", _tickRate, tickMs);

        var sw = new Stopwatch();

        while (!ct.IsCancellationRequested)
        {
            sw.Restart();

            TickOnce();

            sw.Stop();
            int elapsed = (int)sw.ElapsedMilliseconds;
            int remaining = tickMs - elapsed;

            if (remaining > 0)
            {
                try { await Task.Delay(remaining, ct); }
                catch (OperationCanceledException) { break; }
            }
            else if (elapsed > tickMs * 2)
            {
                _logger.LogWarning("Tick {Tick} overran budget: {Elapsed}ms > {Budget}ms",
                    _currentTick, elapsed, tickMs);
            }
        }

        _logger.LogInformation("Tick loop stopped at tick {Tick}", _currentTick);
    }

    /// <summary>Execute a single tick. Exposed for testing.</summary>
    public void TickOnce()
    {
        long startTimestamp = Stopwatch.GetTimestamp();

        _currentTick++;
        _snapshotsThisTick = 0;

        // Structural phase, before anything iterates. ADR-11 rules out Arch's
        // CommandBuffer (it throws under NativeAOT even with the array hints), so
        // spawns and despawns raised while a query was being iterated are applied
        // here instead — one explicit point in the tick, not a hidden side effect
        // of a lock release. Normally a no-op: the network threads' spawn/despawn
        // paths take the world write lock and so cannot overlap an iteration.
        _world.ApplyStructuralChanges();

        // Drain and process all pending inputs under one world Update. The drain reuses
        // _inputs, and every entry already carries the entity handle resolved at ingest.
        var inputs = _inputs;
        _world.DrainInputs(inputs);

        if (inputs.Count > 0)
        {
            // Re-resolve only the handles that went stale since ingest (disconnect
            // inside the hold window). After this, every entry is either bound to a live
            // entity or provably addresses nothing, so the grouping below never needs
            // the user id.
            _world.RebindStale(inputs);

            // Coalesce movement: at most one integration step per player per tick, using
            // the newest input (highest client tick, arrival order as tiebreak). Without
            // this, a client that sends N input packets per tick would move N times as
            // far — the movement model is per-tick, not per-message.
            _newestInputIndex.Clear();
            for (int i = 0; i < inputs.Count; i++)
            {
                EntityHandle handle = inputs[i].Handle;
                if (!handle.IsValid) continue; // addresses nothing; dropped by the handler

                if (!_newestInputIndex.TryGetValue(handle, out int best) ||
                    inputs[i].Input.Tick >= inputs[best].Input.Tick)
                {
                    _newestInputIndex[handle] = i;
                }
            }

            // One component write scope for the whole batch, addressing entities by
            // handle. Nothing round-trips a whole EntityState through storage per input,
            // and the world's string index is not consulted at all.
            _world.UpdateComponents(writer =>
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var pi = inputs[i];
                    if (!pi.Handle.IsValid) continue;

                    bool applyMovement = _newestInputIndex[pi.Handle] == i;
                    _handler.ProcessInput(writer, in pi, _currentTick, applyMovement);
                }
            });
        }

        // Enemy AI: spawn, move, center-zone damage, remove dead
        if (_enemySpawner != null)
        {
            _world.Update((get, set) =>
            {
                _enemySpawner.Tick(get, set, _currentTick);
            });

            // Remove dead enemies outside the write lock (RemoveEntity takes
            // its own lock; calling it inside Update would deadlock).
            var removals = _enemySpawner.PendingRemovals;
            for (int i = 0; i < removals.Count; i++)
            {
                _world.RemoveEntity(removals[i]);
            }
        }

        // Broadcast snapshots to each connected player
        _connections.ForEach(conn =>
        {
            try
            {
                // AOI centre plus the per-player acknowledgement: the newest input tick
                // this player's own entity has accepted. Other players' entities never
                // contribute — reconciliation is strictly per-client. Two components,
                // read directly: the previous `View(get => ...)` form composed a whole
                // EntityState for these two fields and allocated a closure per
                // connection per tick to carry them out of the lambda.
                _world.TryGetSnapshotAnchor(conn.UserId, out Vec2 playerPos, out ulong ackTick);

                // Into the connection's reusable buffer. This used to be
                // `new List<EntityState>()` inside EcsWorld, once per connected client
                // per tick, plus its growth reallocations.
                ReadOnlySpan<EntityState> nearby = conn.ScanAoi(_world, playerPos, _aoiRadius);
                var snapshot = conn.DeltaState.Encode(_currentTick, ackTick, nearby, _keyframeInterval,
                    intern: conn.Encoding == WireEncoding.Proto);
                var env = WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, conn.Encoding);
                conn.Send(env);
                _snapshotsThisTick++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send snapshot to {UserId}", conn.UserId);
            }
        });

        // Metrics: recorded once per tick, no per-entity allocation.
        if (_metrics != null)
        {
            _metrics.RecordProcessedInputs(inputs.Count);
            _metrics.RecordSnapshotsSent(_snapshotsThisTick);
            _metrics.RecordTickDuration(startTimestamp, Stopwatch.GetTimestamp());
        }
    }
}
