using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Input;
using GameServer.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.Shared;

/// <summary>
/// Combat cooldowns are counted in simulation ticks, never wall-clock: the same input
/// sequence must resolve identically on every run and on a predicting client.
/// </summary>
public class TickCooldownTests
{
    // --- Conversion ---

    [Theory]
    [InlineData(15, 8)]   // default: ceil(500ms / 66.67ms) = 8 ticks = 533ms
    [InlineData(10, 5)]   // 500ms exactly
    [InlineData(20, 10)]  // 500ms exactly
    [InlineData(30, 15)]
    [InlineData(1, 1)]    // never rounds down to zero
    public void AttackCooldownTicks_MatchesWallClockDuration(int tickRate, int expectedTicks)
    {
        Assert.Equal(expectedTicks, GameConstants.AttackCooldownTicks(tickRate));
    }

    [Fact]
    public void AttackCooldownTicks_IsNeverShorterThanTheWallClockCooldown()
    {
        foreach (int rate in new[] { 5, 10, 12, 15, 20, 30, 60 })
        {
            double ms = GameConstants.AttackCooldownTicks(rate) * 1000.0 / rate;
            Assert.True(ms >= GameConstants.AttackCooldownMs,
                $"{rate}Hz cooldown is {ms}ms, shorter than {GameConstants.AttackCooldownMs}ms");
        }
    }

    [Fact]
    public void AttackCooldownTicks_NonPositiveRate_FallsBackToDefault()
    {
        Assert.Equal(GameConstants.AttackCooldownTicks(GameConstants.DefaultTickRate),
            GameConstants.AttackCooldownTicks(0));
    }

    // --- Validation boundary ---

    [Fact]
    public void ValidateAttack_BlocksUntilCooldownTick_ThenAllows()
    {
        var attacker = TestHelpers.CreatePlayer("a");
        attacker.CooldownUntilTick = 8;
        var target = TestHelpers.CreatePlayer("b", x: 1);

        Assert.NotNull(CombatLogic.ValidateAttack(in attacker, in target, 7));
        Assert.Null(CombatLogic.ValidateAttack(in attacker, in target, 8));
        Assert.Null(CombatLogic.ValidateAttack(in attacker, in target, 9));
    }

    // --- Behaviour through the input handler ---

    private static (InputHandler handler, GameWorld world) Build(int tickRate = GameConstants.DefaultTickRate)
    {
        var world = new GameWorld();
        var handler = new InputHandler(world, NullLogger.Instance, null, tickRate);
        return (handler, world);
    }

    [Fact]
    public void Attack_SetsCooldownInTicks_NotDateTimeTicks()
    {
        var (handler, world) = Build();
        using var worldScope = world;
        world.AddEntity(TestHelpers.CreatePlayer("a"));
        world.AddEntity(TestHelpers.CreatePlayer("b", x: 1));

        handler.ProcessInput("a", new InputData(1, 0, 0, "b"), currentTick: 100);

        var a = world.GetEntity("a")!.Value;
        Assert.Equal(100ul + (ulong)GameConstants.AttackCooldownTicks(GameConstants.DefaultTickRate),
            a.CooldownUntilTick);
        // A DateTime.Ticks value would be astronomically large; this proves the unit changed.
        Assert.True(a.CooldownUntilTick < 10_000);
    }

    [Fact]
    public void SecondAttackWithinCooldown_DealsNoDamage()
    {
        var (handler, world) = Build();
        using var worldScope = world;
        world.AddEntity(TestHelpers.CreatePlayer("a"));
        world.AddEntity(TestHelpers.CreatePlayer("b", x: 1));

        handler.ProcessInput("a", new InputData(1, 0, 0, "b"), currentTick: 1);
        int hpAfterFirst = world.GetEntity("b")!.Value.Hp;

        // 8-tick cooldown at 15Hz: tick 8 is still blocked (expiry is tick 9).
        handler.ProcessInput("a", new InputData(2, 0, 0, "b"), currentTick: 8);

        Assert.Equal(hpAfterFirst, world.GetEntity("b")!.Value.Hp);
    }

    [Fact]
    public void AttackAfterCooldownExpiry_DealsDamageAgain()
    {
        var (handler, world) = Build();
        using var worldScope = world;
        world.AddEntity(TestHelpers.CreatePlayer("a"));
        world.AddEntity(TestHelpers.CreatePlayer("b", x: 1));

        handler.ProcessInput("a", new InputData(1, 0, 0, "b"), currentTick: 1);
        int hpAfterFirst = world.GetEntity("b")!.Value.Hp;

        handler.ProcessInput("a", new InputData(2, 0, 0, "b"), currentTick: 9);

        Assert.True(world.GetEntity("b")!.Value.Hp < hpAfterFirst);
    }

    [Fact]
    public void AttackRateAt15Hz_MatchesWallClockRate()
    {
        // Equivalence check: over 150 ticks (10s at 15Hz) a player spamming attacks
        // lands the same number of hits the old 500ms wall-clock gate allowed (~20).
        var (handler, world) = Build();
        using var worldScope = world;
        world.AddEntity(TestHelpers.CreatePlayer("a"));
        world.AddEntity(TestHelpers.CreatePlayer("b", x: 1, hp: 100_000));

        int startHp = world.GetEntity("b")!.Value.Hp;
        for (ulong t = 1; t <= 150; t++)
        {
            handler.ProcessInput("a", new InputData(t, 0, 0, "b"), currentTick: t);
        }

        const int damagePerHit = 5; // test player: attack 10 - defense 5
        int hits = (startHp - world.GetEntity("b")!.Value.Hp) / damagePerHit;

        // 150 ticks / 8-tick cooldown = 19 hits (first at tick 1, then every 8 ticks).
        Assert.Equal(19, hits);
        // Wall-clock equivalent over the same 10s window: 10s / 0.5s = 20 hits.
        Assert.InRange(hits, 18, 20);
    }

    [Fact]
    public void CooldownIsDeterministic_AcrossRepeatedRuns()
    {
        // Same input sequence, two independent worlds: byte-identical outcome.
        // This is exactly what a wall-clock cooldown could not guarantee.
        static int RunOnce()
        {
            var world = new GameWorld();
            var handler = new InputHandler(world, NullLogger.Instance, null, GameConstants.DefaultTickRate);
            world.AddEntity(TestHelpers.CreatePlayer("a"));
            world.AddEntity(TestHelpers.CreatePlayer("b", x: 1, hp: 1000));
            for (ulong t = 1; t <= 100; t++)
            {
                handler.ProcessInput("a", new InputData(t, 0, 0, "b"), currentTick: t);
            }
            int hp = world.GetEntity("b")!.Value.Hp;
            world.Dispose();
            return hp;
        }

        int first = RunOnce();
        Thread.Sleep(30); // wall-clock advances; the simulation must not care
        Assert.Equal(first, RunOnce());
        Assert.Equal(first, RunOnce());
    }
}
