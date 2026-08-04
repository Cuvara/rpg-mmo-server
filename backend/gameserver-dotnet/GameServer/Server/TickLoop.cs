using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Input;
using GameServer.Net;
using GameServer.Snapshot;
using GameServer.World;

namespace GameServer.Server;

/// <summary>
/// Fixed-rate simulation tick loop. Drains inputs, processes them, then
/// broadcasts AOI-filtered snapshots to all connected players.
/// Port of Go server/tick.go.
/// </summary>
public sealed class TickLoop
{
    private readonly GameWorld _world;
    private readonly InputHandler _handler;
    private readonly ConnectionManager _connections;
    private readonly int _tickRate;
    private readonly float _aoiRadius;
    private readonly ILogger _logger;
    private ulong _currentTick;

    /// <summary>Current simulation tick.</summary>
    public ulong CurrentTick => _currentTick;

    public TickLoop(
        GameWorld world,
        InputHandler handler,
        ConnectionManager connections,
        int tickRate,
        float aoiRadius,
        ILogger logger)
    {
        _world = world;
        _handler = handler;
        _connections = connections;
        _tickRate = tickRate;
        _aoiRadius = aoiRadius;
        _logger = logger;
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
        _currentTick++;

        // Drain and process all pending inputs under one world Update
        var inputs = _world.DrainInputs();

        if (inputs.Count > 0)
        {
            _world.Update((get, set) =>
            {
                foreach (var pi in inputs)
                {
                    _handler.ProcessInputLocked(get, set, pi.UserId, pi.Input);
                }
            });
        }

        // Broadcast snapshots to each connected player
        _connections.ForEach(conn =>
        {
            try
            {
                Vec2 playerPos = default;
                _world.View(get =>
                {
                    var entity = get(conn.UserId);
                    if (entity != null)
                    {
                        playerPos = entity.Value.Position;
                    }
                });

                var nearby = SnapshotEncoder.GetNearbyEntities(_world, playerPos, _aoiRadius);
                var snapshot = SnapshotEncoder.Encode(_currentTick, nearby);
                var env = WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot);
                conn.Send(env);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send snapshot to {UserId}", conn.UserId);
            }
        });
    }
}
