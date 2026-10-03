using GameServer.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// ADR-30 statuses running in the simulation: periodic damage and healing with their
/// events, expiry, crowd control gating movement, casting and attacking, slows scaling
/// speed, stat modifiers feeding combat, and death clearing everything.
/// </summary>
public class StatusSimulationTests
{
    [Fact]
    public void ADot_TicksOnItsInterval_ThenExpires_WithPeriodicEventsAndARemoval()
    {
        using var f = new SimFixture();
        f.AddMob("m1", 0f, 0f, hp: 100);
        f.AddPlayer("p1", 1f, 0f);
        f.Step(); // tick 1
        f.ApplyStatus("m1", SimFixture.Burning, source: "p1"); // applied at tick 1: due 3,5,7,9,11; expires 11

        f.StepUntil(10);
        Assert.Equal(100 - 4 * 3, f.State("m1").Hp);
        Assert.True(f.Record("m1").Statuses!.Contains(SimFixture.Burning));

        f.StepUntil(12);
        Assert.Equal(100 - 5 * 3, f.State("m1").Hp);
        Assert.False(f.Record("m1").Statuses!.Contains(SimFixture.Burning));

        var ticks = f.EventsOf(GameEventType.Damage).ToList();
        Assert.Equal(5, ticks.Count);
        Assert.All(ticks, e =>
        {
            Assert.Equal(GameEventFlags.Periodic, e.Flags);
            Assert.Equal(SimFixture.Burning, e.EffectId);
            Assert.Equal(3, e.Amount);
            Assert.Equal("p1", e.SourceId);
            Assert.Equal("m1", e.TargetId);
        });
        var removed = Assert.Single(f.EventsOf(GameEventType.StatusRemoved));
        Assert.Equal(SimFixture.Burning, removed.EffectId);
        Assert.Equal("m1", removed.TargetId);
    }

    [Fact]
    public void AStatusChange_MovesTheVersionTheEncoderReads_AndCopyStatusesReportsIt()
    {
        using var f = new SimFixture();
        f.AddMob("m1", 0f, 0f);
        f.Step();
        uint before = f.Views().Single(v => v.Id == "m1").StatusesVersion;

        f.ApplyStatus("m1", SimFixture.Burning, source: "p1");
        EntityView after = f.Views().Single(v => v.Id == "m1");
        Assert.NotEqual(before, after.StatusesVersion);

        f.World.ReadAll(r =>
        {
            Span<StatusEffectData> buffer = new StatusEffectData[4];
            int n = r.CopyStatuses(after.Key, buffer);
            Assert.Equal(1, n);
            Assert.Equal(SimFixture.Burning, buffer[0].EffectId);
            Assert.Equal(1u, buffer[0].Stacks);
            Assert.Equal(f.Tick + 10, buffer[0].ExpiresTick);
            Assert.Equal("p1", buffer[0].SourceId);
        });
    }

    [Fact]
    public void AHot_HealsOnItsInterval_ClampedToMissingHealth()
    {
        using var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f, hp: 90);
        f.Input("p1", SimFixture.Cast(1, SimFixture.Mend)); // self, applies mending at tick 1
        f.Step();
        f.StepUntil(8);

        // +7 at tick 4 (90 -> 97), +3 (clamped) at tick 7.
        Assert.Equal(100, f.State("p1").Hp);
        var heals = f.EventsOf(GameEventType.Heal).ToList();
        Assert.Equal(new[] { 7, 3 }, heals.Select(h => h.Amount));
        Assert.All(heals, h => Assert.Equal(SimFixture.Mending, h.EffectId));
    }

    [Fact]
    public void Root_BlocksMovement_UntilItExpires()
    {
        using var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f);
        f.Step(); // tick 1
        f.ApplyStatus("p1", SimFixture.Rooted); // tick 1 + 5 = expires at 6

        f.Input("p1", SimFixture.Move(1, 1f, 0f));
        f.Step(); // tick 2
        Assert.Equal(0f, f.Pos("p1").X);
        Assert.NotEqual(EntityAction.Moving, f.State("p1").Action);

        f.StepUntil(6); // expires
        f.Input("p1", SimFixture.Move(2, 1f, 0f));
        f.Step();
        Assert.True(f.Pos("p1").X > 0f, "movement must resume once the root expires");
    }

    [Fact]
    public void Slow_ScalesTheDistanceTravelled()
    {
        float Travel(bool slowed)
        {
            using var f = new SimFixture();
            f.AddPlayer("p1", 0f, 0f);
            f.Step();
            if (slowed) f.ApplyStatus("p1", SimFixture.Chilled);
            f.Input("p1", SimFixture.Move(1, 1f, 0f));
            f.Step();
            return f.Pos("p1").X;
        }

        float normal = Travel(false);
        float slow = Travel(true);
        Assert.Equal(5f / 60f, normal, 5);
        Assert.Equal(normal * 0.5f, slow, 5);
    }

    [Fact]
    public void Stun_BlocksMovementCastingAndAttacking_SilenceBlocksOnlyCasting()
    {
        using var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f);
        f.AddMob("m1", 2f, 0f);
        f.Step();

        f.ApplyStatus("p1", SimFixture.Stunned);
        f.Input("p1", new InputData(1, 1f, 0f, "m1", SimFixture.Jab, "m1", default));
        f.Step();
        Assert.Equal(0f, f.Pos("p1").X);
        Assert.Equal(100, f.State("m1").Hp);
        Assert.Equal(GameServer.Input.InputHandler.CannotActRejection, f.Handler.Attacks.LastRejection);
        Assert.Equal(global::Shared.GameLogic.Systems.AbilityLogic.CannotCastRejection, f.Handler.Abilities.LastRejection);

        f.StepUntil(8); // stun over
        f.ApplyStatus("p1", SimFixture.Silenced);
        f.Input("p1", SimFixture.Cast(2, SimFixture.Jab, "m1"));
        f.Step();
        Assert.Equal(100, f.State("m1").Hp);

        // Silence does not stop a basic attack: 10 attack - 5 defense.
        f.Input("p1", new InputData(3, 0f, 0f, "m1"));
        f.Step();
        Assert.Equal(95, f.State("m1").Hp);
    }

    [Fact]
    public void AnAttackModifier_FeedsTheDamageMath()
    {
        int DamageDealt(bool empowered)
        {
            using var f = new SimFixture();
            f.AddPlayer("p1", 0f, 0f);
            f.AddMob("m1", 2f, 0f);
            f.Step();
            if (empowered) f.ApplyStatus("p1", SimFixture.Empowered);
            f.Input("p1", SimFixture.Cast(1, SimFixture.Jab, "m1"));
            f.Step();
            return 100 - f.State("m1").Hp;
        }

        Assert.Equal(6, DamageDealt(false));        // 10 + 1 - 5
        Assert.Equal(16, DamageDealt(true));        // (10 + 10) + 1 - 5
    }

    [Fact]
    public void Death_ClearsEveryStatus_WithRemovalEvents()
    {
        using var f = new SimFixture(withLoot: false);
        f.AddPlayer("p1", 0f, 0f);
        f.AddMob("m1", 2f, 0f, hp: 6);
        f.Step();
        f.ApplyStatus("m1", SimFixture.Chilled);

        f.Input("p1", SimFixture.Cast(1, SimFixture.Jab, "m1")); // 6 damage: dead
        f.Step();

        Assert.True(f.State("m1").Dead);
        Assert.Equal(0, f.Record("m1").Statuses!.Count);
        Assert.Contains(f.EventsOf(GameEventType.StatusRemoved), e => e.EffectId == SimFixture.Chilled);
        Assert.Contains(f.Deaths, d => d.Victim.Id == "m1" && d.Killer.Id == "p1");
    }

    [Fact]
    public void ADotKill_CreditsTheStatusSource()
    {
        using var f = new SimFixture(withLoot: false);
        f.AddPlayer("p1", 50f, 0f);
        f.AddMob("m1", 0f, 0f, hp: 5);
        f.Step();
        f.ApplyStatus("m1", SimFixture.Burning, source: "p1");
        f.StepUntil(6);

        Assert.True(f.State("m1").Dead);
        var death = Assert.Single(f.EventsOf(GameEventType.Death));
        Assert.Equal("p1", death.SourceId);
        Assert.Contains(f.Deaths, d => d.Victim.Id == "m1" && d.Killer.Id == "p1");
    }

    [Fact]
    public void ActorsCarryTheContentStatBlock_FromDefaults()
    {
        using var f = new SimFixture();
        f.AddPlayer("p1", 0f, 0f);
        EntityView v = f.Views().Single(x => x.Id == "p1");

        f.World.ReadAll(r =>
        {
            Assert.Equal(2, r.StatCount(v.Key));
            Span<StatValueData> stats = new StatValueData[2];
            Assert.Equal(2, r.CopyStats(v.Key, stats));
            Assert.Equal(new StatValueData(1, 1), stats[0]);   // level
            Assert.Equal(new StatValueData(2, 100), stats[1]); // mana
        });
    }
}
