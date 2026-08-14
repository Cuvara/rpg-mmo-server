using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
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
    private readonly ISimulationPhase? _simulationPhase;
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

    /// <summary>
    /// Connections to broadcast to this tick, refreshed once per tick and walked twice —
    /// once in the gather phase, once in the encode phase. Reused, so the broadcast
    /// allocates nothing; cleared after each broadcast so a dropped connection is not
    /// held alive by a stale slot.
    /// </summary>
    private Connection[] _viewers = Array.Empty<Connection>();

    private int _viewerCount;

    /// <summary>
    /// The gather callback, built once. An inline lambda captures <c>this</c> and
    /// allocates a delegate every tick; the old broadcast did exactly that.
    /// </summary>
    private readonly Action<GameServer.World.WorldReader> _gatherViews;

    /// <summary>Current simulation tick.</summary>
    public ulong CurrentTick => _currentTick;

    /// <summary>Number of enemies currently alive, or 0 if the spawner is disabled.</summary>
    public int SimulationEntityCount => _simulationPhase?.TrackedEntityCount ?? 0;

    public TickLoop(
        EcsWorld world,
        InputHandler handler,
        ConnectionManager connections,
        int tickRate,
        float aoiRadius,
        ILogger logger,
        GameMetrics? metrics = null,
        int keyframeInterval = GameConstants.DefaultKeyframeInterval,
        ISimulationPhase? simulationPhase = null)
    {
        _world = world;
        _handler = handler;
        _connections = connections;
        _simulationPhase = simulationPhase;
        _tickRate = tickRate;
        _aoiRadius = aoiRadius;
        _logger = logger;
        _metrics = metrics;
        _keyframeInterval = keyframeInterval;
        _gatherViews = GatherViews;
    }

    /// <summary>
    /// Phase A of the broadcast: read every viewer's anchor and AOI out of the world,
    /// into buffers the connections own. Runs inside one world read scope.
    /// </summary>
    private void GatherViews(GameServer.World.WorldReader reader)
    {
        for (int i = 0; i < _viewerCount; i++)
        {
            _viewers[i].GatherSnapshotView(reader, _aoiRadius);
        }
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

        // Enemy AI: spawn, move, reap — three systems in EnemyAiPhase order, sharing one
        // world write scope. Despawns now land inside that scope via the deferred
        // structural phase, so the old dance of collecting ids into PendingRemovals and
        // draining them after the lock was released is gone. Either way the removals are
        // applied before the snapshot broadcast below, which is what stops a client from
        // ever seeing an enemy inside the despawn zone.
        _simulationPhase?.Tick(_currentTick);

        // ── Snapshot broadcast, in two phases ──────────────────────────────────
        //
        // Phase A reads the world for every viewer under ONE read lock. Phase B encodes
        // and sends, touching no world state and holding no lock.
        //
        // The split is the point of this stage. Before it, each viewer took the read
        // lock twice — once for its anchor, once for its AOI scan — so a 200-player tick
        // acquired it 400 times, and serialization ran interleaved with world reads. Now
        // the boundary is explicit: after phase A every connection holds a self-contained
        // view, which is the property that would let phase B move off the tick thread
        // entirely. That move is NOT done here (see BENCHMARK.md section 9); this only
        // stops the tick's structure from being the thing that prevents it.
        int viewers = _connections.CopyTo(_viewers);
        if (viewers > _viewers.Length)
        {
            _viewers = new Connection[viewers];
            viewers = _connections.CopyTo(_viewers);
        }
        _viewerCount = Math.Min(viewers, _viewers.Length);

        if (_viewerCount > 0)
        {
            // Phase A — gather. One read lock for the whole broadcast.
            _world.ReadAll(_gatherViews);

            // Phase B — encode and send. No lock, no world access.
            for (int i = 0; i < _viewerCount; i++)
            {
                Connection conn = _viewers[i];
                try
                {
                    var snapshot = conn.DeltaState.Encode(
                        _currentTick, conn.SnapshotAckTick, conn.StagedAoi, _keyframeInterval,
                        intern: conn.Encoding == WireEncoding.Proto);
                    var env = WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, conn.Encoding);
                    conn.Send(env);
                    _snapshotsThisTick++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send snapshot to {UserId}", conn.UserId);
                }
            }

            // Released so a disconnected connection is not kept alive by the scratch
            // array until the next tick happens to overwrite that slot.
            Array.Clear(_viewers, 0, _viewerCount);
        }

        // Metrics: recorded once per tick, no per-entity allocation.
        if (_metrics != null)
        {
            _metrics.RecordProcessedInputs(inputs.Count);
            _metrics.RecordSnapshotsSent(_snapshotsThisTick);
            _metrics.RecordTickDuration(startTimestamp, Stopwatch.GetTimestamp());
        }
    }
}
