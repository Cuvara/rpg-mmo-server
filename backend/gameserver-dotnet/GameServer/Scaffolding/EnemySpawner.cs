using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Server;
using GameServer.World;
using GameServer.World.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// Server-authoritative enemy AI: the schedule that runs
/// <see cref="EnemySpawnSystem"/>, <see cref="EnemyMoveSystem"/> and
/// <see cref="EnemyReapSystem"/> in <see cref="EnemyAiPhase"/> order, once per tick,
/// inside one world write scope.
///
/// <list type="bullet">
///   <item>Spawns waves of "mob" entities around the live players, or on a ring about
///     the origin when nobody is online.</item>
///   <item>Moves every living enemy toward the nearest live player each tick, or toward
///     the origin when there is none.</item>
///   <item>Reaps dead enemies, and despawns enemies that reach the centre zone near
///     (0,0) while there is nothing to chase.</item>
/// </list>
///
/// <para>Enemies are regular <see cref="EntityState"/> entries with
/// <c>Type = "mob"</c>, so the snapshot encoder, delta encoder and client renderer
/// handle them with zero protocol changes. They additionally carry the
/// <c>EnemyAi</c> archetype tag, which is what the systems query on — see that type for
/// why the tag exists rather than a <c>Type == "mob"</c> test.</para>
///
/// <para><b>What this replaced.</b> A single <c>Tick(get, set, tick)</c> method that
/// walked a <c>List&lt;string&gt;</c> of enemy ids, resolved each id through the world's
/// string index, composed a whole <see cref="EntityState"/>, mutated the copy and wrote
/// all seven components back — every enemy, every tick. The id list was a second source
/// of truth for "which entities are enemies" that had to be kept in step with the world
/// by hand; the archetype query cannot drift from the world because it <i>is</i> the
/// world.</para>
///
/// <para>Enemies also fight back: <see cref="EnemyAttackSystem"/> decides which of them
/// swing at which player, and <see cref="PlayerRespawnSystem"/> gives HP reaching 0 a
/// defined end. Neither implements any combat — the decision becomes ordinary queued
/// input and <c>InputHandler</c> resolves it down the one combat path this server has, the
/// same route <see cref="BotBrainSystem"/> takes. See <see cref="EnemyAiPhase.Attack"/>
/// for why a fourth and fifth phase are the right place for them.</para>
///
/// <para>There is no "center-zone damage" phase. The old class comment and the tick
/// loop's comment both claimed one; no code ever implemented it. Nothing was removed
/// here — see the CHANGELOG. The enemy damage that exists now is not that: it is
/// range-based and aimed at a player, not a function of standing near (0,0).</para>
/// </summary>
public sealed class EnemySpawner : ISimulationPhase
{
    private readonly EcsWorld _world;
    private readonly SimulationSchedule _schedule;
    private readonly EnemyAiSettings _settings;

    /// <summary>
    /// The scope callback, built once. A lambda written inline at the call site captures
    /// <c>this</c> and so allocates a delegate on every tick.
    /// </summary>
    private readonly Action<ulong, WorldWriter> _runSchedule;

    /// <summary>
    /// Per-group observer, supplied at construction and never reassigned. It is readonly
    /// deliberately: <c>SimulationStateArchitectureTests</c> forbids mutable instance state
    /// on a phase, and a settable observer is exactly that — a field whose value changes
    /// the phase's behaviour while being invisible to the world.
    /// </summary>
    private readonly Action<SimulationGroup, long, long>? _onGroupRan;

    /// <summary>
    /// Attack decisions this tick, flushed as ordinary input after the write scope closes.
    /// Scratch in the strict sense — filled and drained within one <see cref="Tick"/> —
    /// and the same type <see cref="BotPlayerSpawner"/> uses, because it is the same job:
    /// an entity with no connection deciding to attack. A second buffer of the same shape
    /// would be a second place for the "decide inside the scope, push outside it" rule to
    /// be got wrong.
    /// </summary>
    [SimulationScratch]
    private readonly BotDecisionBuffer _decisions = new();

    /// <summary>Counters for <c>/status</c>. See <see cref="EnemyAttackStats"/>.</summary>
    private readonly EnemyAttackStats _attacks = new();

    /// <summary>
    /// Single-rate construction: every group runs at <paramref name="tickRate"/>, which is
    /// the pre-multi-rate server exactly. Kept because it is what the characterization and
    /// byte-identity tests construct, and their whole value is that they were not rewritten
    /// to accommodate this change.
    /// </summary>
    public EnemySpawner(EcsWorld world, int tickRate, ILogger logger)
        : this(world, SimulationRates.Uniform(tickRate), logger)
    {
    }

    /// <summary>
    /// Single-rate construction with explicit settings. Tests that want a particular
    /// population or a particular placement use this rather than setting environment
    /// variables, which a test process shares with every other test in it.
    /// </summary>
    public EnemySpawner(EcsWorld world, int tickRate, EnemyAiSettings settings, ILogger logger)
        : this(world, SimulationRates.Uniform(tickRate), logger, settings: settings)
    {
    }

    /// <param name="onGroupRan">
    /// Optional observer called once per group that runs, with the group and the
    /// start/end <see cref="System.Diagnostics.Stopwatch"/> timestamps. The host uses it to
    /// record per-group metrics without this type depending on the metric set.
    /// </param>
    /// <param name="settings">
    /// Every tuning knob the AI has. Null means <see cref="EnemyAiSettings.Default"/>,
    /// which is the compiled-in set and, on a world with no live players, the behaviour
    /// this class had before it was configurable.
    /// </param>
    public EnemySpawner(
        EcsWorld world,
        SimulationRates rates,
        ILogger logger,
        Action<SimulationGroup, long, long>? onGroupRan = null,
        EnemyAiSettings? settings = null)
    {
        _world = world;
        _onGroupRan = onGroupRan;
        _settings = settings ?? EnemyAiSettings.Default;

        // dt is the WORLD group's timestep, not the base tick's, because that is the rate
        // these systems actually run at. This is the rule the whole design turns on: a
        // system integrates with the dt of its own group, so `EnemySpeed` stays in world
        // units per second whatever the base rate is. Handing them 1/BaseHz while running
        // them every fourth tick is precisely the bug the time model exists to prevent.
        float dt = rates.DeltaTimeFor(SimulationGroup.World);

        // Ordering is declared by each system's Order, not by the order of these
        // arguments — pass them in any order and the schedule still runs
        // spawn -> move -> reap. That is the difference from the previous shape, where
        // ordering was three method calls in a private method and nothing said so.
        _schedule = new SimulationSchedule(
            rates,
            new EnemySpawnSystem(dt, _settings, logger),
            new EnemyMoveSystem(dt, _settings),
            new EnemyAttackSystem(rates, _settings, _decisions, _attacks),
            // WorldEvery as the one-shot hold, for the reason GameServer passes it to
            // InputHandler: an action written on one group and sampled on another has to
            // survive one sampling period or it reaches the wire on a coin flip.
            new PlayerRespawnSystem(_settings, _attacks, rates.WorldEvery, logger),
            new EnemyReapSystem(_settings, logger));

        _runSchedule = (tick, writer) => _schedule.RunDue(writer, tick, _onGroupRan);
    }

    /// <summary>
    /// Number of enemies currently alive. A count over the <c>EnemyAi</c> archetype, so
    /// it is answered by the world and cannot drift from it.
    /// </summary>
    public int AliveCount => _world.CountWith<EnemyAi>();

    /// <summary>
    /// The tuning in force. Published so <c>/status</c> can report what this server is
    /// actually doing rather than what the manifest that was supposed to have produced it
    /// says — an already-allocated GameServer does not necessarily reflect that manifest,
    /// since its environment is fixed at pod creation.
    /// </summary>
    public EnemyAiSettings Settings => _settings;

    /// <summary>
    /// What enemy-side combat has done. Published for <c>/status</c>; see
    /// <see cref="EnemyAttackStats"/> for why the throttle counter in particular is worth
    /// an endpoint field.
    /// </summary>
    public EnemyAttackStats Attacks => _attacks;

    /// <summary>The systems this phase runs, in the order they run. Diagnostics and tests.</summary>
    public IReadOnlyList<IEcsSystem> Systems => _schedule.SystemsIn(SimulationGroup.World);

    /// <summary>The schedule this phase runs. Diagnostics and tests.</summary>
    public SimulationSchedule Schedule => _schedule;

    /// <summary>
    /// Run one tick of enemy AI: the whole schedule inside one world write scope.
    /// </summary>
    /// <remarks>
    /// One scope for all systems, not one per system, because the deferred structural
    /// drain happens on the way out of a scope — spawns and despawns must land once, at
    /// the end of the phase and still before snapshots are built.
    /// </remarks>
    /// <remarks>
    /// The tick is passed through the scope rather than stashed in a field: a phase that
    /// held <c>_currentTick</c> between calls would be keeping simulation state in a
    /// class, which is the thing <c>SimulationStateArchitectureTests</c> now forbids.
    /// </remarks>
    /// <remarks>
    /// Skips the write scope entirely on a base tick where no group of this phase is due.
    /// At the default 60/15/5 that is three ticks in four, and taking the world write lock
    /// on them to run nothing would be pure contention against the network threads.
    /// </remarks>
    public void Tick(ulong currentTick)
    {
        if (!_schedule.AnyDue(currentTick)) return;
        _world.UpdateComponents(currentTick, _runSchedule);

        // Outside the scope, deliberately and not as a tidiness choice: PushInput takes
        // the world's read lock to resolve the id, the lock is not recursive, and calling
        // it from inside UpdateComponents throws. The one-tick delay between deciding and
        // striking is the delay a real client has — see BotPlayerSpawner, which pays it
        // for the same reason.
        _decisions.Flush(_world, currentTick);
    }
}
