using Microsoft.Extensions.Logging;
using GameServer.Server;
using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// <see cref="EnemyAiPhase.Spawn"/> — releases a wave of enemies on a fixed interval, up
/// to a population cap.
/// </summary>
/// <remarks>
/// <para><b>Not per-entity-linear, and therefore not a chunk loop.</b> This system reads
/// one piece of state and creates entities; there is no array to walk. Forcing it into a
/// chunk iteration would be shape for its own sake.</para>
///
/// <para>Structural work goes through <see cref="WorldWriter.Spawn"/>, which applies
/// immediately when nothing is iterating and otherwise queues for the deferred drain.
/// Nothing is iterating at this point in the schedule — that is why spawn is first — so
/// the entity exists in time for the move system to step it in the same tick. Arch's
/// <c>CommandBuffer</c> is not used and must not be (ADR-11).</para>
/// </remarks>
internal sealed class EnemySpawnSystem : IEcsSystem
{
    private readonly ILogger _logger;
    private readonly float _dt;

    public EnemySpawnSystem(float dt, ILogger logger)
    {
        _dt = dt;
        _logger = logger;
    }

    public string Name => "enemy.spawn";

    public int Order => (int)EnemyAiPhase.Spawn;

    /// <summary>
    /// World. Wave cadence is a world-simulation concern measured in seconds, and its
    /// accumulator is stepped by the world dt, so the wall-clock interval is unchanged by
    /// the base rate.
    /// </summary>
    public SimulationGroup Group => SimulationGroup.World;

    public ComponentAccess Access => new(
        writes: new[] { typeof(EnemySpawnState) },
        structural: true);

    public void Run(WorldWriter writer, ulong currentTick)
    {
        ref EnemySpawnState state = ref writer.Singleton<EnemySpawnState>();

        state.Accumulator += _dt;
        if (state.Accumulator < EnemyAiTuning.WaveIntervalSec) return;

        // Subtract rather than reset: the accumulator carries its remainder so the wave
        // cadence does not drift against wall time at tick rates that do not divide the
        // interval evenly.
        state.Accumulator -= EnemyAiTuning.WaveIntervalSec;

        int alive = writer.QueryWith<EnemyAi>(Span<EntityHandle>.Empty);
        int toSpawn = Math.Min(EnemyAiTuning.EnemiesPerWave, EnemyAiTuning.MaxEnemies - alive);
        if (toSpawn <= 0) return;

        for (int i = 0; i < toSpawn; i++)
        {
            // Re-taking the ref each iteration: Spawn is a structural change and may move
            // the singleton's chunk, which would leave a held ref pointing at stale
            // storage. Cheap, and the alternative is a bug that only appears once the
            // singleton's archetype happens to compact.
            ref EnemySpawnState s = ref writer.Singleton<EnemySpawnState>();
            s.NextEnemyNumber++;
            string id = $"enemy-{s.NextEnemyNumber}";

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

            // Guarded: the extension builds its params array and boxes both floats
            // BEFORE the level check, so an unguarded call allocates per spawned enemy
            // even with Debug off — inside the world write scope (#249).
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Spawned enemy {Id} at ({X:F1}, {Y:F1})", id, x, y);
            }
        }
    }
}

/// <summary>
/// <see cref="EnemyAiPhase.Move"/> — steps every living enemy one tick toward the origin.
/// </summary>
/// <remarks>
/// <para><b>This one is per-entity-linear, so it iterates chunks.</b> It walks
/// <c>Span&lt;Position&gt;</c> and <c>Span&lt;Health&gt;</c> directly rather than
/// resolving each entity through a handle, which is the shape ECS storage exists to
/// provide and the shape the core's own scans already use.</para>
///
/// <para><b>The arithmetic is deliberately not <c>MovementSystem.Integrate</c>.</b> The AI
/// has always carried its own step, and it differs in two ways that are visible on the
/// wire: it is not clamped to the map bounds, and it normalises with a reciprocal square
/// root rather than through <c>ResolveDirection</c>. Routing it through the shared model
/// would move every enemy onto different floats. The expression below is
/// character-for-character the original, pinned bit-exactly by
/// <c>EnemyAiCharacterizationTests.OneTickOfMovement_IsBitExactAgainstTheAisOwnArithmetic</c>.</para>
/// </remarks>
internal sealed class EnemyMoveSystem : IEcsSystem
{
    private readonly float _dt;

    public EnemyMoveSystem(float dt) => _dt = dt;

    public string Name => "enemy.move";

    public int Order => (int)EnemyAiPhase.Move;

    /// <summary>
    /// World. Enemies are not prediction-relevant — no client predicts them — so stepping
    /// them four times more often would buy nothing a client can observe while costing a
    /// full chunk walk per base tick. It is also what keeps this system's arithmetic on the
    /// same floats: <c>EnemyAiCharacterizationTests</c> pins one 15Hz step bit-exactly.
    /// </summary>
    public SimulationGroup Group => SimulationGroup.World;

    public ComponentAccess Access => new(
        reads: new[] { typeof(Health) },
        writes: new[] { typeof(Position) });

    public void Run(WorldWriter writer, ulong currentTick)
    {
        var body = new Body(_dt);
        writer.VisitChunks<EnemyAi, Body>(ref body);
    }

    /// <summary>
    /// The per-chunk body. A <c>struct</c> so the visit devirtualises and nothing is
    /// allocated per chunk or per tick.
    /// </summary>
    private struct Body : ISimChunkVisitor
    {
        private readonly float _dt;

        public Body(float dt) => _dt = dt;

        public void Visit(in SimChunk chunk)
        {
            Span<Position> positions = chunk.Positions;
            Span<Health> healths = chunk.Healths;

            for (int i = 0; i < chunk.Count; i++)
            {
                // A dead enemy does not move. It is still reaped, by the reap system.
                if (healths[i].Dead) continue;

                float dx = -positions[i].Value.X;
                float dy = -positions[i].Value.Y;
                float distSq = dx * dx + dy * dy;

                if (distSq <= 0.01f) continue; // already at center

                float invDist = 1.0f / MathF.Sqrt(distSq);
                positions[i].Value = new Vec2(
                    positions[i].Value.X + dx * invDist * EnemyAiTuning.EnemySpeed * _dt,
                    positions[i].Value.Y + dy * invDist * EnemyAiTuning.EnemySpeed * _dt);
            }
        }
    }
}

/// <summary>
/// <see cref="EnemyAiPhase.Reap"/> — destroys enemies that are dead or that have reached
/// the centre zone.
/// </summary>
/// <remarks>
/// <para><b>Not per-entity-linear, and therefore still handle-based.</b> The decision is
/// per entity and the action is structural: it needs an entity identity to despawn, which
/// a component span does not carry. Exposing entity handles through the chunk view purely
/// to satisfy a shape rule would add the one piece of Arch that the view is designed not
/// to leak, for no gain. This is the exception the design allows and it is stated rather
/// than hidden.</para>
///
/// <para>Despawns go through <see cref="WorldWriter.Despawn"/>, so a removal raised while
/// a query is iterating is queued and drained by the deferred structural phase rather than
/// mutating storage mid-iteration. Reaping inside the same scope, before snapshots are
/// built, is what keeps a centre-arriving enemy from ever being observable inside the
/// despawn radius.</para>
/// </remarks>
internal sealed class EnemyReapSystem : IEcsSystem
{
    private readonly ILogger _logger;

    /// <summary>
    /// Reusable handle buffer, refilled from a query before every read. Resetting it at
    /// any tick boundary would change nothing but allocation, which is the test
    /// <see cref="SimulationScratchAttribute"/> demands.
    /// </summary>
    [SimulationScratch]
    private EntityHandle[] _handles = Array.Empty<EntityHandle>();

    public EnemyReapSystem(ILogger logger) => _logger = logger;

    public string Name => "enemy.reap";

    public int Order => (int)EnemyAiPhase.Reap;

    /// <summary>
    /// World, and deliberately <b>not</b> Background despite looking like cleanup. Reaping
    /// is what stops a dead or centre-arrived enemy from being observable — it must run in
    /// the same pass that moved it and before the snapshot is built. At 5Hz an enemy would
    /// stay visible inside the despawn radius for up to 200ms, which is a gameplay
    /// regression wearing the costume of a performance win.
    /// </summary>
    public SimulationGroup Group => SimulationGroup.World;

    public ComponentAccess Access => new(
        reads: new[] { typeof(Position), typeof(Health) },
        structural: true);

    public void Run(WorldWriter writer, ulong currentTick)
    {
        int count = writer.QueryWith<EnemyAi>(_handles);
        if (count > _handles.Length)
        {
            // Headroom: exact-size growth re-queries again at count+1 (#249).
            _handles = new EntityHandle[count + (count >> 2)];
            count = writer.QueryWith<EnemyAi>(_handles);
        }

        int n = Math.Min(count, _handles.Length);
        for (int i = 0; i < n; i++)
        {
            ref readonly EntityHandle handle = ref _handles[i];
            if (!writer.IsAlive(in handle)) continue;

            if (writer.HealthOf(in handle).Dead)
            {
                writer.Despawn(in handle);
                continue;
            }

            Vec2 p = writer.PositionOf(in handle).Value;
            if (p.X * p.X + p.Y * p.Y <= EnemyAiTuning.DespawnRadiusSq)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Enemy despawned at center");
                }
                writer.Despawn(in handle);
            }
        }
    }
}
