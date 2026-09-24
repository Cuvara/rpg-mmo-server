using GameServer.Input;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Input;

/// <summary>
/// The blind spot itself, demonstrated against a real <see cref="EcsWorld"/> and a real
/// <see cref="InputHandler"/> rather than against the audit's own arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AttackRateAuditTests"/> proves the audit's rule. It cannot prove the rule is
/// needed, because it never runs the validator. This does: every attack below goes through
/// <c>InputHandler.ProcessInput</c> and <c>CombatLogic.ValidateAttack</c>, and the
/// assertions are that <b>the validator refuses nothing</b> while the account lands attacks
/// far above the rate one cooldown permits.
/// </para>
/// <para>
/// The entity is replaced between swings with <c>RemoveEntity</c>/<c>AddEntity</c> — the
/// same pair the server runs on a map transfer, which removes a player's entity with no
/// hold (<c>Server/GameServer.cs</c>, "Clean removal"). It is a stand-in for any route that
/// hands an account a fresh entity, not a claim that a transfer loop is practical: the point
/// is what the validator does when one occurs, and the answer is nothing.
/// </para>
/// </remarks>
public class AttackRateAuditSeamTests
{
    private const int TickRate = GameConstants.DefaultTickRate;

    private static InputData Attack(ulong tick, string targetId) =>
        new(tick: tick, moveX: 0f, moveY: 0f, attackTargetId: targetId);

    [Fact]
    public void ReplacingTheEntityClearsTheCooldown_AndTheValidatorRefusesNothing()
    {
        using var world = new EcsWorld();
        var audit = new AttackRateAudit(TickRate, GameConstants.AttackCooldownTicks(TickRate));

        var handler = new InputHandler(
            world, NullLogger.Instance, null, TickRate, MapBounds.Default,
            onAttackAccepted: (userId, tick) => audit.RecordAccepted(userId, tick));

        // A target that cannot die, so nothing else stops the run.
        world.AddEntity(TestHelpers.CreateMob("m1", x: 1, y: 0, hp: 1_000_000, def: 0));
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0, atk: 1));

        // Every tick, for two audit windows. Without the replacement below, the cooldown
        // would refuse all but one swing in eight.
        for (ulong tick = 1; tick <= (ulong)(audit.WindowTicks * 2); tick++)
        {
            handler.ProcessInput("p1", Attack(tick, "m1"), currentTick: tick);

            // The map-transfer shape: entity gone, then back, with no cooldown carried.
            world.RemoveEntity("p1");
            world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0, atk: 1));
        }

        int swings = audit.WindowTicks * 2;

        // THE POINT: the per-attack rule was satisfied every single time.
        Assert.Equal(swings, handler.Attacks.Received);
        Assert.Equal(swings, handler.Attacks.Accepted);
        Assert.Equal(0, handler.Attacks.Rejected);

        // ... and one attack per tick is eight times what the cooldown allows, which only
        // the account-keyed audit can say.
        Assert.True(audit.ViolationsFor("p1") > 0,
            "the validator accepted every swing and nothing flagged the rate");
    }

    [Fact]
    public void WithoutTheReplacement_TheSamePlayerIsRefusedAndNothingIsFlagged()
    {
        using var world = new EcsWorld();
        var audit = new AttackRateAudit(TickRate, GameConstants.AttackCooldownTicks(TickRate));

        var handler = new InputHandler(
            world, NullLogger.Instance, null, TickRate, MapBounds.Default,
            onAttackAccepted: (userId, tick) => audit.RecordAccepted(userId, tick));

        world.AddEntity(TestHelpers.CreateMob("m1", x: 1, y: 0, hp: 1_000_000, def: 0));
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0, atk: 1));

        // The negative control the test above needs: identical input, one entity. If this
        // also came out clean the first test would prove nothing about the replacement.
        for (ulong tick = 1; tick <= (ulong)(audit.WindowTicks * 2); tick++)
            handler.ProcessInput("p1", Attack(tick, "m1"), currentTick: tick);

        Assert.True(handler.Attacks.Rejected > 0, "the cooldown refused nothing");
        Assert.Equal(0, audit.ViolationsFor("p1"));
    }
}
