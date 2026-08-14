using Microsoft.Extensions.Logging;
using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;

namespace GameServer.AI;

/// <summary>
/// <see cref="EnemyAiPhase.Spawn"/> — releases a wave of enemies on a fixed interval,
/// up to a population cap.
/// </summary>
/// <remarks>
/// Structural work: this is the server's only entity <b>creation</b> outside the join
/// path. It goes through <see cref="WorldWriter.Spawn"/>, which applies immediately when
/// nothing is iterating and otherwise queues for the deferred structural phase. Nothing
/// is iterating at this point in the tick — that is the reason spawn runs first — so the
/// entity exists in time for <see cref="EnemyMoveSystem"/> to step it in the same tick.
/// Arch's <c>CommandBuffer</c> is not used and must not be (ADR-11).
/// </remarks>
internal sealed class EnemySpawnSystem
{
    private readonly ILogger _logger;
    private readonly float _dt;

    private int _nextEnemyNum;
    private float _spawnAccumulator;

    public EnemySpawnSystem(float dt, ILogger logger)
    {
        _dt = dt;
        _logger = logger;
    }

    public void Run(WorldWriter writer, int aliveCount)
    {
        _spawnAccumulator += _dt;
        if (_spawnAccumulator < EnemyAiTuning.WaveIntervalSec) return;

        // Subtract rather than reset: the accumulator carries its remainder so the wave
        // cadence does not drift against wall time at tick rates that do not divide the
        // interval evenly.
        _spawnAccumulator -= EnemyAiTuning.WaveIntervalSec;

        int toSpawn = Math.Min(EnemyAiTuning.EnemiesPerWave, EnemyAiTuning.MaxEnemies - aliveCount);
        if (toSpawn <= 0) return;

        for (int i = 0; i < toSpawn; i++)
        {
            _nextEnemyNum++;
            string id = $"enemy-{_nextEnemyNum}";

            // Random angle on the spawn circle.
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            float x = MathF.Cos(angle) * EnemyAiTuning.SpawnRadius;
            float y = MathF.Sin(angle) * EnemyAiTuning.SpawnRadius;

            writer.Spawn(
                new EntityState
                {
                    Id = id,
                    Type = "mob",
                    Position = new Vec2(x, y),
                    Hp = EnemyAiTuning.EnemyHp,
                    MaxHp = EnemyAiTuning.EnemyHp,
                    Speed = EnemyAiTuning.EnemySpeed,
                    Attack = EnemyAiTuning.EnemyAttack,
                    Defense = EnemyAiTuning.EnemyDefense,
                },
                EntityTags.EnemyAi);

            _logger.LogDebug("Spawned enemy {Id} at ({X:F1}, {Y:F1})", id, x, y);
        }
    }
}

/// <summary>
/// <see cref="EnemyAiPhase.Move"/> — steps every living enemy one tick toward the
/// origin.
/// </summary>
/// <remarks>
/// <para><b>The arithmetic is deliberately not <c>MovementSystem.Integrate</c>.</b> The
/// AI has always carried its own step, and it differs in two ways that are visible on
/// the wire: it is not clamped to the map bounds, and it normalises with a reciprocal
/// square root rather than through <c>ResolveDirection</c>. Routing it through the
/// shared movement model would move every enemy onto different floats. The expression
/// below is character-for-character the original, and
/// <c>EnemyAiCharacterizationTests.OneTickOfMovement_IsBitExactAgainstTheAisOwnArithmetic</c>
/// pins it bit-exactly against a re-evaluation of it.</para>
///
/// <para>Unifying the two movement models is a real question, but it is a gameplay
/// decision with a wire consequence, not a refactor — see the CHANGELOG.</para>
/// </remarks>
internal sealed class EnemyMoveSystem
{
    private readonly float _dt;

    public EnemyMoveSystem(float dt) => _dt = dt;

    public void Run(WorldWriter writer, ReadOnlySpan<EntityHandle> enemies)
    {
        for (int i = 0; i < enemies.Length; i++)
        {
            ref readonly EntityHandle handle = ref enemies[i];
            if (!writer.IsAlive(in handle)) continue;

            // A dead enemy does not move. It is still reaped, by the reap system.
            if (writer.HealthOf(in handle).Dead) continue;

            ref Position position = ref writer.PositionOf(in handle);

            float dx = -position.Value.X;
            float dy = -position.Value.Y;
            float distSq = dx * dx + dy * dy;

            if (distSq <= 0.01f) continue; // already at center

            float invDist = 1.0f / MathF.Sqrt(distSq);
            position.Value = new Vec2(
                position.Value.X + dx * invDist * EnemyAiTuning.EnemySpeed * _dt,
                position.Value.Y + dy * invDist * EnemyAiTuning.EnemySpeed * _dt);
        }
    }
}

/// <summary>
/// <see cref="EnemyAiPhase.Reap"/> — destroys enemies that are dead or that have
/// reached the centre zone.
/// </summary>
/// <remarks>
/// <para>Structural work: the server's only entity <b>destruction</b> outside the
/// disconnect path. It goes through <see cref="WorldWriter.Despawn"/>, so a removal
/// raised while a query is iterating is queued and drained by the deferred structural
/// phase rather than mutating storage mid-iteration. The previous shape could not do
/// this at all — it collected ids into a <c>PendingRemovals</c> list which the tick loop
/// drained <i>after</i> releasing the write lock, because calling <c>RemoveEntity</c>
/// inside the lock would have deadlocked on it.</para>
///
/// <para>Reaping inside the same scope, before snapshots are built, is what keeps a
/// centre-arriving enemy from ever being observable inside the despawn radius — the
/// property <c>EnemyReachingTheCentre_IsGoneTheSameTick_AndNeverObservedInsideTheZone</c>
/// checks on every tick of a 400-tick run.</para>
/// </remarks>
internal sealed class EnemyReapSystem
{
    private readonly ILogger _logger;

    public EnemyReapSystem(ILogger logger) => _logger = logger;

    public void Run(WorldWriter writer, ReadOnlySpan<EntityHandle> enemies)
    {
        for (int i = 0; i < enemies.Length; i++)
        {
            ref readonly EntityHandle handle = ref enemies[i];
            if (!writer.IsAlive(in handle)) continue;

            if (writer.HealthOf(in handle).Dead)
            {
                writer.Despawn(in handle);
                continue;
            }

            Vec2 p = writer.PositionOf(in handle).Value;
            if (p.X * p.X + p.Y * p.Y <= EnemyAiTuning.DespawnRadiusSq)
            {
                _logger.LogDebug("Enemy despawned at center");
                writer.Despawn(in handle);
            }
        }
    }
}
