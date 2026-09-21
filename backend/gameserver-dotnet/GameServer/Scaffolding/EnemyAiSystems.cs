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
    /// <summary>
    /// Attempts made to place an enemy clear of every live player before the placement is
    /// accepted anyway.
    ///
    /// <para>Bounded, and small, because the loop has to terminate on a world where no
    /// clear placement exists — a lobby where twenty players stand on the same tile has no
    /// point at the spawn distance from any of them that is clear of all of them, and an
    /// unbounded retry would spin the tick loop forever looking for one. Six samples of a
    /// circle is enough that an ordinary spread of players almost always yields a clear
    /// point on the first or second, and the fallback is not a failure: an enemy at the
    /// full spawn distance from the player it was anchored to is still a fair spawn for
    /// that player, it is merely closer than preferred to somebody else.</para>
    /// </summary>
    private const int PlacementAttempts = 6;

    private readonly ILogger _logger;
    private readonly float _dt;
    private readonly EnemyAiSettings _settings;

    /// <summary>
    /// Live players, refilled from the world on every wave. See
    /// <see cref="PlayerTargetBuffer"/> for why this is re-read rather than cached.
    /// </summary>
    [SimulationScratch]
    private PlayerTargetBuffer _players = new();

    public EnemySpawnSystem(float dt, EnemyAiSettings settings, ILogger logger)
    {
        _dt = dt;
        _settings = settings;
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

    /// <summary>
    /// Reads <see cref="Position"/> and <see cref="Health"/> now, because placement is
    /// anchored to where the live players are. Declared rather than left implicit: the
    /// whole point of these sets is that a reader — and eventually the scheduler — can see
    /// what a system touches without reading its body.
    /// </summary>
    public ComponentAccess Access => new(
        reads: new[] { typeof(Position), typeof(Health) },
        writes: new[] { typeof(EnemySpawnState) },
        structural: true);

    public void Run(WorldWriter writer, ulong currentTick)
    {
        ref EnemySpawnState state = ref writer.Singleton<EnemySpawnState>();

        state.Accumulator += _dt;
        if (state.Accumulator < _settings.WaveIntervalSec) return;

        // Subtract rather than reset: the accumulator carries its remainder so the wave
        // cadence does not drift against wall time at tick rates that do not divide the
        // interval evenly.
        state.Accumulator -= _settings.WaveIntervalSec;

        // Read once per wave, before any structural change. Both the cap and the wave size
        // scale on it, and the placement anchors on it.
        int livePlayers = _settings.Chase ? _players.Refresh(writer) : 0;

        int alive = writer.QueryWith<EnemyAi>(Span<EntityHandle>.Empty);
        int toSpawn = Math.Min(
            _settings.EffectiveWaveSize(livePlayers),
            _settings.EffectiveMaxEnemies(livePlayers) - alive);
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

            // NOT re-read inside the loop, deliberately. The players this wave is placed
            // around are the ones that were there when the wave began; refreshing per
            // enemy would re-query the world once per spawned entity for a set that cannot
            // have changed, since nothing between here and the drain moves a player.
            Vec2 position = ChoosePosition(livePlayers);

            writer.Spawn(
                new EntityState
                {
                    Id = id,
                    Type = "mob",
                    Position = position,
                    Hp = _settings.Hp,
                    MaxHp = _settings.Hp,
                    Speed = _settings.Speed,
                    Attack = _settings.Attack,
                    Defense = _settings.Defense,
                },
                EntityTags.EnemyAi);

            // Guarded: the extension builds its params array and boxes both floats
            // BEFORE the level check, so an unguarded call allocates per spawned enemy
            // even with Debug off — inside the world write scope (#249).
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Spawned enemy {Id} at ({X:F1}, {Y:F1})", id, position.X, position.Y);
            }
        }
    }

    /// <summary>
    /// Where one enemy of this wave goes.
    ///
    /// <para>With no live player to anchor to this is the pre-change placement
    /// character-for-character — a uniform angle on a circle of
    /// <see cref="EnemyAiSettings.SpawnDistance"/> about the origin — which is what keeps
    /// an empty server identical to the one that shipped and lets the characterization
    /// tests stay untouched.</para>
    ///
    /// <para>With players, the anchor is a <b>uniformly chosen</b> one of them rather than
    /// the centroid or the nearest. The centroid is a single point, so it reproduces the
    /// one small ring that made the fight a conveyor belt as soon as the group is spread
    /// out; choosing per enemy spreads the wave across everybody who is playing, which is
    /// the difference between a crowd converging on one spot and a crowd everywhere.</para>
    /// </summary>
    private Vec2 ChoosePosition(int livePlayers)
    {
        float angle = Random.Shared.NextSingle() * MathF.Tau;

        if (livePlayers <= 0)
        {
            return new Vec2(
                MathF.Cos(angle) * _settings.SpawnDistance,
                MathF.Sin(angle) * _settings.SpawnDistance);
        }

        Vec2 candidate = default;
        for (int attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            Vec2 anchor = _players[Random.Shared.Next(livePlayers)];
            candidate = _settings.Bounds.Clamp(new Vec2(
                anchor.X + (MathF.Cos(angle) * _settings.SpawnDistance),
                anchor.Y + (MathF.Sin(angle) * _settings.SpawnDistance)));

            // Clamping is what makes the check necessary rather than paranoid: a spawn
            // measured at the full distance from its anchor can be pulled back across the
            // map edge to within arm's reach of a player standing in the corner.
            if (!_players.AnyWithin(candidate, _settings.MinSpawnDistanceSq))
            {
                return candidate;
            }

            angle = Random.Shared.NextSingle() * MathF.Tau;
        }

        // Exhausted: every sample landed near somebody. Take the last one rather than
        // giving up on the enemy — a wave that silently spawns fewer entities the tighter
        // the crowd gets is the threadbare fight coming back through the door marked
        // "safety check".
        return candidate;
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
    private readonly EnemyAiSettings _settings;

    /// <summary>
    /// Live players, refilled from the world at the top of every run. See
    /// <see cref="PlayerTargetBuffer"/> for why this is re-read rather than cached, and
    /// why the nearest-player search is a scan.
    /// </summary>
    [SimulationScratch]
    private PlayerTargetBuffer _players = new();

    public EnemyMoveSystem(float dt, EnemyAiSettings settings)
    {
        _dt = dt;
        _settings = settings;
    }

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
        // Gathered before the chunk walk, not inside it. Reading the player set per enemy
        // would be an archetype query per entity per tick, and the set cannot change
        // during the walk: nothing in this system writes a player's position.
        int livePlayers = _settings.Chase ? _players.Refresh(writer) : 0;

        var body = new Body(_dt, _settings, livePlayers > 0 ? _players : null);
        writer.VisitChunks<EnemyAi, Body>(ref body);
    }

    /// <summary>
    /// The per-chunk body. A <c>struct</c> so the visit devirtualises and nothing is
    /// allocated per chunk or per tick.
    /// </summary>
    private struct Body : ISimChunkVisitor
    {
        private readonly float _dt;
        private readonly EnemyAiSettings _settings;

        /// <summary>
        /// The live players, or null when there are none and every enemy falls back to the
        /// origin. Holding the buffer by reference costs nothing — it is the system's own
        /// scratch, already filled, and copying its contents into the struct is what an
        /// allocation would look like here.
        /// </summary>
        private readonly PlayerTargetBuffer? _players;

        public Body(float dt, EnemyAiSettings settings, PlayerTargetBuffer? players)
        {
            _dt = dt;
            _settings = settings;
            _players = players;
        }

        public void Visit(in SimChunk chunk)
        {
            Span<Position> positions = chunk.Positions;
            Span<Health> healths = chunk.Healths;

            for (int i = 0; i < chunk.Count; i++)
            {
                // A dead enemy does not move. It is still reaped, by the reap system.
                if (healths[i].Dead) continue;

                // The target, and the whole behaviour change: the nearest live player when
                // there is one, and otherwise the origin — which is the only target the AI
                // ever had. A world with no live players therefore runs the pre-change step
                // on the pre-change floats, which is why EnemyAiCharacterizationTests still
                // pins this arithmetic bit-exactly.
                // `target` starts at the origin, so the no-player path computes
                // `0f - x` where the pre-change code wrote `-x`. Those differ for exactly
                // one input, x == +0.0, and that input is caught by the `distSq <= 0.01f`
                // guard two lines down before either result is used.
                Vec2 target = default;
                bool hasTarget = _players != null
                    && _players.TryNearest(positions[i].Value, out target);

                float dx = target.X - positions[i].Value.X;
                float dy = target.Y - positions[i].Value.Y;
                float distSq = dx * dx + dy * dy;

                if (distSq <= 0.01f) continue; // already on top of the target

                // Stop at contact rather than walking into the target and jittering across
                // it every tick. Only when chasing: at the origin there is nothing to stand
                // off from, and applying a stand-off there would leave a ring of enemies
                // circling the centre forever instead of reaching the despawn zone.
                if (hasTarget && distSq <= _settings.ContactRangeSq) continue;

                float invDist = 1.0f / MathF.Sqrt(distSq);
                positions[i].Value = new Vec2(
                    positions[i].Value.X + dx * invDist * _settings.Speed * _dt,
                    positions[i].Value.Y + dy * invDist * _settings.Speed * _dt);
            }
        }
    }
}

/// <summary>
/// <see cref="EnemyAiPhase.Reap"/> — destroys enemies that are dead, and enemies that
/// have reached the centre zone <b>while there is nothing to chase</b>.
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
    private readonly EnemyAiSettings _settings;

    /// <summary>
    /// Live players, refilled from the world at the top of every run. Only the count is
    /// used: centre-despawn applies to an enemy with nothing to chase, and "nothing to
    /// chase" is a property of the world, not of the individual enemy.
    /// </summary>
    [SimulationScratch]
    private PlayerTargetBuffer _players = new();

    /// <summary>
    /// Reusable handle buffer, refilled from a query before every read. Resetting it at
    /// any tick boundary would change nothing but allocation, which is the test
    /// <see cref="SimulationScratchAttribute"/> demands.
    /// </summary>
    [SimulationScratch]
    private EntityHandle[] _handles = Array.Empty<EntityHandle>();

    public EnemyReapSystem(EnemyAiSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

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
        // Whether anything is being chased at all. When it is, an enemy standing near the
        // origin is standing where the fight is and must not be deleted for it; when it is
        // not, the origin is the destination and arriving there is the end of the enemy's
        // life, exactly as before this change.
        bool centreDespawn = !_settings.Chase || _players.Refresh(writer) == 0;

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

            if (!centreDespawn) continue;

            Vec2 p = writer.PositionOf(in handle).Value;
            if (p.X * p.X + p.Y * p.Y <= _settings.DespawnRadiusSq)
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
