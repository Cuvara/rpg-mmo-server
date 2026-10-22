using System.Globalization;
using GameServer.Input;
using GameServer.Observability;
using GameServer.Scaffolding;
using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Scaffolding;

/// <summary>
/// The test lab's deterministic death (<c>POST /debug/kill</c>): a queued kill reaches HP 0
/// through the normal damage and death path on the tick, and the ordinary respawn rule brings
/// the player back within one world interval.
/// </summary>
/// <remarks>
/// The fixture reproduces <c>TickLoop.TickOnce</c>'s order — critical input scope, then the
/// simulation phase — at the DEFAULT multi-rate schedule, so "one world interval" is
/// <see cref="SimulationRates.WorldEvery"/> base ticks rather than one call.
/// </remarks>
public class DebugKillPhaseTests
{
    private static readonly SimulationRates Rates = SimulationRates.Default;

    /// <summary>Away from the origin, so a respawn (to the spawn point) is visible as a move.</summary>
    private static readonly Vec2 Away = new(300f, 300f);

    private static EnemyAiSettings Settings(bool respawn)
    {
        var lookup = new Dictionary<string, string?>
        {
            // No enemies of their own: the only death in these worlds is the debug kill.
            [EnemyAiSettings.EnvMaxEnemies] = "0",
            [EnemyAiSettings.EnvMaxEnemiesPerPlayer] = "0",
            [EnemyAiSettings.EnvWaveSize] = "0",
            [EnemyAiSettings.EnvWaveSizePerPlayer] = "0",
            [EnemyAiSettings.EnvChase] = "off",
            [EnemyAiSettings.EnvAttacksEnabled] = "off",
            [EnemyAiSettings.EnvRespawnPlayers] = respawn ? "on" : "off",
        };
        Assert.True(
            EnemyAiSettings.TryCreate(n => lookup.GetValueOrDefault(n), MapBounds.Default,
                out EnemyAiSettings? s, out string? err),
            err);
        return s!;
    }

    private sealed class Rig : IDisposable
    {
        public readonly EcsWorld World = new();
        public readonly TickEventBuffer Events = new();
        public readonly InputHandler Handler;
        public readonly EnemySpawner Enemies;
        public readonly DebugKillQueue Kills = new();
        public readonly List<(EntityState Victim, EntityState Killer)> Deaths = new();
        public readonly List<GameEventData> EventLog = new();
        private readonly CompositeSimulationPhase _phase;
        private readonly List<PendingInput> _inputs = new();
        public ulong Tick;

        /// <summary>Whether the phase can see the resolver yet (Program.cs binds it after construction).</summary>
        public volatile bool CombatBound;

        public Rig(bool respawn, bool bindCombat = true)
        {
            Handler = new InputHandler(
                World, NullLogger.Instance, (v, k) => Deaths.Add((v, k)), Rates.MovementHz, MapBounds.Default,
                events: Events, oneShotHoldTicks: Rates.WorldEvery);
            Enemies = new EnemySpawner(World, Rates, NullLogger.Instance, null, Settings(respawn));
            CombatBound = bindCombat;
            // The composition Program.cs builds: the debug phase after the enemy phase.
            _phase = new CompositeSimulationPhase(
                Enemies, new DebugKillPhase(World, Kills, () => CombatBound ? Handler.Combat : null, NullLogger.Instance));
        }

        public void AddPlayer(string id, int hp = 80)
        {
            EntityState p = TestHelpers.CreatePlayer(id, Away.X, Away.Y);
            p.Hp = hp;
            p.MaxHp = 100;
            World.AddEntity(p);
        }

        public void Step()
        {
            Tick++;
            ulong tick = Tick;
            _inputs.Clear();
            World.DrainInputs(_inputs);
            World.UpdateComponents(w =>
            {
                foreach (PendingInput p in _inputs) Handler.ProcessInput(w, p, tick);
                Handler.ApplyHeldMovement(w, tick);
                Handler.StepGameplay(w, tick);
            });
            _phase.Tick(tick);
            foreach (PendingGameEvent e in Events.Events) EventLog.Add(e.Data);
            Events.Clear();
        }

        public EntityState Entity(string id) => World.GetEntity(id)!.Value;

        public void Dispose() => World.Dispose();
    }

    /// <summary>
    /// The kill is applied by the tick, not by the submitting thread: the world is untouched
    /// until the next step, and the result names the tick it landed on.
    /// </summary>
    [Fact]
    public async Task AQueuedKill_ChangesNothingUntilTheTickRuns()
    {
        using var rig = new Rig(respawn: true);
        rig.AddPlayer("p1");
        rig.Step();

        // Submitted from a pool thread, as the HTTP listener does.
        Task<DebugKillResult>? submitted = null;
        await Task.Run(() => { submitted = rig.Kills.Submit("p1"); });
        Task<DebugKillResult> pending = submitted!;
        Assert.False(pending.IsCompleted);
        Assert.Equal(80, rig.Entity("p1").Hp);
        Assert.False(rig.Entity("p1").Dead);

        rig.Step();
        DebugKillResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DebugKillOutcome.Killed, result.Outcome);
        Assert.Equal(rig.Tick, result.Tick);
        Assert.Equal(80, result.HpBefore);
    }

    /// <summary>
    /// The death goes down the normal path — Damage for exactly the HP held, the Death event,
    /// the death callback, the Dead flag and action, no despawn — and the respawn rule, not
    /// this hook, revives the player within one world interval. The control arm with respawn
    /// off stays dead, which is what makes "revived" attributable to the respawn rule.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AKilledPlayerDiesThroughTheNormalPath_AndTheRespawnRuleBringsThemBack(bool respawn)
    {
        using var rig = new Rig(respawn);
        rig.AddPlayer("p1", hp: 80);
        rig.Step();

        Task<DebugKillResult> pending = rig.Kills.Submit("p1");
        rig.Step();
        ulong killTick = rig.Tick;
        Assert.Equal(DebugKillOutcome.Killed, (await pending).Outcome);

        // Immediately after the kill tick: dead, in the world, at 0 HP, in the Dead action.
        EntityState dead = rig.Entity("p1");
        Assert.True(dead.Dead);
        Assert.Equal(0, dead.Hp);
        Assert.Equal(EntityAction.Dead, dead.Action);
        Assert.Contains(rig.EventLog, e => e.Type == GameEventType.Damage && e.TargetId == "p1" && e.Amount == 80);
        Assert.Contains(rig.EventLog, e => e.Type == GameEventType.Death && e.TargetId == "p1");
        Assert.Contains(rig.Deaths, d => d.Victim.Id == "p1");

        ulong revivedAt = 0;
        for (int i = 0; i < Rates.WorldEvery * 3; i++)
        {
            rig.Step();
            if (revivedAt == 0 && !rig.Entity("p1").Dead) revivedAt = rig.Tick;
        }

        if (respawn)
        {
            Assert.True(revivedAt > killTick, "the player was never respawned");
            Assert.True(revivedAt - killTick <= (ulong)Rates.WorldEvery,
                $"respawned {revivedAt - killTick} base ticks after the kill; the rule allows one world interval ({Rates.WorldEvery})");
            EntityState back = rig.Entity("p1");
            Assert.Equal(back.MaxHp, back.Hp);
            Assert.NotEqual(EntityAction.Dead, back.Action);
            Assert.NotEqual(Away, back.Position);
            Assert.Equal(1, rig.Enemies.Attacks.Respawns);
        }
        else
        {
            Assert.Equal(0UL, revivedAt);
            Assert.True(rig.Entity("p1").Dead);
            Assert.Equal(0, rig.Enemies.Attacks.Respawns);
        }
    }

    /// <summary>Refusals change nothing and say why.</summary>
    [Fact]
    public async Task Refusals_AreNamed_AndChangeNothing()
    {
        using var rig = new Rig(respawn: false);
        rig.AddPlayer("p1");
        rig.World.AddEntity(TestHelpers.CreateMob("m1", Away.X + 50f, Away.Y));
        rig.Step();

        Task<DebugKillResult> missing = rig.Kills.Submit("nobody");
        Task<DebugKillResult> mob = rig.Kills.Submit("m1");
        rig.Step();
        Assert.Equal(DebugKillOutcome.NotFound, (await missing).Outcome);
        Assert.Equal(DebugKillOutcome.NotAPlayer, (await mob).Outcome);
        Assert.False(rig.Entity("m1").Dead);

        Task<DebugKillResult> first = rig.Kills.Submit("p1");
        Task<DebugKillResult> second = rig.Kills.Submit("p1");
        rig.Step();
        Assert.Equal(DebugKillOutcome.Killed, (await first).Outcome);
        Assert.Equal(DebugKillOutcome.AlreadyDead, (await second).Outcome);
        Assert.Single(rig.EventLog, e => e.Type == GameEventType.Death);
    }

    /// <summary>
    /// A request the tick never reached is withdrawn on cancellation and is NOT applied later:
    /// a lab that got a 504 must not find the player dead a moment afterwards.
    /// </summary>
    [Fact]
    public async Task AWithdrawnRequest_IsNeverApplied()
    {
        using var rig = new Rig(respawn: false, bindCombat: false);
        rig.AddPlayer("p1");

        using var cts = new CancellationTokenSource();
        Task<DebugKillResult> pending = rig.Kills.Submit("p1", cts.Token);
        rig.Step(); // resolver not bound yet: the request waits
        Assert.False(pending.IsCompleted);

        cts.Cancel();
        Assert.Equal(DebugKillOutcome.Timeout, (await pending).Outcome);

        // Bind the resolver: the withdrawn request must not be applied by the next tick...
        rig.CombatBound = true;
        rig.Step();
        Assert.False(rig.Entity("p1").Dead);

        // ...while the same request submitted now is — so the step above could have killed.
        Task<DebugKillResult> control = rig.Kills.Submit("p1");
        rig.Step();
        Assert.Equal(DebugKillOutcome.Killed, (await control).Outcome);
        Assert.True(rig.Entity("p1").Dead);
    }

    /// <summary>The queue is bounded; past it a request is answered busy without queueing.</summary>
    [Fact]
    public async Task TheQueueIsBounded()
    {
        using var rig = new Rig(respawn: false, bindCombat: false);
        var tasks = new List<Task<DebugKillResult>>();
        for (int i = 0; i < DebugKillQueue.MaxPending; i++)
        {
            tasks.Add(rig.Kills.Submit("p" + i.ToString(CultureInfo.InvariantCulture)));
        }

        Assert.All(tasks, t => Assert.False(t.IsCompleted));
        Assert.Equal(DebugKillOutcome.Busy, (await rig.Kills.Submit("one-more")).Outcome);
    }
}
