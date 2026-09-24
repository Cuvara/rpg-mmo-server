using System;
using Microsoft.Extensions.Logging;
using GameServer.Persistence;
using GameServer.Server;
using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace GameServer.Scaffolding;

/// <summary>
/// What enemy-side combat did, for <c>/status</c>. A class held by a <c>readonly</c>
/// field rather than counters on the systems themselves, because a simulation system may
/// hold no mutable instance state (ADR-12, <c>SimulationStateArchitectureTests</c>) — the
/// same shape, and the same reason, as <c>InputHandler.AttackStats</c>.
/// </summary>
public sealed class EnemyAttackStats
{
    /// <summary>Attack decisions emitted as input. Not kills, and not landed hits.</summary>
    /// <remarks>
    /// The gap between this and <c>attacks_accepted</c> is the honest measure of how often
    /// an enemy decided to swing at a player another enemy killed first: the decision is
    /// made on the world tick and validated on the next critical tick, and a target that
    /// died in between is refused there.
    /// </remarks>
    public long Decided;

    /// <summary>
    /// Attacks NOT emitted because the target had already taken its window's worth.
    ///
    /// <para>The load-bearing counter of this whole feature. It is the difference between
    /// "the enemies are not attacking" and "the enemies are attacking and the cap is
    /// holding", which are indistinguishable from a player's HP bar and from every other
    /// field on the endpoint. A demo where this is zero with a crowd on a player means the
    /// cap is not the thing limiting the fight.</para>
    /// </summary>
    public long Throttled;

    /// <summary>Players returned to the map after reaching 0 HP.</summary>
    public long Respawns;

    /// <summary>Most recent tick on which any enemy attack was decided. 0 when none has been.</summary>
    public ulong LastDecisionTick;
}

/// <summary>
/// <see cref="EnemyAiPhase.Attack"/> — decides which enemies swing at which player this
/// tick, and at what rate a player may be hit at all.
/// </summary>
/// <remarks>
/// <para><b>No combat is implemented here.</b> The decision is turned into an ordinary
/// <see cref="InputData"/> carrying an attack target and pushed through
/// <see cref="EcsWorld.PushInput"/>, exactly as <see cref="BotBrainSystem"/> does — so the
/// damage, the range and cooldown validation, the <c>Damage</c> and <c>Death</c> game
/// events, the <c>Attacking</c> action, the kill counters and the death callback all
/// happen on the next critical tick inside <c>InputHandler</c>, down the one combat path
/// this server has. <c>CombatLogic.CalculateDamage</c> is therefore reached by exactly the
/// route a player's attack reaches it, which is what ADR-10 requires: a second damage
/// formula here would be a server that disagreed with the client that compiles the
/// first.</para>
///
/// <para><b>Handle-based rather than a chunk walk</b>, and for the reason
/// <see cref="BotBrainSystem"/> gives: the decision needs the enemy's <i>id string</i>,
/// because that is what <see cref="EcsWorld.PushInput"/> addresses an input to, and
/// <see cref="SimChunk"/> carries components, not identity. It also needs the target's id
/// and the enemy's <see cref="Combat"/>, neither of which the chunk view exposes.</para>
///
/// <para><b>The survivability mechanism, and why it is not simply a smaller number.</b>
/// Lowering <c>GAMESERVER_ENEMY_ATTACK</c> does not bound anything: damage is floored at
/// <c>GameConstants.MinDamage</c>, so three hundred enemies attacking a player deal at
/// least three hundred damage per round however weak each one is, and the player dies at
/// the floor. A per-enemy cooldown does not bound it either — three hundred enemies each
/// respecting the same 500ms cooldown still deliver three hundred hits every 500ms. The
/// only thing that bounds what a target takes is a limit expressed on the TARGET, so that
/// is what this is: at most <see cref="EnemyAiSettings.AttackersPerTarget"/> attacks are
/// emitted against one player per <see cref="EnemyAiSettings.AttackIntervalSec"/>, and a
/// player surrounded by 327 enemies takes exactly what a player surrounded by 3
/// takes.</para>
///
/// <para><b>Why the window is a global phase rather than per-target state.</b> A budget
/// that refilled on a per-target schedule would be simulation state carried between ticks,
/// which belongs on a component and would cost one for a rule that does not need it. The
/// window is derived from the tick instead: the whole system runs on the world ticks where
/// <c>worldIndex % windowWorldTicks == 0</c> and is a no-op on every other, so the budget
/// is filled and spent inside one run and carries nothing. The cost of that choice is that
/// every player's window opens on the same tick, so a server with twelve players produces
/// its damage in a pulse rather than a smear. That is stated rather than hidden; it is
/// bounded (at the ceilings, 1000 x players events, against
/// <c>TickEventBuffer.Capacity</c>) and it is what a player sees anyway at these
/// rates.</para>
/// </remarks>
internal sealed class EnemyAttackSystem : IEcsSystem
{
    private readonly EnemyAiSettings _settings;
    private readonly BotDecisionBuffer _decisions;
    private readonly EnemyAttackStats _stats;

    /// <summary>Base ticks per world tick, for turning the base tick into a world index.</summary>
    private readonly int _worldEvery;

    /// <summary>
    /// Length of one target's damage window in WORLD ticks, at least 1.
    ///
    /// <para>Computed once at construction from the configured interval and the world
    /// rate, and rounded UP for the same reason <c>GameConstants.AttackCooldownTicks</c>
    /// rounds up: a window shorter than the interval the operator configured delivers more
    /// damage per second than the number they wrote, and this is the number the whole
    /// survivability claim is made against.</para>
    /// </summary>
    private readonly int _windowWorldTicks;

    /// <summary>Live players, refilled every run. See <see cref="PlayerTargetBuffer"/>.</summary>
    [SimulationScratch]
    private readonly PlayerTargetBuffer _players = new();

    /// <summary>Enemy handles, refilled from a query before every read.</summary>
    [SimulationScratch]
    private EntityHandle[] _handles = Array.Empty<EntityHandle>();

    /// <summary>
    /// Attacks still allowed against live player <c>i</c> this window. Refilled from the
    /// cap at the top of every run, so it carries nothing across ticks — which is the
    /// claim <see cref="SimulationScratchAttribute"/> makes and it is true here because
    /// the system only runs on the tick a window opens.
    /// </summary>
    [SimulationScratch]
    private int[] _budget = Array.Empty<int>();

    public EnemyAttackSystem(
        SimulationRates rates, EnemyAiSettings settings,
        BotDecisionBuffer decisions, EnemyAttackStats stats)
    {
        _settings = settings;
        _decisions = decisions;
        _stats = stats;
        _worldEvery = rates.WorldEvery < 1 ? 1 : rates.WorldEvery;

        int worldHz = rates.WorldHz < 1 ? 1 : rates.WorldHz;
        int ticks = (int)MathF.Ceiling(settings.AttackIntervalSec * worldHz);
        _windowWorldTicks = ticks < 1 ? 1 : ticks;
    }

    public string Name => "enemy.attack";

    public int Order => (int)EnemyAiPhase.Attack;

    /// <summary>
    /// World. An enemy's swing is not prediction-relevant — no client predicts one — and
    /// deciding on the critical group would cost a scan of the whole population four times
    /// per world tick to produce decisions the window throws away three times in four.
    /// </summary>
    public SimulationGroup Group => SimulationGroup.World;

    /// <summary>
    /// Reads only. Nothing is written to the world here: the decision becomes input, and
    /// the input is what writes. Declaring it honestly is the point of these sets.
    /// </summary>
    public ComponentAccess Access => new(
        reads: new[] { typeof(Position), typeof(Health), typeof(Combat) });

    public void Run(WorldWriter writer, ulong currentTick)
    {
        if (!_settings.AttacksEnabled || _settings.AttackersPerTarget <= 0) return;

        // The window gate, counted in WORLD ticks so the same configuration delivers the
        // same damage per second whatever the base rate is — counting base ticks would
        // make a 60Hz server four times deadlier than a 15Hz one on identical settings.
        //
        // The `- 1` matches SimulationRates.RunsOn, which schedules a group on
        // `(baseTick - 1) % every == 0`: the world group's ticks are 1, 1+every, 1+2*every,
        // … so the index that counts them starts where they start. Stated honestly, this
        // is an alignment and not a rate: dividing the raw tick instead yields the same
        // consecutive indices and therefore the same damage per second, and changes only
        // WHICH world tick opens the first window. It is written this way because an index
        // derived differently from the schedule it indexes is a discrepancy waiting to
        // matter, not because anything today can tell the two apart — no test here fails
        // on that mutation, and the mutation table in the PR records it as surviving.
        ulong worldIndex = (currentTick > 0 ? currentTick - 1 : 0) / (ulong)_worldEvery;
        if (worldIndex % (ulong)_windowWorldTicks != 0) return;

        int livePlayers = _players.Refresh(writer);
        if (livePlayers == 0) return;

        if (_budget.Length < livePlayers)
        {
            // Headroom on growth, not exact size (#249).
            _budget = new int[livePlayers + (livePlayers >> 2) + 1];
        }
        Array.Fill(_budget, _settings.AttackersPerTarget, 0, livePlayers);

        int count = writer.QueryWith<EnemyAi>(_handles);
        if (count > _handles.Length)
        {
            _handles = new EntityHandle[count + (count >> 2)];
            count = writer.QueryWith<EnemyAi>(_handles);
        }

        int n = Math.Min(count, _handles.Length);

        // Sized to the most decisions this run can produce, which is the cap times the
        // live players and NOT the enemy population: the budget is what bounds the output,
        // so sizing on the population would allocate a buffer scaled by the very number
        // the cap exists to decouple the cost from. Begin also resets the count, and
        // skipping it is not a smaller version of this call — the buffer starts at length
        // zero and every Add against it is a silent no-op, which reads as enemies that
        // decide to attack and never swing.
        long room = (long)livePlayers * _settings.AttackersPerTarget;
        _decisions.Begin((int)Math.Min(room, n));

        for (int i = 0; i < n; i++)
        {
            ref readonly EntityHandle enemy = ref _handles[i];
            if (!writer.IsAlive(in enemy)) continue;

            // A dead enemy emits nothing. The handler would refuse it (DeadEntity) and
            // manufacturing rejections to be counted as anomalies is how the anomaly
            // counters stop meaning anything — the rule BotBrainSystem states.
            if (writer.HealthOf(in enemy).Dead) continue;

            Vec2 from = writer.PositionOf(in enemy).Value;
            if (!_players.TryNearestIndex(in from, out int target, out float distSq)) continue;

            // Cheap reject before anything is composed. GameConstants.AttackRange is the
            // range the handler will apply, so measuring anything else here would emit
            // input that is refused on arrival.
            if (distSq > GameConstants.AttackRange * GameConstants.AttackRange) continue;

            // The cap, checked BEFORE the composes below: a throttled enemy must cost a
            // compare, not two EntityState materialisations, or the cheap case is the one
            // that scales with the population.
            if (_budget[target] <= 0)
            {
                _stats.Throttled++;
                continue;
            }

            EntityHandle victim = _players.HandleAt(target);

            // The authoritative gate, and deliberately the SAME function the handler will
            // apply rather than a restatement of its conditions: dead target, range and
            // cooldown all come from CombatLogic. Reached only for an enemy that has
            // already passed the squared-distance filter and holds a budget slot, so the
            // composes are bounded by the cap times the player count, not by the
            // population.
            EntityState attacker = writer.Compose(in enemy);
            EntityState defender = writer.Compose(in victim);

            // Validated against the tick the input will be PROCESSED on, not the tick the
            // decision is made on, and the off-by-one is load-bearing rather than
            // cosmetic. The decision is flushed after this scope and drained at the top of
            // the next base tick, so the handler charges the cooldown from currentTick + 1
            // — and a cooldown of exactly one window then expires one tick after the
            // window that should spend it. Checking at currentTick refuses every second
            // window and silently halves the damage rate this system's whole survivability
            // claim is stated against, with nothing failing anywhere: the enemies simply
            // attack half as often as the configuration says.
            if (CombatLogic.ValidateAttack(in attacker, in defender, currentTick + 1) != null) continue;

            _budget[target]--;
            _stats.Decided++;
            _stats.LastDecisionTick = currentTick;

            // Move (0,0) — an enemy's position is owned by EnemyMoveSystem, and an input
            // that also carried a direction would integrate a second step for it on the
            // critical group at a dt this system never reasoned about.
            _decisions.Add(
                writer.IdRefOf(in enemy).Value,
                0f, 0f,
                writer.IdRefOf(in victim).Value);
        }
    }
}

/// <summary>
/// <see cref="EnemyAiPhase.Respawn"/> — returns players whose HP reached 0 to the map.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all, and what it is not.</b> Enemy attacks make player
/// death reachable for the first time, and what this codebase does today when a player's
/// HP reaches 0 is not a design — it is an absence. Nothing reaps a dead player
/// (<see cref="EnemyReapSystem"/> queries <c>EnemyAi</c>); <c>InputHandler</c> refuses
/// every input from it for the life of the process; <c>PlayerTargetBuffer</c> stops
/// counting it, so the enemies that killed it wander off; and <c>AsyncSaver</c> persists
/// <c>hp = 0</c>, which <c>PlayerSpawn.Resolve</c> restores verbatim on the next join —
/// there is no <c>dead</c> column, so the character comes back at 0 HP on any server,
/// permanently (docs/DESIGN.md, "Known gap"). Shipping enemy attacks without this would
/// make that reachable in a demo.</para>
///
/// <para><b>It is the smallest correct behaviour, not a death system.</b> There is no
/// death screen, no timed respawn, no corpse, no penalty and no schema change. A timed
/// respawn needs a per-entity tick to count down to, which is a component field and a wire
/// consideration; that is a larger change and is deliberately not made here. What this
/// guarantees is only that HP reaching 0 has a defined end: the player is dead for at most
/// one world tick — long enough for the <c>Death</c> event and the <c>Dead</c> action to
/// be sampled by a snapshot, which is what makes the death observable — and is then back
/// at the map's spawn point at full HP.</para>
///
/// <para><b>Why it lives in the enemy AI phase.</b> It is not enemy AI. It is here because
/// this phase is what made player death reachable, because it must run in the same world
/// write scope after the thing that kills, and because a separate phase for one system
/// would put the ordering that matters into the composition root where nothing states it.
/// The system is named <c>player.respawn</c> rather than <c>enemy.*</c> so a schedule dump
/// does not claim otherwise.</para>
///
/// <para><b>It covers every cause of death, not only enemies</b> — it asks the world who
/// is dead rather than being told by whatever killed them. A player killed by another
/// player comes back the same way.</para>
/// </remarks>
internal sealed class PlayerRespawnSystem : IEcsSystem
{
    private readonly EnemyAiSettings _settings;
    private readonly EnemyAttackStats _stats;
    private readonly ILogger _logger;
    private readonly int _oneShotHoldTicks;

    [SimulationScratch]
    private EntityHandle[] _handles = Array.Empty<EntityHandle>();

    public PlayerRespawnSystem(
        EnemyAiSettings settings, EnemyAttackStats stats, int oneShotHoldTicks, ILogger logger)
    {
        _settings = settings;
        _stats = stats;
        _oneShotHoldTicks = oneShotHoldTicks;
        _logger = logger;
    }

    public string Name => "player.respawn";

    public int Order => (int)EnemyAiPhase.Respawn;

    public SimulationGroup Group => SimulationGroup.World;

    public ComponentAccess Access => new(
        reads: new[] { typeof(Health) },
        writes: new[] { typeof(Position), typeof(Health), typeof(Locomotion) });

    public void Run(WorldWriter writer, ulong currentTick)
    {
        if (!_settings.RespawnPlayers) return;

        int count = writer.QueryWith<PlayerTag>(_handles);
        if (count > _handles.Length)
        {
            _handles = new EntityHandle[count + (count >> 2) + 1];
            count = writer.QueryWith<PlayerTag>(_handles);
        }

        int n = Math.Min(count, _handles.Length);
        for (int i = 0; i < n; i++)
        {
            ref readonly EntityHandle player = ref _handles[i];
            if (!writer.IsAlive(in player)) continue;

            ref Health health = ref writer.HealthOf(in player);
            if (!health.Dead) continue;

            // MaxHp, not ServerDefaults.DefaultPlayerHp: the entity's own maximum is what
            // the join path restored from the persisted row, and reviving to a constant
            // would silently re-stat anybody whose maximum is not the default.
            health.Hp = health.MaxHp > 0 ? health.MaxHp : 1;
            health.Dead = false;

            // The same point the join path uses, clamped by the same bounds — a respawn
            // that put a player somewhere a join could not is a second placement policy.
            writer.PositionOf(in player).Value = _settings.Bounds.Clamp(PlayerSpawn.SpawnPoint);

            // Idle, not Unspecified: Dead is terminal for the action field
            // (ActionTransitions.IsTerminal), so something has to take the entity out of
            // it or a revived player renders as a corpse that walks.
            ActionTransitions.Enter(
                ref writer.LocomotionOf(in player), SimAction.Idle, currentTick, _oneShotHoldTicks);

            _stats.Respawns++;

            // Guarded: this runs on the tick thread inside the world write lock, and the
            // logging extension builds its params array before the level check (#249).
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Respawned {Id} at the spawn point with {Hp} HP",
                    writer.IdRefOf(in player).Value, health.Hp);
            }
        }
    }
}
