using GameServer.Gameplay;
using GameServer.Observability;
using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using GameServer.World.Components;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// The tick-thread half of <c>POST /debug/kill</c>: takes a queued player's HP to 0 through
/// <see cref="CombatResolver.ApplyDamage"/>, the same call a damage-over-time tick makes, so the
/// <c>Damage</c> and <c>Death</c> events, the <c>Dead</c> action, the death callback and
/// <see cref="CombatResolver.AfterDeath"/> all happen exactly as in play. Nothing is despawned;
/// whatever brings a dead player back (<see cref="PlayerRespawnSystem"/>) does so unaided.
/// </summary>
/// <remarks>
/// <para><b>Why a queue of its own and not the input queue.</b> The input queue carries client
/// input semantics: per-entity coalescing to the newest input, the monotonic
/// <c>LastInputTick</c> check and the ingest budget. A kill pushed there would either be
/// refused as a stale tick or advance the player's input cursor and supersede the real input
/// queued for the same tick. The request is therefore left in a <see cref="DebugKillQueue"/> by
/// the HTTP thread and applied by <see cref="Tick"/>, which the tick loop calls on the tick thread; the HTTP thread
/// never touches the world.</para>
///
/// <para><b>Order.</b> Composed LAST in the simulation phase, after the enemy phase, so a kill
/// lands after this tick's <c>player.respawn</c> run and the player is revived by the NEXT
/// World-group run: dead for at most one world interval (<c>SimulationRates.WorldEvery</c> base
/// ticks), which is what an enemy kill gives. As with an enemy kill, the <c>Death</c> event is
/// the guaranteed observable; a snapshot samples the <c>Dead</c> state only when the kill lands
/// on a World tick.</para>
///
/// <para><b>Cost when idle.</b> One <see cref="DebugKillQueue.IsEmpty"/> read per base tick: no
/// lock, no allocation. Only a pending request takes the world write lock.</para>
///
/// <para>Dev/test only. Wired solely when <see cref="ServerEnv.DebugEntities"/> is on, which no
/// fleet manifest sets.</para>
/// </remarks>
public sealed class DebugKillPhase : ISimulationPhase
{
    private readonly EcsWorld _world;
    private readonly DebugKillQueue _queue;
    private readonly Func<CombatResolver?> _combat;
    private readonly ILogger _logger;

    /// <summary>Builds the phase.</summary>
    /// <param name="world">The world the kills apply to.</param>
    /// <param name="queue">Where the HTTP thread leaves requests.</param>
    /// <param name="combat">
    /// The input handler's resolver, read on the tick thread. A function rather than the value
    /// because the phase is built inside the server's constructor, before the composition root
    /// can read the resolver back; until it returns non-null, requests wait.
    /// </param>
    /// <param name="logger">Logger.</param>
    public DebugKillPhase(EcsWorld world, DebugKillQueue queue, Func<CombatResolver?> combat, ILogger logger)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Tick(ulong currentTick)
    {
        if (_queue.IsEmpty) return;

        CombatResolver? combat = _combat();
        if (combat == null) return;

        _world.UpdateComponents((Self: this, Combat: combat, Tick: currentTick),
            static (s, writer) => s.Self.Drain(writer, s.Combat, s.Tick));
    }

    private void Drain(WorldWriter writer, CombatResolver combat, ulong tick)
    {
        while (_queue.TryTake(out DebugKillQueue.Request? request))
        {
            request.Complete(Apply(writer, combat, request.Id, tick));
        }
    }

    private DebugKillResult Apply(WorldWriter writer, CombatResolver combat, string id, ulong tick)
    {
        EntityHandle target = writer.Resolve(id);
        if (!target.IsValid) return new DebugKillResult(DebugKillOutcome.NotFound, tick, 0);

        EntityState state = writer.Compose(target);
        if (state.Type != "player") return new DebugKillResult(DebugKillOutcome.NotAPlayer, tick, 0);

        ref Health health = ref writer.HealthOf(target);
        if (health.Dead) return new DebugKillResult(DebugKillOutcome.AlreadyDead, tick, 0);

        int before = health.Hp;

        // Exactly the HP the player has (at least 1, so a 0-HP-but-alive entity still crosses
        // the death check): the Damage event reports the damage that killed, nothing more. No
        // source, as for a damage-over-time tick whose source has gone.
        combat.ApplyDamage(
            writer, null, PendingGameEvent.NoKey, default, target, writer.IdRefOf(target).Stable,
            Math.Max(before, 1), 0u, GameEventFlags.None, 0u, tick);

        _logger.LogInformation("Debug kill of {Id} at tick {Tick} (HP {Hp} -> 0)", id, tick, before);
        return new DebugKillResult(DebugKillOutcome.Killed, tick, before);
    }
}
