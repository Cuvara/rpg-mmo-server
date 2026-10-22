using GameServer.Gameplay;
using GameServer.Input;
using GameServer.Observability;
using Shared.GameLogic.Components;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// Every combat rejection the server already enforces reaches
/// <c>gameserver_inputs_rejected_total</c> under a reason of its own. The four asserted
/// first used to fold into <c>attack_other</c> / <c>ability_other</c>, which is the bucket
/// documented as "the classifier is stale".
/// </summary>
public class CombatRejectionReasonTests
{
    private static (SimFixture F, List<InputRejectionReason> Reasons) Fixture()
    {
        var reasons = new List<InputRejectionReason>();
        var f = new SimFixture(onRejected: (_, r) => reasons.Add(r));
        return (f, reasons);
    }

    [Fact]
    public void AStunnedAttackerAndCaster_AreCountedAsCannotAct_AndCannotCast()
    {
        var (f, reasons) = Fixture();
        using var _ = f;
        f.AddPlayer("p1", 0f, 0f);
        f.AddMob("m1", 2f, 0f);
        f.Step();

        f.ApplyStatus("p1", SimFixture.Stunned);
        f.Input("p1", new InputData(1, 0f, 0f, "m1", SimFixture.Jab, "m1", default));
        f.Step();

        Assert.Equal(new[] { InputRejectionReason.AttackCannotAct, InputRejectionReason.AbilityCannotCast }, reasons);
        Assert.Equal(100, f.State("m1").Hp);
    }

    [Fact]
    public void AnAttackOnAnItem_IsCountedAsUntargetable()
    {
        var (f, reasons) = Fixture();
        using var _ = f;
        f.AddPlayer("p1", 0f, 0f);
        string itemId = "";
        f.World.UpdateComponents(w => itemId = CombatResolver.SpawnItem(w, "wolf_pelt", 1, new Vec2(1f, 0f), 1000));
        f.Step();

        f.Input("p1", new InputData(1, 0f, 0f, itemId));
        f.Step();

        Assert.Equal(new[] { InputRejectionReason.AttackTargetUntargetable }, reasons);
    }

    [Fact]
    public void AProjectileAimedAtTheCastersOwnPosition_IsCountedAsMissingAim()
    {
        var (f, reasons) = Fixture();
        using var _ = f;
        f.AddPlayer("p1", 0f, 0f);
        f.Step();

        f.Input("p1", SimFixture.Cast(1, SimFixture.Bolt, aimX: 0f, aimY: 0f));
        f.Step();

        Assert.Equal(new[] { InputRejectionReason.AbilityMissingAim }, reasons);
    }

    /// <summary>
    /// The reasons that were already classified still are: range and cooldown on both paths.
    /// Guards against the new branches shadowing an existing one.
    /// </summary>
    [Fact]
    public void RangeAndCooldown_StillClassifyOnBothPaths()
    {
        var (f, reasons) = Fixture();
        using var _ = f;
        f.AddPlayer("p1", 0f, 0f);
        f.AddMob("near", 1f, 0f);
        f.AddMob("far", 50f, 0f);
        f.Step();

        f.Input("p1", new InputData(1, 0f, 0f, "far"));
        f.Step();
        f.Input("p1", SimFixture.Cast(2, SimFixture.Jab, "far"));
        f.Step();
        f.Input("p1", new InputData(3, 0f, 0f, "near"));
        f.Input("p1", new InputData(4, 0f, 0f, "near"));
        f.Step();

        Assert.Equal(
            new[]
            {
                InputRejectionReason.AttackOutOfRange,
                InputRejectionReason.AbilityOutOfRange,
                InputRejectionReason.AttackOnCooldown,
            },
            reasons);
    }

    /// <summary>The new reasons are published (primed at zero) under stable labels.</summary>
    [Fact]
    public void TheNewReasonsArePrimedAndLabelled()
    {
        using var metrics = new GameMetrics("map_test", $"test.{Guid.NewGuid():N}");
        var expected = new Dictionary<InputRejectionReason, string>
        {
            [InputRejectionReason.AttackTargetUntargetable] = "attack_target_untargetable",
            [InputRejectionReason.AttackCannotAct] = "attack_cannot_act",
            [InputRejectionReason.AbilityCannotCast] = "ability_cannot_cast",
            [InputRejectionReason.AbilityMissingAim] = "ability_missing_aim",
        };

        foreach (var (reason, label) in expected)
        {
            Assert.Contains(reason, InputRejection.All);
            Assert.Equal(label, InputRejection.Label(reason));
            Assert.NotEqual(RejectionWeight.Forged, InputRejection.Weight(reason));
            Assert.Equal(0, metrics.InputsRejected(reason));
        }

        metrics.RecordInputRejected(InputRejectionReason.AttackCannotAct);
        Assert.Equal(1, metrics.InputsRejected(InputRejectionReason.AttackCannotAct));
    }
}
