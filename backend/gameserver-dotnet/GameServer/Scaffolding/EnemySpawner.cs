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
///   <item>Spawns waves of "mob" entities from the map edges.</item>
///   <item>Moves every living enemy toward the map center each tick.</item>
///   <item>Despawns enemies that reach the center zone near (0,0), and reaps dead ones.</item>
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
/// <para>There is no "center-zone damage" phase. The old class comment and the tick
/// loop's comment both claimed one; no code ever implemented it. Nothing was removed
/// here — see the CHANGELOG.</para>
/// </summary>
public sealed class EnemySpawner : ISimulationPhase
{
    private readonly EcsWorld _world;
    private readonly SystemSchedule _schedule;

    /// <summary>
    /// The scope callback, built once. A lambda written inline at the call site captures
    /// <c>this</c> and so allocates a delegate on every tick.
    /// </summary>
    private readonly Action<ulong, WorldWriter> _runSchedule;

    public EnemySpawner(EcsWorld world, int tickRate, ILogger logger)
    {
        _world = world;
        float dt = 1.0f / tickRate;

        // Ordering is declared by each system's Order, not by the order of these
        // arguments — pass them in any order and the schedule still runs
        // spawn -> move -> reap. That is the difference from the previous shape, where
        // ordering was three method calls in a private method and nothing said so.
        _schedule = new SystemSchedule(
            new EnemySpawnSystem(dt, logger),
            new EnemyMoveSystem(dt),
            new EnemyReapSystem(logger));

        _runSchedule = (tick, writer) => _schedule.Run(writer, tick);
    }

    /// <summary>
    /// Number of enemies currently alive. A count over the <c>EnemyAi</c> archetype, so
    /// it is answered by the world and cannot drift from it.
    /// </summary>
    public int AliveCount => _world.CountWith<EnemyAi>();

    /// <summary>The systems this phase runs, in the order they run. Diagnostics and tests.</summary>
    public IReadOnlyList<IEcsSystem> Systems => _schedule.Systems;

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
    public void Tick(ulong currentTick) => _world.UpdateComponents(currentTick, _runSchedule);
}
