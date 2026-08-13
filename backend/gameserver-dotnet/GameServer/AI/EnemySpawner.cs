using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.World;

namespace GameServer.AI;

/// <summary>
/// Server-authoritative enemy spawner and AI. Runs every tick inside the
/// simulation loop so all clients see identical enemies via the existing
/// snapshot system.
///
/// <list type="bullet">
///   <item>Spawns waves of "mob" entities from the map edges.</item>
///   <item>Moves every living enemy toward the map center each tick.</item>
///   <item>Despawns enemies that reach the center zone near (0,0).</item>
///   <item>Removes dead/despawned enemies from the world.</item>
/// </list>
///
/// Enemies are regular <see cref="EntityState"/> entries with
/// <c>Type = "mob"</c>, so the snapshot encoder, delta encoder, and client
/// renderer handle them with zero protocol changes.
/// </summary>
public sealed class EnemySpawner
{
    // ── Spawn tuning ──

    /// <summary>World-unit radius enemies spawn at (from center).</summary>
    private const float SpawnRadius = 13.0f;

    /// <summary>Enemies per wave.</summary>
    private const int EnemiesPerWave = 2;

    /// <summary>Seconds between waves.</summary>
    private const float WaveIntervalSec = 1.5f;

    /// <summary>Maximum simultaneous enemies in the world.</summary>
    private const int MaxEnemies = 30;

    // ── Enemy stats ──

    private const int EnemyHp = 30;
    private const int EnemyAttack = 5;
    private const int EnemyDefense = 2;

    /// <summary>Movement speed toward center (world units per second).</summary>
    private const float EnemySpeed = 2.5f;

    // ── Center zone ──

    /// <summary>Radius around (0,0) at which enemies despawn (reached the center).</summary>
    private const float DespawnRadius = 2.5f;
    private const float DespawnRadiusSq = DespawnRadius * DespawnRadius;

    // ── State ──

    private readonly EcsWorld _world;
    private readonly ILogger _logger;
    private readonly float _dt;

    /// <summary>Tracks all living enemy IDs so we can iterate them each tick.</summary>
    private readonly List<string> _enemyIds = new();

    /// <summary>Scratch list for removals during iteration (avoids modifying _enemyIds mid-loop).</summary>
    private readonly List<string> _removeBuffer = new();

    /// <summary>Enemy IDs to remove from the world after the Update call returns.</summary>
    private readonly List<string> _pendingRemovals = new();

    private int _nextEnemyNum;
    private float _spawnAccumulator;

    /// <summary>Number of enemies currently alive.</summary>
    public int AliveCount => _enemyIds.Count;

    public EnemySpawner(EcsWorld world, int tickRate, ILogger logger)
    {
        _world = world;
        _logger = logger;
        _dt = 1.0f / tickRate;
    }

    /// <summary>
    /// Called once per simulation tick, inside the tick loop. Spawns new
    /// enemies if the timer fires, then moves and damages all living enemies.
    /// Must be called under the world write lock (from <see cref="EcsWorld.Update"/>).
    /// </summary>
    /// <remarks>
    /// Dead enemies are marked <c>Dead = true</c> via <paramref name="set"/> so
    /// they appear as dead in the next snapshot, then collected in
    /// <see cref="PendingRemovals"/> for the caller to remove AFTER the
    /// <see cref="EcsWorld.Update"/> call returns (calling
    /// <see cref="EcsWorld.RemoveEntity"/> inside Update would deadlock on
    /// the non-reentrant write lock).
    /// </remarks>
    public void Tick(
        Func<string, EntityState?> get,
        Action<string, EntityState> set,
        ulong currentTick)
    {
        // ── Spawn phase ──
        _spawnAccumulator += _dt;
        if (_spawnAccumulator >= WaveIntervalSec)
        {
            _spawnAccumulator -= WaveIntervalSec;
            SpawnWave(set);
        }

        // ── Move + damage phase ──
        _removeBuffer.Clear();
        _pendingRemovals.Clear();

        for (int i = 0; i < _enemyIds.Count; i++)
        {
            var id = _enemyIds[i];
            var state = get(id);
            if (state == null || state.Value.Dead)
            {
                _removeBuffer.Add(id);
                _pendingRemovals.Add(id);
                continue;
            }

            var e = state.Value;

            // Move toward (0,0)
            float dx = -e.Position.X;
            float dy = -e.Position.Y;
            float distSq = dx * dx + dy * dy;

            if (distSq > 0.01f) // not already at center
            {
                float invDist = 1.0f / MathF.Sqrt(distSq);
                e.Position = new Vec2(
                    e.Position.X + dx * invDist * EnemySpeed * _dt,
                    e.Position.Y + dy * invDist * EnemySpeed * _dt);
            }

            // Despawn when enemy reaches center
            float centerDistSq = e.Position.X * e.Position.X + e.Position.Y * e.Position.Y;
            if (centerDistSq <= DespawnRadiusSq)
            {
                _removeBuffer.Add(id);
                _pendingRemovals.Add(id);
                _logger.LogDebug("Enemy {Id} despawned at center", id);
                continue; // skip set — entity will be removed
            }

            set(id, e);
        }

        // Remove from tracking list (world removal deferred to caller)
        for (int i = 0; i < _removeBuffer.Count; i++)
        {
            _enemyIds.Remove(_removeBuffer[i]);
        }
    }

    /// <summary>
    /// Enemy IDs that died or disappeared this tick. The caller must remove
    /// them from the world AFTER releasing the write lock to avoid deadlock.
    /// </summary>
    public IReadOnlyList<string> PendingRemovals => _pendingRemovals;

    private void SpawnWave(Action<string, EntityState> set)
    {
        int toSpawn = Math.Min(EnemiesPerWave, MaxEnemies - _enemyIds.Count);
        if (toSpawn <= 0) return;

        for (int i = 0; i < toSpawn; i++)
        {
            _nextEnemyNum++;
            string id = $"enemy-{_nextEnemyNum}";

            // Random angle on the spawn circle
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            float x = MathF.Cos(angle) * SpawnRadius;
            float y = MathF.Sin(angle) * SpawnRadius;

            var entity = new EntityState
            {
                Id = id,
                Type = "mob",
                Position = new Vec2(x, y),
                Hp = EnemyHp,
                MaxHp = EnemyHp,
                Speed = EnemySpeed,
                Attack = EnemyAttack,
                Defense = EnemyDefense,
            };

            // Use the set callback (SetEntityLocked) which creates the entity
            // if it doesn't exist — safe inside the Update write lock.
            set(id, entity);
            _enemyIds.Add(id);

            _logger.LogDebug("Spawned enemy {Id} at ({X:F1}, {Y:F1})", id, x, y);
        }
    }
}
