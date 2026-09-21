using System;
using Microsoft.Extensions.Logging;
using GameServer.Server;
using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// Marks a player entity as synthetic. See <see cref="EntityTags.Bot"/> for why identity
/// is a tag rather than an id convention, and what goes wrong without it.
/// </summary>
/// <remarks>
/// Carries a byte for the same reason <see cref="PlayerTag"/> does: Arch stores a backing
/// array per component type per chunk, and a zero-size struct is a degenerate case not
/// worth relying on.
/// </remarks>
[EcsComponent]
public struct BotTag
{
    /// <summary>Unused. Present so the component has a size.</summary>
    public byte Value;
}

/// <summary>
/// The bot phase's own simulation state, on a singleton entity rather than in fields —
/// the same rule, and for the same reason, as <see cref="EnemySpawnState"/>.
/// </summary>
[EcsComponent]
public struct BotSpawnState
{
    /// <summary>True once the population has been created. Bots spawn once, not in waves.</summary>
    public bool Spawned;

    /// <summary>Monotonic counter behind the wander direction. Never reset.</summary>
    public uint WanderSeed;
}

/// <summary>
/// Synthetic players that move and fight, so a map with three real clients still reads as
/// a crowd.
///
/// <para><b>Why not <see cref="LoadTestSpawner"/>.</b> That class was read first and is
/// unsuitable, for three reasons that are all structural rather than cosmetic. It spawns
/// <c>Type = "mob"</c> entities tagged <see cref="EnemyAi"/> — they are enemies, not
/// players, so they would be chased and reaped by the enemy systems and counted in
/// <c>enemies_alive</c>. It is <b>mutually exclusive</b> with the enemy spawner in the
/// composition root, so turning it on turns the fight off. And its motion is a rigid
/// rotation about the world origin at a fixed radius, which is precisely the
/// everything-happens-at-(0,0) shape this work exists to remove. It is a bandwidth load
/// generator — worst-case delta pressure, every entity dirty every tick — and it is good
/// at that; it is not a player simulator, and bending it into one would cost more than
/// this file and leave the load-test tool worse.</para>
///
/// <para><b>Bots drive the real input path.</b> The brain produces an
/// <see cref="InputData"/> and pushes it through <see cref="EcsWorld.PushInput"/> with a
/// null ingress — the door that method documents for "callers that have no connection
/// (tests, benches, scaffolding)". The ordinary <c>InputHandler</c> then validates and
/// applies it on the next tick: real movement integration, real map clamping, real
/// <c>CombatLogic</c> validation, real cooldowns, real damage, real death handling and
/// real kill events. Nothing about combat is reimplemented here, which is the point —
/// a second damage path that drifted from the first would be a bug that only shows up as
/// bots doing something players cannot.</para>
///
/// <para><b>Why input is pushed outside the write scope.</b> <see cref="EcsWorld.PushInput"/>
/// takes the read lock to resolve the id, and the world's lock is not recursive, so
/// calling it from inside <c>UpdateComponents</c> throws. The brain system therefore
/// <i>decides</i> inside the scope, writing to a reusable buffer, and <see cref="Tick"/>
/// flushes the decisions after the scope closes. The one-tick delay between deciding and
/// acting is not a workaround — it is exactly the delay a real client has, and running the
/// bots on a shorter path than players is how bots stop being a test of anything.</para>
/// </summary>
public sealed class BotPlayerSpawner : ISimulationPhase
{
    private readonly EcsWorld _world;
    private readonly SimulationSchedule _schedule;
    private readonly BotSettings _settings;
    private readonly Action<ulong, WorldWriter> _runSchedule;
    private readonly Action<SimulationGroup, long, long>? _onGroupRan;

    /// <summary>
    /// Decisions made by the brain system this tick, flushed after the write scope closes.
    /// Scratch in the strict sense: it is filled from the world and drained within one
    /// <see cref="Tick"/>, so resetting it at any tick boundary would change nothing.
    /// </summary>
    [SimulationScratch]
    private readonly BotDecisionBuffer _decisions = new();

    public BotPlayerSpawner(
        EcsWorld world,
        SimulationRates rates,
        BotSettings settings,
        ILogger logger,
        Action<SimulationGroup, long, long>? onGroupRan = null)
    {
        _world = world;
        _settings = settings;
        _onGroupRan = onGroupRan;

        // No dt is taken, unlike every other phase here, and that is the design rather than
        // an omission: this phase integrates nothing. It emits input DIRECTIONS, and the
        // input handler multiplies by speed and the critical group's dt exactly as it does
        // for a real client. A dt held here would be an invitation to move bots directly.
        _schedule = new SimulationSchedule(
            rates,
            new BotSpawnSystem(settings, logger),
            new BotBrainSystem(settings, _decisions));

        _runSchedule = (tick, writer) => _schedule.RunDue(writer, tick, _onGroupRan);
    }

    /// <summary>Bots currently in the world. A count over the archetype, so it cannot drift.</summary>
    public int AliveCount => _world.CountWith<BotTag>();

    /// <summary>The tuning in force. Diagnostics and <c>/status</c>.</summary>
    public BotSettings Settings => _settings;

    public void Tick(ulong currentTick)
    {
        if (!_schedule.AnyDue(currentTick)) return;

        _world.UpdateComponents(currentTick, _runSchedule);

        // Outside the scope, deliberately — see the class remarks.
        _decisions.Flush(_world, currentTick);
    }
}

/// <summary>
/// Creates the bot population once, scattered over <see cref="BotSettings.Spread"/>.
/// </summary>
internal sealed class BotSpawnSystem : IEcsSystem
{
    private readonly BotSettings _settings;
    private readonly ILogger _logger;

    public BotSpawnSystem(BotSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string Name => "bot.spawn";

    public int Order => 0;

    public SimulationGroup Group => SimulationGroup.World;

    public ComponentAccess Access => new(
        writes: new[] { typeof(BotSpawnState) },
        structural: true);

    public void Run(WorldWriter writer, ulong currentTick)
    {
        ref BotSpawnState state = ref writer.Singleton<BotSpawnState>();
        if (state.Spawned) return;
        state.Spawned = true;

        for (int i = 0; i < _settings.Count; i++)
        {
            // Re-taken every iteration: Spawn is structural and may move the singleton's
            // chunk, leaving a held ref pointing at stale storage. Same rule, and the same
            // reason, as EnemySpawnSystem.
            ref BotSpawnState s = ref writer.Singleton<BotSpawnState>();
            s.WanderSeed++;

            // A uniform disc rather than a ring: sqrt of the fraction is what makes the
            // area density even instead of piling every bot on the rim. The golden-angle
            // step spreads successive indices around the disc rather than letting a small
            // population land on one arc.
            float angle = i * 2.39996323f;
            float radius = _settings.Spread * MathF.Sqrt((i + 0.5f) / _settings.Count);
            var position = _settings.Bounds.Clamp(new Vec2(
                MathF.Cos(angle) * radius,
                MathF.Sin(angle) * radius));

            writer.Spawn(
                new EntityState
                {
                    Id = $"{BotSettings.IdPrefix}{i:D5}",
                    // A real player type, because a bot is supposed to be indistinguishable
                    // from one to the client, the AOI and the enemy AI. What separates it
                    // is the tag, and the tag matters in exactly one place: persistence.
                    Type = "player",
                    Position = position,
                    Hp = _settings.Hp,
                    MaxHp = _settings.Hp,
                    Speed = _settings.Speed,
                    Attack = _settings.Attack,
                    Defense = _settings.Defense,
                },
                EntityTags.Bot);
        }

        _logger.LogInformation(
            "Spawned {Count} bot players over a {Spread}-unit disc. Bots are NOT persisted " +
            "and hold no connection; they exist to make the map look inhabited.",
            _settings.Count, _settings.Spread);
    }
}

/// <summary>
/// Decides what each bot does this tick: engage the nearest enemy within
/// <see cref="BotSettings.EngageRange"/>, or wander.
/// </summary>
/// <remarks>
/// <para>Handle-based rather than a chunk walk, and this is the exception the design
/// allows rather than an oversight: the decision needs the bot's <b>id string</b>, because
/// that is what <see cref="EcsWorld.PushInput"/> addresses an input to, and a component
/// span does not carry identity. It also needs the enemy's id, for the same reason.</para>
///
/// <para>Nothing is allocated per tick in steady state. The handle and position buffers
/// belong to <see cref="BotDecisionBuffer"/> and are reused; the id strings are the
/// interned ones already held by <c>EntityIdRef</c>, not new ones.</para>
/// </remarks>
internal sealed class BotBrainSystem : IEcsSystem
{
    private readonly BotSettings _settings;
    private readonly BotDecisionBuffer _decisions;

    /// <summary>Reusable handle buffers. See <see cref="EnemyReapSystem"/> for the growth rule.</summary>
    [SimulationScratch]
    private EntityHandle[] _bots = Array.Empty<EntityHandle>();

    [SimulationScratch]
    private EntityHandle[] _enemies = Array.Empty<EntityHandle>();

    public BotBrainSystem(BotSettings settings, BotDecisionBuffer decisions)
    {
        _settings = settings;
        _decisions = decisions;
    }

    public string Name => "bot.brain";

    public int Order => 1;

    /// <summary>
    /// World. A bot is not prediction-relevant to anybody — no client predicts one — so
    /// deciding four times more often would buy nothing observable and cost a full scan
    /// per base tick. The input it emits is still integrated by the critical group, at the
    /// critical rate, exactly like a real player's.
    /// </summary>
    public SimulationGroup Group => SimulationGroup.World;

    public ComponentAccess Access => new(
        reads: new[] { typeof(Position), typeof(Health) });

    public void Run(WorldWriter writer, ulong currentTick)
    {
        int botCount = Fill(writer, ref _bots, bots: true);
        if (botCount == 0)
        {
            _decisions.Clear();
            return;
        }

        int enemyCount = Fill(writer, ref _enemies, bots: false);

        _decisions.Begin(botCount);

        for (int i = 0; i < botCount; i++)
        {
            ref readonly EntityHandle bot = ref _bots[i];
            if (!writer.IsAlive(in bot)) continue;

            // A dead bot sends nothing. The input handler would reject it anyway
            // (DeadEntity), and manufacturing rejections to be counted as anomalies is a
            // good way to make the anomaly counters useless.
            if (writer.HealthOf(in bot).Dead) continue;

            Vec2 from = writer.PositionOf(in bot).Value;
            string botId = writer.IdRefOf(in bot).Value;

            if (TryFindTarget(writer, enemyCount, in from, out Vec2 targetPos, out string? targetId))
            {
                float dx = targetPos.X - from.X;
                float dy = targetPos.Y - from.Y;
                float distSq = (dx * dx) + (dy * dy);

                // In range: attack, and stop moving. Moving while attacking is legal, but a
                // bot that keeps walking drifts through its target and spends half its
                // cooldowns out of range.
                if (distSq <= GameConstants.AttackRange * GameConstants.AttackRange)
                {
                    _decisions.Add(botId, 0f, 0f, targetId);
                    continue;
                }

                if (distSq > 0f)
                {
                    float inv = 1.0f / MathF.Sqrt(distSq);

                    // A DIRECTION, never a displacement: the server integrates
                    // direction x speed x dt itself. Sending anything longer than a unit
                    // vector here would be a bot cheating through the same door a client
                    // cannot, and the handler normalises it anyway.
                    _decisions.Add(botId, dx * inv, dy * inv, attackTargetId: null);
                    continue;
                }
            }

            // Nothing worth engaging: drift, so the map is not a car park. The direction is
            // derived from the tick and the bot's index rather than from Random.Shared, so
            // a replayed tick sequence produces the same wander — a test that asserts bot
            // movement must not be at the mercy of a shared RNG other systems also draw
            // from.
            float theta = ((currentTick * 0.013f) + (i * 2.39996323f)) % MathF.Tau;
            _decisions.Add(botId, MathF.Cos(theta), MathF.Sin(theta), attackTargetId: null);
        }
    }

    /// <summary>
    /// Nearest living enemy within <see cref="BotSettings.EngageRange"/>, or false.
    /// </summary>
    /// <remarks>
    /// A linear scan over the enemy handles, for the same reason
    /// <see cref="PlayerTargetBuffer.TryNearest"/> is one: the spatial grid would need a
    /// query object per bot per tick, and an allocation in the tick loop is the thing this
    /// codebase does not do. The range test is applied to the squared distance before
    /// anything else, so a bot on the far side of the map costs one subtract and one
    /// compare per enemy.
    /// </remarks>
    private bool TryFindTarget(
        WorldWriter writer, int enemyCount, in Vec2 from,
        out Vec2 position, out string? id)
    {
        position = default;
        id = null;

        float bestSq = _settings.EngageRangeSq;
        int best = -1;

        int n = Math.Min(enemyCount, _enemies.Length);
        for (int i = 0; i < n; i++)
        {
            ref readonly EntityHandle enemy = ref _enemies[i];
            if (!writer.IsAlive(in enemy)) continue;
            if (writer.HealthOf(in enemy).Dead) continue;

            Vec2 p = writer.PositionOf(in enemy).Value;
            float dx = p.X - from.X;
            float dy = p.Y - from.Y;
            float distSq = (dx * dx) + (dy * dy);

            if (distSq < bestSq)
            {
                bestSq = distSq;
                best = i;
                position = p;
            }
        }

        if (best < 0) return false;

        id = writer.IdRefOf(in _enemies[best]).Value;
        return true;
    }

    /// <summary>
    /// Refill a handle buffer, growing it with headroom. Exact-size growth re-queries
    /// again at count+1 (#249).
    /// </summary>
    private static int Fill(WorldWriter writer, ref EntityHandle[] buffer, bool bots)
    {
        int matches = bots
            ? writer.QueryWith<BotTag>(buffer)
            : writer.QueryWith<EnemyAi>(buffer);

        if (matches > buffer.Length)
        {
            buffer = new EntityHandle[matches + (matches >> 2) + 1];
            matches = bots
                ? writer.QueryWith<BotTag>(buffer)
                : writer.QueryWith<EnemyAi>(buffer);
        }

        return Math.Min(matches, buffer.Length);
    }
}

/// <summary>
/// The decisions one tick of <see cref="BotBrainSystem"/> produced, and the flush that
/// turns them into ordinary queued inputs once the world's write scope has closed.
/// </summary>
/// <remarks>
/// A class with reusable arrays rather than a list of records: the brain runs every world
/// tick over the whole bot population, and a per-bot allocation there would be a per-tick
/// allocation scaled by the very number this feature exists to raise.
/// </remarks>
internal sealed class BotDecisionBuffer
{
    private string[] _ids = Array.Empty<string>();
    private float[] _moveX = Array.Empty<float>();
    private float[] _moveY = Array.Empty<float>();
    private string?[] _attackTargets = Array.Empty<string>();
    private int _count;

    /// <summary>Decisions currently buffered.</summary>
    public int Count => _count;

    /// <summary>Move direction recorded for decision <paramref name="index"/>. Tests.</summary>
    public (string Id, float MoveX, float MoveY, string? AttackTargetId) this[int index] =>
        (_ids[index], _moveX[index], _moveY[index], _attackTargets[index]);

    /// <summary>Reset for a tick that will record at most <paramref name="capacity"/> decisions.</summary>
    public void Begin(int capacity)
    {
        if (_ids.Length < capacity)
        {
            int size = capacity + (capacity >> 2) + 1;
            _ids = new string[size];
            _moveX = new float[size];
            _moveY = new float[size];
            _attackTargets = new string?[size];
        }

        _count = 0;
    }

    public void Clear() => _count = 0;

    public void Add(string id, float moveX, float moveY, string? attackTargetId)
    {
        if (_count >= _ids.Length) return;

        _ids[_count] = id;
        _moveX[_count] = moveX;
        _moveY[_count] = moveY;
        _attackTargets[_count] = attackTargetId;
        _count++;
    }

    /// <summary>
    /// Queue every buffered decision as an ordinary input.
    /// </summary>
    /// <remarks>
    /// <para><c>ingress: null</c> is the documented path for a caller with no connection.
    /// It skips the per-connection budget and the movement coalescing, both of which exist
    /// to bound what a <i>socket</i> can do to the queue; a bot emits exactly one input per
    /// world tick by construction, so there is nothing to bound.</para>
    ///
    /// <para>The input's tick is the current tick, which satisfies the handler's monotonic
    /// check: the brain runs once per world tick, so each bot's successive inputs carry
    /// strictly increasing ticks.</para>
    ///
    /// <para>A dropped input (the world-wide queue bound) is deliberately not retried and
    /// not counted as an error. The queue being full means real players are saturating it,
    /// and a bot that fought harder for queue space under load would be a load-shedding
    /// mechanism working backwards.</para>
    /// </remarks>
    public void Flush(EcsWorld world, ulong currentTick)
    {
        for (int i = 0; i < _count; i++)
        {
            world.PushInput(
                _ids[i],
                new InputData(currentTick, _moveX[i], _moveY[i], _attackTargets[i]),
                ingress: null);
        }

        _count = 0;
    }
}
