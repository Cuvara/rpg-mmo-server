using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.World;

namespace GameServer.AI;

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
public sealed class EnemySpawner
{
    private readonly EcsWorld _world;
    private readonly EnemySpawnSystem _spawn;
    private readonly EnemyMoveSystem _move;
    private readonly EnemyReapSystem _reap;

    /// <summary>
    /// Reusable buffer of enemy handles for this tick. Owned here so the per-tick query
    /// allocates nothing once the population has stabilised; it grows to the high-water
    /// mark and stays, which is bounded by <see cref="EnemyAiTuning.MaxEnemies"/>.
    /// </summary>
    private EntityHandle[] _enemies = Array.Empty<EntityHandle>();

    /// <summary>
    /// The scope callback, built once. A lambda written inline at the call site captures
    /// <c>this</c> and so allocates a delegate on every tick; hoisting it to a field is
    /// the difference between the AI phase allocating per tick and not.
    /// </summary>
    private readonly Action<WorldWriter> _runPhases;

    public EnemySpawner(EcsWorld world, int tickRate, ILogger logger)
    {
        _world = world;
        float dt = 1.0f / tickRate;

        _spawn = new EnemySpawnSystem(dt, logger);
        _move = new EnemyMoveSystem(dt);
        _reap = new EnemyReapSystem(logger);
        _runPhases = RunPhases;
    }

    /// <summary>
    /// Number of enemies currently alive. A count over the <c>EnemyAi</c> archetype
    /// rather than the length of a hand-maintained id list, so it is answered by the
    /// world itself and cannot disagree with it.
    /// </summary>
    public int AliveCount => _world.EnemyCount;

    /// <summary>
    /// Run one tick of enemy AI. Takes the world write scope itself; the tick loop calls
    /// this directly rather than wrapping it, because the phases have to share one scope
    /// for the reap to land before snapshots are built.
    /// </summary>
    /// <remarks>
    /// Structural changes raised inside the scope — spawns from
    /// <see cref="EnemySpawnSystem"/>, despawns from <see cref="EnemyReapSystem"/> — are
    /// applied by the world's deferred structural phase, which
    /// <see cref="EcsWorld.UpdateComponents"/> drains on the way out. That is the ADR-11
    /// substitute for Arch's <c>CommandBuffer</c>, and it is why the removals are visible
    /// to the snapshot broadcast later in the same tick without the old dance of
    /// collecting ids and draining them after the lock was released.
    /// </remarks>
    public void Tick(ulong currentTick)
    {
        _world.UpdateComponents(_runPhases);
    }

    private void RunPhases(WorldWriter writer)
    {
        // Phase 1 — Spawn. Before anything iterates, so new enemies are steppable
        // this tick and their creation takes the immediate path.
        _spawn.Run(writer, AliveCountLocked(writer));

        // The population for the rest of the tick, captured once. Count-don't-
        // saturate: a short buffer reports what it needed and we retry at the right
        // size rather than processing a prefix and stranding the remainder.
        int count = writer.QueryEnemies(_enemies);
        if (count > _enemies.Length)
        {
            _enemies = new EntityHandle[count];
            count = writer.QueryEnemies(_enemies);
        }

        var enemies = new ReadOnlySpan<EntityHandle>(_enemies, 0, Math.Min(count, _enemies.Length));

        // Phase 2 — Move.
        _move.Run(writer, enemies);

        // Phase 3 — Reap. After Move, because "arrived at the centre" is a fact Move
        // produced this tick.
        _reap.Run(writer, enemies);
    }

    /// <summary>
    /// Enemy population as seen from inside the write scope. <see cref="AliveCount"/>
    /// takes a read lock and would deadlock against the write lock we already hold.
    /// </summary>
    private int AliveCountLocked(WorldWriter writer) => writer.QueryEnemies(Span<EntityHandle>.Empty);
}
