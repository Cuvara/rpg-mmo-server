using GameServer.Input;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Input;

/// <summary>
/// The attack-path counters exist because a rejected attack is otherwise invisible on a
/// server running at Information level: from the outside, a client attacking out of range
/// looks identical to a client not attacking at all, and that ambiguity cost a live
/// investigation (zero leaderboard kills, no way to tell which link was broken). These
/// tests pin the classification — every attack input lands in exactly one bucket, and
/// kills are counted only for attacks that actually killed.
/// </summary>
public class AttackTelemetryTests
{
    private const int TickRate = GameConstants.DefaultTickRate;

    private static InputHandler NewHandler(EcsWorld world) =>
        new(world, NullLogger.Instance, null, TickRate, MapBounds.Default);

    private static InputData Attack(ulong tick, string targetId) =>
        new(tick: tick, moveX: 0f, moveY: 0f, attackTargetId: targetId);

    [Fact]
    public void InRangeAttack_CountsReceivedAndAccepted_AndKillCountsOnlyOnDeath()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0, atk: 10));
        // Defense 3 vs attack 10 → 7 damage per hit; hp 8 → second hit kills.
        world.AddEntity(TestHelpers.CreateMob("m1", x: 1, y: 0, hp: 8, def: 3));
        var handler = NewHandler(world);

        handler.ProcessInput("p1", Attack(tick: 1, targetId: "m1"), currentTick: 1);
        Assert.Equal(1, handler.Attacks.Received);
        Assert.Equal(1, handler.Attacks.Accepted);
        Assert.Equal(0, handler.Attacks.Kills);

        // Past the cooldown window so the second swing is valid.
        ulong next = 1UL + (ulong)GameConstants.AttackCooldownTicks(TickRate);
        handler.ProcessInput("p1", Attack(tick: next, targetId: "m1"), currentTick: next);

        Assert.Equal(2, handler.Attacks.Received);
        Assert.Equal(2, handler.Attacks.Accepted);
        Assert.Equal(1, handler.Attacks.Kills);
        Assert.Equal(0, handler.Attacks.Rejected);
        Assert.Equal(0, handler.Attacks.Unresolved);
    }

    [Fact]
    public void OutOfRangeAttack_CountsRejected_AndKeepsTheValidatorReason()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        world.AddEntity(TestHelpers.CreateMob("m1", x: GameConstants.AttackRange + 5f, y: 0));
        var handler = NewHandler(world);

        handler.ProcessInput("p1", Attack(tick: 1, targetId: "m1"), currentTick: 1);

        Assert.Equal(1, handler.Attacks.Received);
        Assert.Equal(1, handler.Attacks.Rejected);
        Assert.Equal(0, handler.Attacks.Accepted);
        // The exact wording belongs to CombatLogic; what matters here is that the
        // breadcrumb is the validator's reason, not something re-derived.
        Assert.NotNull(handler.Attacks.LastRejection);
        Assert.Contains("range", handler.Attacks.LastRejection);
    }

    [Fact]
    public void AttackOnMissingTarget_CountsUnresolved_NotRejected()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        var handler = NewHandler(world);

        handler.ProcessInput("p1", Attack(tick: 1, targetId: "despawned-mob"), currentTick: 1);

        Assert.Equal(1, handler.Attacks.Received);
        Assert.Equal(1, handler.Attacks.Unresolved);
        Assert.Equal(0, handler.Attacks.Rejected);
        Assert.Equal(0, handler.Attacks.Accepted);
        Assert.Null(handler.Attacks.LastRejection);
    }

    [Fact]
    public void InputWithoutAttackTarget_CountsNothing()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        var handler = NewHandler(world);

        handler.ProcessInput("p1",
            new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null), currentTick: 1);

        Assert.Equal(0, handler.Attacks.Received);
        Assert.Equal(0, handler.Attacks.Unresolved);
        Assert.Equal(0, handler.Attacks.Rejected);
        Assert.Equal(0, handler.Attacks.Accepted);
        Assert.Equal(0, handler.Attacks.Kills);
    }
}
