using System.Globalization;
using GameServer.Input;
using GameServer.Scaffolding;
using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Scaffolding;

/// <summary>
/// Enemy-side combat: enemies that damage players, the cap that decides whether 327 of
/// them are a fight or an instant delete, and what happens when a player's HP reaches 0.
///
/// <para><b>Why the fixture drives input as well as the AI phase.</b> The attack systems
/// implement no combat. They decide, and the decision becomes ordinary queued input that
/// <c>InputHandler</c> resolves on the next tick — so a test that only ticks the phase
/// asserts nothing about damage, because no damage has happened yet. <see cref="Fight"/>
/// therefore reproduces the tick loop's own order: drain and process the inputs decided
/// last tick, then run the phase. Anything less would be a test of the decision rather
/// than of the fight.</para>
///
/// <para><b>Why the rates are uniform 15Hz.</b> One tick of the phase is then one world
/// tick and one base tick, so a "window" in the arithmetic below is a countable number of
/// calls to <see cref="Fight.Tick"/> rather than something the reader has to derive from
/// two rates. The multi-rate case is not ignored — <see cref="TheWindowIsCountedInWorldTicks_NotBaseTicks"/>
/// asserts it specifically.</para>
/// </summary>
public class EnemyAttackTests
{
    private const int TickRate = 15;

    /// <summary>Window length in world ticks at <see cref="TickRate"/> and the default 0.5s interval.</summary>
    private const int WindowTicks = 8;

    /// <summary>
    /// Where these fights happen, and it is deliberately NOT the origin.
    ///
    /// <para>With chasing off — which is how every fight here is set up, so that nothing
    /// moves and the distances a test writes are the distances it measures against —
    /// <see cref="EnemyReapSystem"/> despawns any enemy within
    /// <c>EnemyAiTuning.DespawnRadius</c> of (0,0), because a targetless enemy arriving at
    /// the centre is an enemy that has reached the end of its life. A fight staged at the
    /// origin therefore deletes its own enemies on the first tick, and every assertion
    /// below would pass or fail for that reason instead of the one it names.</para>
    ///
    /// <para>It is also far enough out that a player respawning at the spawn point (the
    /// origin) lands well outside <c>GameConstants.AttackRange</c> of the crowd, so a test
    /// about one death is not quietly a test about a kill loop.</para>
    /// </summary>
    private static readonly Vec2 Centre = new(300f, 300f);

    /// <summary>
    /// Settings for a fight whose shape the test controls: no spawning of its own, so the
    /// only enemies in the world are the ones the test placed, and no chasing, so nothing
    /// moves and every distance in the test stays the distance the test wrote.
    /// </summary>
    private static EnemyAiSettings Settings(
        bool attacks = true, int attackersPerTarget = 3, float interval = 0.5f,
        bool respawn = true, bool chase = false, int wave = 0, int max = 0)
    {
        var lookup = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvMaxEnemies] = max.ToString(CultureInfo.InvariantCulture),
            [EnemyAiSettings.EnvMaxEnemiesPerPlayer] = "0",
            [EnemyAiSettings.EnvWaveSize] = wave.ToString(CultureInfo.InvariantCulture),
            [EnemyAiSettings.EnvWaveSizePerPlayer] = "0",
            [EnemyAiSettings.EnvChase] = chase ? "on" : "off",
            // Effectively motionless. With chasing off the move system still walks every
            // enemy toward the origin, and a test that places an enemy at a measured
            // distance and then lets it drift is measuring the drift.
            [EnemyAiSettings.EnvSpeed] = "0.01",
            [EnemyAiSettings.EnvAttacksEnabled] = attacks ? "on" : "off",
            [EnemyAiSettings.EnvAttackersPerTarget] = attackersPerTarget.ToString(CultureInfo.InvariantCulture),
            [EnemyAiSettings.EnvAttackIntervalSec] = interval.ToString(CultureInfo.InvariantCulture),
            [EnemyAiSettings.EnvRespawnPlayers] = respawn ? "on" : "off",
        };

        Assert.True(
            EnemyAiSettings.TryCreate(n => lookup.GetValueOrDefault(n), MapBounds.Default,
                out EnemyAiSettings? s, out string? err),
            err);
        return s!;
    }

    /// <summary>
    /// A world, the enemy phase and the real input handler, ticked in the order the tick
    /// loop ticks them.
    /// </summary>
    private sealed class Fight : IDisposable
    {
        public readonly EcsWorld World = new();
        public readonly TickEventBuffer Events = new();
        public readonly EnemySpawner Ai;
        public readonly InputHandler Handler;

        /// <summary>Every event the simulation produced, accumulated across ticks.</summary>
        public readonly List<GameEventData> EventLog = new();

        private readonly List<PendingInput> _inputs = new();

        public Fight(EnemyAiSettings settings, int tickRate = TickRate)
            : this(settings, SimulationRates.Uniform(tickRate), tickRate)
        {
        }

        public Fight(EnemyAiSettings settings, SimulationRates rates, int cooldownRate)
        {
            Ai = new EnemySpawner(World, rates, NullLogger.Instance, null, settings);

            // The same handler the server builds, with the event buffer wired: the damage
            // and death events this feature has to deliver are produced there and nowhere
            // else, so a fixture without the buffer could not tell a silent hit from a
            // reported one.
            Handler = new InputHandler(
                World, NullLogger.Instance, null, cooldownRate, MapBounds.Default,
                events: Events);
        }

        /// <summary>
        /// One tick, in the tick loop's order: inputs decided last tick are drained and
        /// processed first, then the simulation phase runs and decides for the next one.
        /// </summary>
        public void Tick(ulong tick)
        {
            _inputs.Clear();
            World.DrainInputs(_inputs);
            if (_inputs.Count > 0)
            {
                World.UpdateComponents(writer =>
                {
                    foreach (PendingInput p in _inputs)
                    {
                        Handler.ProcessInput(writer, p, tick);
                    }
                });
            }

            Ai.Tick(tick);

            // Drained here rather than at the end of the run because the real buffer is
            // cleared once per broadcast; accumulating keeps the test's view of "what the
            // simulation reported" complete without changing the buffer's lifetime.
            foreach (PendingGameEvent e in Events.Events) EventLog.Add(e.Data);
            Events.Clear();
        }

        public void Run(int ticks, ulong from = 1)
        {
            for (ulong t = from; t < from + (ulong)ticks; t++) Tick(t);
        }

        public EntityState Entity(string id) => World.GetEntity(id)!.Value;

        public int Hp(string id) => Entity(id).Hp;

        public void Dispose() => World.Dispose();
    }

    /// <summary>
    /// Place <paramref name="count"/> enemies on a ring of <paramref name="radius"/> about
    /// <paramref name="about"/>, carrying the tuning's own attack and defense so the
    /// damage arithmetic in these tests is the shipped arithmetic.
    /// </summary>
    private static void Surround(
        Fight fight, int count, float radius = 1.0f, Vec2? about = null, string prefix = "mob")
    {
        Vec2 centre = about ?? Centre;
        for (int i = 0; i < count; i++)
        {
            float angle = i * 2.39996323f;
            EntityState mob = TestHelpers.CreateMob(
                $"{prefix}{i:D4}",
                centre.X + (MathF.Cos(angle) * radius),
                centre.Y + (MathF.Sin(angle) * radius));
            mob.Attack = EnemyAiTuning.EnemyAttack;
            mob.Defense = EnemyAiTuning.EnemyDefense;
            fight.World.Spawn(mob, EntityTags.EnemyAi);
        }
    }

    /// <summary>A player at <see cref="Centre"/> unless told otherwise.</summary>
    private static void AddPlayer(Fight fight, string id, int hp = 100, int maxHp = 100, Vec2? at = null)
    {
        Vec2 p = at ?? Centre;
        EntityState player = TestHelpers.CreatePlayer(id, p.X, p.Y);
        player.Hp = hp;
        player.MaxHp = maxHp;
        fight.World.AddEntity(player);
    }

    // ── Enemies damage players ───────────────────────────────────────────────

    /// <summary>
    /// The feature, asserted as a CHANGE against a control arm rather than as a state.
    ///
    /// <para>"HP is below maximum" would be satisfiable by anything that ever touched the
    /// player; the pair below is not. The two arms differ in exactly one environment
    /// variable, run for exactly the same number of ticks over exactly the same world, and
    /// the control arm has to end at full HP — so a change that broke the switch fails
    /// here whichever way it broke it.</para>
    /// </summary>
    [Fact]
    public void EnemiesDamageAPlayerInRange_AndDoNotWhenAttacksAreOff()
    {
        int Survive(bool attacks)
        {
            using var fight = new Fight(Settings(attacks: attacks));
            AddPlayer(fight, "p1");
            Surround(fight, 6);
            fight.Run(WindowTicks * 4 + 2);
            return fight.Hp("p1");
        }

        int off = Survive(attacks: false);
        int on = Survive(attacks: true);

        Assert.Equal(100, off);
        Assert.True(on < off, $"enemies dealt no damage: {on} HP with attacks on, {off} with them off");
    }

    /// <summary>
    /// Nothing out of <c>GameConstants.AttackRange</c> lands a hit, even standing still
    /// beside a player for a long time. The control arm is the same world at contact
    /// range, so this cannot pass by the feature being inert.
    /// </summary>
    [Fact]
    public void EnemiesOutsideAttackRange_DoNotAttack()
    {
        int Survive(float radius)
        {
            using var fight = new Fight(Settings());
            AddPlayer(fight, "p1");
            Surround(fight, 6, radius);
            fight.Run(WindowTicks * 4 + 2);
            return fight.Hp("p1");
        }

        // 3.0 is the range; 3.5 is outside it, 1.0 is the contact distance chasers stop at.
        Assert.Equal(100, Survive(3.5f));
        Assert.True(Survive(1.0f) < 100, "the in-range control took no damage either");
    }

    // ── The survivability cap ────────────────────────────────────────────────

    /// <summary>
    /// <b>The claim this whole feature is answerable for.</b> A player surrounded by 60
    /// enemies loses exactly as much HP as a player surrounded by 3, because the limit is
    /// expressed on the target rather than on the attacker.
    ///
    /// <para>Asserted as equality between two arms rather than as "less than some number":
    /// an inequality against a constant passes for a build where enemies stopped attacking
    /// altogether, and the third assertion below is what rules that out.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void IncomingDamageIsCappedPerTarget_WhateverTheCrowdSize(int cap)
    {
        int Damage(int crowd)
        {
            using var fight = new Fight(Settings(attackersPerTarget: cap));
            AddPlayer(fight, "p1");
            Surround(fight, crowd);
            fight.Run(WindowTicks * 5 + 2);
            return 100 - fight.Hp("p1");
        }

        int small = Damage(cap);
        int large = Damage(60);

        Assert.True(small > 0, "the small arm took no damage, so the comparison proves nothing");
        Assert.Equal(small, large);
    }

    /// <summary>
    /// The cap is per WINDOW, not per tick — which is the distinction a per-tick cap gets
    /// wrong by a factor of the world rate, and gets wrong in the direction that kills the
    /// player.
    ///
    /// <para>Five windows at three hits of one damage each is 15, and the run is sized to
    /// exactly five windows. A per-tick cap would deliver three hits on every tick an
    /// enemy was off cooldown and land far more.</para>
    /// </summary>
    [Fact]
    public void TheCapIsPerWindow_NotPerTick()
    {
        using var fight = new Fight(Settings(attackersPerTarget: 3));
        AddPlayer(fight, "p1");
        Surround(fight, 40);

        const int windows = 5;
        fight.Run((WindowTicks * windows) + 2);

        int damage = 100 - fight.Hp("p1");

        // Damage per hit is max(1, enemy attack 5 - player defense 5) = 1, so at these
        // stats the hit count and the damage are the same number.
        //
        // The upper bound allows one window of slack because the run can straddle a
        // boundary, and it is still enormously discriminating: with a per-TICK cap the
        // same 40-enemy crowd would land three hits on every tick an enemy was off
        // cooldown — of the order of 120 damage over this run, killing the player outright
        // — so the difference between the two rules is 18 against dead, not 18 against 15.
        Assert.InRange(damage, 1, 3 * (windows + 1));
    }

    /// <summary>
    /// Every window that opens spends its whole budget, so the configured rate is the rate
    /// that is delivered.
    ///
    /// <para><b>The assertion the other cap tests cannot make.</b> They are upper bounds
    /// and comparisons between arms, and a build that attacked at half the configured rate
    /// satisfies every one of them — both arms halve together, and half of a bound is
    /// still under it. The specific way that happens is real: the decision is validated on
    /// the world tick and the cooldown is charged one base tick later, so validating
    /// against the decision tick instead of the apply tick refuses every second window and
    /// halves the damage with nothing failing anywhere. This is the lower bound that sees
    /// it.</para>
    /// </summary>
    [Fact]
    public void EveryWindowThatOpensSpendsItsWholeBudget()
    {
        const int cap = 3;
        const int windows = 6;

        using var fight = new Fight(Settings(attackersPerTarget: cap));
        AddPlayer(fight, "p1");
        Surround(fight, 20);

        // Windows open on ticks 1, 9, 17, … (see EnemyAttackSystem for the phase). Running
        // to the tick after the sixth opens means six decisions and six resolutions.
        fight.Run((WindowTicks * (windows - 1)) + 2);

        Assert.Equal(cap * windows, (long)fight.Ai.Attacks.Decided);
        Assert.Equal(cap * windows, 100 - fight.Hp("p1"));
    }

    /// <summary>
    /// A cap of zero is a configuration that means "enemies never land a hit", and it has
    /// to mean that rather than "no cap". Off-by-one in the budget check reads as an
    /// unbounded fight, which is the failure mode with the worst consequence.
    /// </summary>
    [Fact]
    public void ACapOfZero_LandsNoHits()
    {
        using var fight = new Fight(Settings(attackersPerTarget: 0));
        AddPlayer(fight, "p1");
        Surround(fight, 40);
        fight.Run(WindowTicks * 4 + 2);

        Assert.Equal(100, fight.Hp("p1"));
    }

    /// <summary>
    /// Each player gets their OWN budget. A shared budget would make a crowd safer the
    /// more of them there are, which is the cap working backwards.
    /// </summary>
    [Fact]
    public void EachTargetHasItsOwnBudget()
    {
        using var fight = new Fight(Settings(attackersPerTarget: 2));
        var second = new Vec2(Centre.X + 50f, Centre.Y);
        AddPlayer(fight, "p1");
        AddPlayer(fight, "p2", at: second);

        Surround(fight, 10);
        Surround(fight, 10, about: second, prefix: "far");

        fight.Run(WindowTicks * 4 + 2);

        Assert.True(fight.Hp("p1") < 100, "p1 took nothing");
        Assert.Equal(fight.Hp("p1"), fight.Hp("p2"));
    }

    /// <summary>
    /// The throttle counter is what tells an operator the cap is the thing limiting the
    /// fight, so it has to move when the cap bites and stay still when it does not.
    /// </summary>
    [Fact]
    public void TheThrottleCounterDistinguishesACappedFightFromAQuietOne()
    {
        using var crowded = new Fight(Settings(attackersPerTarget: 2));
        AddPlayer(crowded, "p1");
        Surround(crowded, 30);
        crowded.Run(WindowTicks * 3 + 2);

        using var sparse = new Fight(Settings(attackersPerTarget: 2));
        AddPlayer(sparse, "p1");
        Surround(sparse, 2);
        sparse.Run(WindowTicks * 3 + 2);

        Assert.True(crowded.Ai.Attacks.Throttled > 0, "a 30-enemy crowd throttled nothing");
        Assert.Equal(0, sparse.Ai.Attacks.Throttled);
        Assert.True(sparse.Ai.Attacks.Decided > 0, "the sparse arm decided nothing, so it proves nothing");
    }

    /// <summary>
    /// The window is counted in WORLD ticks, so the same configuration delivers the same
    /// damage per second whatever the base rate is. Counting it in base ticks would make a
    /// 60Hz server four times deadlier than a 15Hz one on identical settings — the class of
    /// bug the multi-rate time model exists to prevent.
    /// </summary>
    [Fact]
    public void TheWindowIsCountedInWorldTicks_NotBaseTicks()
    {
        // Same world rate (15Hz), different base rates, run for the same number of WORLD
        // ticks — which is the same wall-clock span in both arms.
        //
        // The comparison is on DECISIONS rather than on HP, and deliberately: a decision
        // is flushed as input and resolved on the NEXT base tick, so the last window of a
        // run lands its damage one base tick after the run ends in one arm and four in the
        // other. That trailing tick is an artefact of where the test stops, not of the
        // rule under test, and comparing HP would make this assertion a statement about
        // the harness. Damage is still asserted non-zero in both arms, so "equal because
        // neither attacked" cannot pass.
        (long Decided, int Damage) Measure(SimulationRates rates)
        {
            using var fight = new Fight(Settings(), rates, rates.MovementHz);
            AddPlayer(fight, "p1");
            Surround(fight, 20);
            fight.Run(WindowTicks * 4 * rates.WorldEvery);
            return (fight.Ai.Attacks.Decided, 100 - fight.Hp("p1"));
        }

        var uniform = Measure(SimulationRates.Uniform(15));
        var multi = Measure(SimulationRates.Default);   // 60/15/5

        Assert.True(uniform.Damage > 0, "the uniform arm took no damage");
        Assert.True(multi.Damage > 0, "the multi-rate arm took no damage");
        Assert.Equal(uniform.Decided, multi.Decided);
    }

    /// <summary>
    /// The enemy combat phase allocates nothing per tick in steady state.
    ///
    /// <para>Measured rather than asserted from the shape of the code, because the two
    /// ways this feature could allocate are both invisible in a review: the decision
    /// buffer and the budget array grow on demand, and a growth rule that re-allocated
    /// every tick instead of once would read identically. Warmed up first so buffer growth
    /// and JIT promotion are not counted as steady state — the distinction the tick
    /// allocation bench exists to make.</para>
    ///
    /// <para>The measurement covers the phase only, not the input handler that resolves
    /// its decisions: <c>PushInput</c> and <c>DrainInputs</c> are the existing input path,
    /// already measured by <c>TickAllocationBench</c>, and folding them in here would make
    /// this test fail for somebody else's regression.</para>
    /// </summary>
    [Fact]
    public void TheCombatPhaseAllocatesNothingPerTickInSteadyState()
    {
        using var fight = new Fight(Settings(attackersPerTarget: 3));
        AddPlayer(fight, "p1");
        Surround(fight, 40);

        // Reused, because the List-returning DrainInputs overload allocates one per call
        // and that is the harness's cost, not the phase's. The tick loop reuses a list for
        // exactly this reason.
        var drained = new List<PendingInput>();

        // Warm-up: several full windows, so every buffer has reached its high-water mark.
        for (ulong t = 1; t <= 200; t++)
        {
            fight.Ai.Tick(t);
            fight.World.DrainInputs(drained);
            drained.Clear();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong t = 201; t <= 1200; t++)
        {
            fight.Ai.Tick(t);
            fight.World.DrainInputs(drained);
            drained.Clear();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        // Zero, not a budget. Every buffer the phase uses is [SimulationScratch] and has
        // stopped growing by now, so anything above zero here is a per-tick allocation and
        // the number would name it: a re-allocated handle buffer at this population is
        // ~1.5 KB per window, a per-decision string would be tens of bytes per swing.
        long allocated = after - before;
        Assert.True(allocated == 0,
            $"the enemy combat phase allocated {allocated} B over 1000 steady-state ticks");
    }

    // ── What reaches the client ──────────────────────────────────────────────

    /// <summary>
    /// The damage has to be reportable, not merely applied: a client cannot derive a hit
    /// from an HP delta (the target may leave the AOI, or be healed in the same tick), and
    /// the damage number is what floats off a head.
    /// </summary>
    [Fact]
    public void EnemyDamageIsEmittedAsAGameEvent()
    {
        using var fight = new Fight(Settings());
        AddPlayer(fight, "p1");
        Surround(fight, 4);
        fight.Run(WindowTicks * 3 + 2);

        List<GameEventData> damage = fight.EventLog
            .FindAll(e => e.Type == GameEventType.Damage && e.TargetId == "p1");

        Assert.NotEmpty(damage);
        Assert.All(damage, e =>
        {
            Assert.StartsWith("mob", e.SourceId);
            Assert.True(e.Amount > 0, "a damage event carried no amount");
        });
    }

    /// <summary>
    /// A player killed by enemies produces a Death event naming the enemy that killed
    /// them, and it is produced BEFORE the respawn — an unobservable death is the same
    /// thing as no death, and the respawn rule must not erase the moment it is reacting
    /// to.
    /// </summary>
    [Fact]
    public void AKilledPlayerProducesADeathEvent()
    {
        using var fight = new Fight(Settings());
        AddPlayer(fight, "p1", hp: 2);
        Surround(fight, 4);

        fight.Run(WindowTicks * 4 + 2);

        GameEventData death = Assert.Single(
            fight.EventLog.FindAll(e => e.Type == GameEventType.Death && e.TargetId == "p1"));
        Assert.StartsWith("mob", death.SourceId);
    }

    // ── Death and respawn ────────────────────────────────────────────────────

    /// <summary>
    /// <b>What happens when a player's HP reaches 0.</b> With the rule on, the player is
    /// back on the map at full HP; with it off, the pre-existing behaviour is unchanged —
    /// dead forever, every input refused, and <c>hp = 0</c> on its way to the database.
    ///
    /// <para>Both arms are asserted, because the value of the rule is precisely the
    /// difference between them and an assertion on one arm alone cannot see it.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APlayerAtZeroHpIsRespawned_OrStaysDeadWhenTheRuleIsOff(bool respawn)
    {
        using var fight = new Fight(Settings(respawn: respawn));

        // Staged away from the origin, so the respawn moving the player to the spawn point
        // is visible as a change of position rather than as a no-op.
        AddPlayer(fight, "p1", hp: 2);
        Surround(fight, 4);

        fight.Run(WindowTicks * 4 + 2);

        EntityState after = fight.Entity("p1");

        // The death happened in both arms — otherwise neither arm says anything about
        // what follows it.
        Assert.Contains(fight.EventLog, e => e.Type == GameEventType.Death && e.TargetId == "p1");

        if (respawn)
        {
            Assert.False(after.Dead);
            Assert.Equal(after.MaxHp, after.Hp);
            Assert.Equal(0f, after.Position.X);
            Assert.Equal(0f, after.Position.Y);
            Assert.True(fight.Ai.Attacks.Respawns > 0, "nothing was counted as a respawn");
        }
        else
        {
            Assert.True(after.Dead);
            Assert.Equal(0, after.Hp);
            Assert.Equal(0, fight.Ai.Attacks.Respawns);
        }
    }

    /// <summary>
    /// Respawn restores the entity's OWN maximum, not a server constant. A character whose
    /// maximum is not 100 must not be silently re-statted by dying.
    /// </summary>
    [Fact]
    public void RespawnRestoresTheEntitysOwnMaximum()
    {
        using var fight = new Fight(Settings());
        AddPlayer(fight, "p1", hp: 2, maxHp: 250);
        Surround(fight, 4);

        fight.Run(WindowTicks * 4 + 2);

        Assert.Equal(250, fight.Hp("p1"));
    }

    /// <summary>
    /// A respawned player is out of the <c>Dead</c> action, which is terminal for the
    /// action field. Without this the revived entity renders as a corpse that walks, and
    /// nothing about its HP would say so.
    /// </summary>
    [Fact]
    public void ARespawnedPlayerIsNoLongerReportedAsDead()
    {
        using var fight = new Fight(Settings());
        AddPlayer(fight, "p1", hp: 2);
        Surround(fight, 4);

        fight.Run(WindowTicks * 4 + 2);

        Assert.NotEqual(EntityAction.Dead, fight.Entity("p1").Action);
    }

    /// <summary>
    /// A respawned player accepts input again. This is the assertion that makes the
    /// respawn worth having: the entity being alive in the world is not the same as the
    /// player being able to play, because <c>InputHandler</c> refuses a dead entity's
    /// input before it looks at anything else.
    /// </summary>
    [Fact]
    public void ARespawnedPlayerCanActAgain()
    {
        using var fight = new Fight(Settings());
        AddPlayer(fight, "p1", hp: 2);
        Surround(fight, 4);

        fight.Run(WindowTicks * 4 + 2);
        Assert.True(fight.Ai.Attacks.Respawns > 0, "the player never died, so this proves nothing");

        ulong tick = (ulong)(WindowTicks * 4 + 3);
        fight.World.PushInput("p1", new InputData(tick, 1f, 0f, null), ingress: null);
        fight.Tick(tick + 1);

        Assert.True(fight.Entity("p1").Position.X > 0f,
            "a respawned player could not move, so the entity is alive and the player is not");
    }

    // ── A player held for reconnect is out of reach ──────────────────────────

    private static void SetLinkdead(Fight fight, string id, bool value) =>
        fight.World.UpdateComponents(writer =>
        {
            EntityHandle handle = writer.Resolve(id);
            Assert.True(handle.IsValid, $"{id} is not in the world");
            writer.PlayerTagOf(in handle).Linkdead = value;
        });

    /// <summary>
    /// Three arms over the same fight: an ordinary player is hit, a held one is not, and the
    /// held one is hit again the moment the hold is lifted. The third arm is what makes the
    /// second mean anything -- without it, "took no damage" would also be satisfied by the
    /// flag doing nothing and the fight happening not to land a blow.
    /// </summary>
    [Fact]
    public void APlayerHeldForReconnect_IsNotAttacked_AndIsAgainOnceItIsBack()
    {
        int ticks = WindowTicks * 4 + 2;

        using (var control = new Fight(Settings()))
        {
            AddPlayer(control, "p1");
            Surround(control, 6);
            control.Run(ticks);
            Assert.True(control.Hp("p1") < 100, "control arm: the fight landed no hit, so this test proves nothing");
        }

        using var fight = new Fight(Settings());
        AddPlayer(fight, "p1");
        Surround(fight, 6);
        SetLinkdead(fight, "p1", true);
        fight.Run(ticks);
        Assert.Equal(100, fight.Hp("p1"));

        SetLinkdead(fight, "p1", false);
        fight.Run(ticks, from: (ulong)ticks + 1);
        Assert.True(fight.Hp("p1") < 100, "released from the hold, the player should be in reach again");
    }

    /// <summary>
    /// The incident itself. On the dev cluster the smoke test walked to x=4.83, dropped its
    /// connection, and its saved row came back x=0 y=0 hp=79/100: killed during the 30s
    /// reconnect grace, revived at the spawn point at full health, hit again, and persisted
    /// there by the eviction save. A player one blow from death, surrounded, with respawn on,
    /// reproduces it in the control arm -- and in the held arm stays exactly where and as
    /// they were.
    /// </summary>
    [Fact]
    public void AHeldPlayer_IsNotKilledAndMovedToTheSpawnPoint()
    {
        int ticks = WindowTicks * 4 + 2;

        using (var control = new Fight(Settings(respawn: true)))
        {
            AddPlayer(control, "p1", hp: 1);
            Surround(control, 6);
            control.Run(ticks);
            Vec2 at = control.Entity("p1").Position;
            Assert.True(at.X != Centre.X || at.Y != Centre.Y,
                "control arm: the player was neither killed nor respawned, so this test proves nothing");
        }

        using var fight = new Fight(Settings(respawn: true));
        AddPlayer(fight, "p1", hp: 1);
        Surround(fight, 6);
        SetLinkdead(fight, "p1", true);
        fight.Run(ticks);

        EntityState held = fight.Entity("p1");
        Assert.Equal(1, held.Hp);
        Assert.Equal(Centre.X, held.Position.X);
        Assert.Equal(Centre.Y, held.Position.Y);
    }
}
