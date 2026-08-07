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
    private readonly GameWorld _world;
    private readonly InputHandler _handler;
    private readonly ConnectionManager _connections;
    private readonly int _tickRate;
    private readonly float _aoiRadius;
    private readonly int _keyframeInterval;
    private readonly ILogger _logger;
    private readonly GameMetrics? _metrics;
    private ulong _currentTick;
    private int _snapshotsThisTick;

    /// <summary>
    /// Scratch map (user ID -> index of the newest input in this tick's drained batch).
    /// Reused across ticks so input coalescing allocates nothing in the hot path.
    /// </summary>
    private readonly Dictionary<string, int> _newestInputIndex = new();

    /// <summary>Current simulation tick.</summary>
    public ulong CurrentTick => _currentTick;

    public TickLoop(
        GameWorld world,
        InputHandler handler,
        ConnectionManager connections,
        int tickRate,
        float aoiRadius,
        ILogger logger,
        GameMetrics? metrics = null,
        int keyframeInterval = GameConstants.DefaultKeyframeInterval)
    {
        _world = world;
        _handler = handler;
        _connections = connections;
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

        // Drain and process all pending inputs under one world Update
        var inputs = _world.DrainInputs();

        if (inputs.Count > 0)
        {
            // Coalesce movement: at most one integration step per player per tick, using
            // the newest input (highest client tick, arrival order as tiebreak). Without
            // this, a client that sends N input packets per tick would move N times as
            // far — the movement model is per-tick, not per-message.
            _newestInputIndex.Clear();
            for (int i = 0; i < inputs.Count; i++)
            {
                string userId = inputs[i].UserId;
                if (!_newestInputIndex.TryGetValue(userId, out int best) ||
                    inputs[i].Input.Tick >= inputs[best].Input.Tick)
                {
                    _newestInputIndex[userId] = i;
                }
            }

            _world.Update((get, set) =>
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var pi = inputs[i];
                    bool applyMovement = _newestInputIndex[pi.UserId] == i;
                    _handler.ProcessInputLocked(get, set, pi.UserId, pi.Input, _currentTick, applyMovement);
                }
            });
        }

        // Broadcast snapshots to each connected player
        _connections.ForEach(conn =>
        {
            try
            {
                Vec2 playerPos = default;
                ulong ackTick = 0;
                _world.View(get =>
                {
                    var entity = get(conn.UserId);
                    if (entity != null)
                    {
                        playerPos = entity.Value.Position;
                        // Per-player acknowledgement: the newest input tick this
                        // player's own entity has accepted. Other players' entities
                        // never contribute — reconciliation is strictly per-client.
                        ackTick = entity.Value.LastInputTick;
                    }
                });

                var nearby = SnapshotEncoder.GetNearbyEntities(_world, playerPos, _aoiRadius);
                var snapshot = conn.DeltaState.Encode(_currentTick, ackTick, nearby, _keyframeInterval);
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
