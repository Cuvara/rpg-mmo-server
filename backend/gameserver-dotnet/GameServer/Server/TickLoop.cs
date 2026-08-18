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
    private readonly SimulationRates _rates;
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

    /// <summary>
    /// Measures the rate the base timeline is actually advancing at. Fed from the loop
    /// below with the same <see cref="Stopwatch"/> timestamps that pace it, so the achieved
    /// rate and the schedule it is measuring cannot disagree about what a second is.
    /// </summary>
    private readonly AchievedRateMeter _rateMeter = new();

    /// <summary>
    /// The measured base-tick rate in Hz over the last completed window, or 0 for the first
    /// window of process life. Published so that nobody has to derive a rate by dividing a
    /// tick counter by an uptime — the arithmetic that produced #147. See
    /// <see cref="AchievedRateMeter"/>.
    /// </summary>
    public double AchievedTickHz => _rateMeter.AchievedHz;


    /// <summary>
    /// Single-rate construction: every group runs at <paramref name="tickRate"/>. This is
    /// the pre-multi-rate loop exactly — one timeline, snapshots every tick — and is what
    /// the existing tick-sensitive tests build.
    /// </summary>
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
        : this(world, handler, connections, SimulationRates.Uniform(tickRate), aoiRadius,
               logger, metrics, keyframeInterval, simulationPhase)
    {
    }

    public TickLoop(
        EcsWorld world,
        InputHandler handler,
        ConnectionManager connections,
        SimulationRates rates,
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
        _rates = rates;
        _aoiRadius = aoiRadius;
        _logger = logger;
        _metrics = metrics;
        _keyframeInterval = keyframeInterval;
        _gatherViews = GatherViews;
    }

    /// <summary>The rate configuration this loop runs.</summary>
    public SimulationRates Rates => _rates;

    /// <summary>
    /// Whether the world group — and therefore the snapshot broadcast — is due on the
    /// current base tick. Diagnostics and tests.
    /// </summary>
    public bool WorldDueNow => _rates.RunsOn(SimulationGroup.World, _currentTick);

    /// <summary>
    /// Phase A of the broadcast: read every viewer's anchor and AOI out of the world,
    /// into buffers the connections own. Runs inside one world read scope.
    /// </summary>
    private void GatherViews(GameServer.World.WorldReader reader)
    {
        for (int i = 0; i < _viewerCount; i++)
        {
            _viewers[i].GatherSnapshotView(reader, _aoiRadius, _currentTick, _keyframeInterval);
        }
    }

    /// <summary>
    /// How far behind schedule the loop may fall before it gives up on the backlog,
    /// resynchronises to now, and counts the ticks it dropped.
    ///
    /// <para>The alternative — running the missed ticks back to back — is the unbounded
    /// catch-up loop: each catch-up tick costs more than the budget it is trying to
    /// reclaim, so a server that falls behind falls further behind, and the mechanism meant
    /// to preserve real-time pacing is what stops it recovering. This loop drops the lost
    /// time instead. Simulation time then runs slower than wall time under sustained
    /// overload, which is a visible, bounded, measurable failure rather than a spiral.</para>
    /// </summary>
    private const int MaxLagTicks = 8;

    /// <summary>Run the tick loop until cancellation.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // Period in Stopwatch ticks, not integer milliseconds. 1000/60 truncates to 16ms,
        // which is a 4% fast clock — 2.4 extra ticks a second, and every duration expressed
        // in ticks silently short by the same factor. The old loop could afford the
        // truncation at 15Hz (1000/15 is exact); the base rate cannot.
        long periodTicks = Stopwatch.Frequency / _rates.BaseHz;
        double periodMs = 1000.0 / _rates.BaseHz;

        _logger.LogInformation(
            "Tick loop starting: base {BaseHz}Hz ({PeriodMs:F2}ms budget), {Rates}",
            _rates.BaseHz, periodMs, _rates);

        long nextDeadline = Stopwatch.GetTimestamp() + periodTicks;

        while (!ct.IsCancellationRequested)
        {
            long tickStart = Stopwatch.GetTimestamp();

            TickOnce();

            long now = Stopwatch.GetTimestamp();
            // Monotonic in, monotonic out. `now` is the same clock that sets the deadline
            // this loop is paced by, which is the entire point of measuring here rather
            // than letting an observer time the loop from outside with its own.
            _rateMeter.Sample(_currentTick, now);
            double elapsedMs = (now - tickStart) * 1000.0 / Stopwatch.Frequency;

            if (elapsedMs > periodMs)
            {
                _metrics?.RecordTickOverrun();
                if (elapsedMs > periodMs * 2)
                {
                    _logger.LogWarning(
                        "Tick {Tick} overran the base budget: {Elapsed:F1}ms > {Budget:F2}ms",
                        _currentTick, elapsedMs, periodMs);
                }
            }

            long remaining = nextDeadline - now;
            if (remaining > 0)
            {
                int delayMs = (int)(remaining * 1000 / Stopwatch.Frequency);
                if (delayMs > 0)
                {
                    try { await Task.Delay(delayMs, ct); }
                    catch (OperationCanceledException) { break; }
                }
                nextDeadline += periodTicks;
                continue;
            }

            // Behind schedule. Advance the deadline without sleeping, so a single slow tick
            // is absorbed by the next few. Past MaxLagTicks the backlog is unrecoverable
            // and is dropped in one step rather than chased.
            long lagTicks = (-remaining) / periodTicks;
            if (lagTicks >= MaxLagTicks)
            {
                _metrics?.RecordTickBacklogDropped(lagTicks);
                _logger.LogWarning(
                    "Simulation is {Lag} base ticks behind wall clock; dropping the backlog " +
                    "and resynchronising. Simulation time is now behind real time.",
                    lagTicks);
                nextDeadline = now + periodTicks;
            }
            else
            {
                nextDeadline += periodTicks;
            }
        }

        _logger.LogInformation("Tick loop stopped at base tick {Tick}", _currentTick);
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

        // ── Critical group ────────────────────────────────────────────────────────
        //
        // Input, movement and combat, every base tick. It is timed here rather than
        // through the schedule's per-group observer because this work is not yet a
        // declared IEcsSystem — it runs in the loop body. Recording it under the same
        // `group="critical"` label as a declared system would is what keeps the metric
        // honest: the group's cost is reported whether or not its work has been
        // system-ified yet, so the dashboard does not silently lose the busiest group.
        long criticalStart = Stopwatch.GetTimestamp();

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

                // Same scope: players who sent nothing this tick still advance on their
                // held direction. Inside the input scope rather than in one of its own so
                // the whole critical group is one write lock per base tick.
                _handler.ApplyHeldMovement(writer, _currentTick, _rates.WorldEvery);
            });
        }
        else if (_rates.WorldEvery > 1)
        {
            // No packets arrived at all this tick — common at 60Hz base with clients
            // sending at 10-15Hz — but held directions still have to be integrated.
            _world.UpdateComponents(writer =>
                _handler.ApplyHeldMovement(writer, _currentTick, _rates.WorldEvery));
        }

        // Enemy AI: spawn, move, reap — three systems in EnemyAiPhase order, sharing one
        // world write scope. Despawns now land inside that scope via the deferred
        // structural phase, so the old dance of collecting ids into PendingRemovals and
        // draining them after the lock was released is gone. Either way the removals are
        // applied before the snapshot broadcast below, which is what stops a client from
        // ever seeing an enemy inside the despawn zone.
        _metrics?.RecordGroupRun(SimulationGroup.Critical, criticalStart, Stopwatch.GetTimestamp());

        // The phase runs the declared systems of whichever groups are due on this base
        // tick, in Critical -> World -> Background order. It no-ops on a tick where none
        // are due, so at 60/15/5 three ticks in four never take the world write lock here.
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
        // ── Replication cadence is the WORLD rate, not the base rate ───────────────
        //
        // Simulating at 60Hz does not mean replicating at 60Hz, and the two must not be
        // allowed to become the same number by accident. Sending every base tick would
        // quadruple outbound bandwidth per client — the measured 45.9 KB/s at 200 players
        // (BENCHMARK.md) would leave the < 50 KB/s mobile budget ADR-7 sets — to deliver
        // state the client interpolates across anyway. It would also silently redefine the
        // keyframe interval, which counts snapshots: 30 snapshots is 2 seconds at 15Hz and
        // 0.5s at 60Hz, so the same constant would quadruple full-keyframe bandwidth on top.
        //
        // Gating here keeps both properties: snapshots ship at the world rate, and the
        // keyframe interval keeps meaning what its comment says.
        if (!_rates.RunsOn(SimulationGroup.World, _currentTick))
        {
            if (_metrics != null)
            {
                _metrics.RecordProcessedInputs(inputs.Count);
                _metrics.RecordTickDuration(startTimestamp, Stopwatch.GetTimestamp());
            }
            return;
        }

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

            // Phase B is gone from the tick. Gathering already staged each viewer's
            // snapshot on its own connection and signalled its write task, which encodes
            // and serializes there — see Connection.GatherSnapshotView. The tick thread's
            // whole share of a snapshot is now the gather plus a flag.
            //
            // Ordering is structural, not disciplinary: each connection has one write
            // task reading one channel, so tick N+1's frame cannot overtake tick N's.
            // Back-pressure coalesces to the newest staged snapshot and loses nothing,
            // because encoding is lazy and the delta is computed against the last
            // snapshot actually sent.
            _snapshotsThisTick = _viewerCount;

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
