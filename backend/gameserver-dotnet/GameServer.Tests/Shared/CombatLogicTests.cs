using Shared.GameLogic;

namespace GameServer.Tests.Shared;

public class CombatLogicTests
{
    // --- CalculateDamage (table-driven) ---

    [Theory]
    [InlineData(10, 5, 5)]     // 10 atk - 5 def = 5
    [InlineData(5, 10, 1)]     // 5 atk - 10 def = min 1
    [InlineData(10, 0, 10)]    // 10 atk - 0 def = 10
    [InlineData(1, 100, 1)]    // min damage always 1
    [InlineData(100, 0, 100)]  // high attack, no defense
    [InlineData(10, 9, 1)]     // barely above min
    [InlineData(10, 10, 1)]    // equal atk/def = min 1 (since 0 -> min 1)
    [InlineData(0, 0, 1)]      // both zero = min 1
    public void CalculateDamage_Cases(int atk, int def, int expected)
    {
        var attacker = new EntityState { Attack = atk };
        var defender = new EntityState { Defense = def };
        var damage = CombatLogic.CalculateDamage(in attacker, in defender);
        Assert.Equal(expected, damage);
    }

    [Fact]
    public void CalculateDamage_NeverReturnsLessThanMinDamage()
    {
        var attacker = new EntityState { Attack = 1 };
        var defender = new EntityState { Defense = 9999 };
        var damage = CombatLogic.CalculateDamage(in attacker, in defender);
        Assert.True(damage >= GameConstants.MinDamage);
    }

    // --- HandleDeath ---

    [Fact]
    public void HandleDeath_KillsWhenHpZero()
    {
        var entity = TestHelpers.CreatePlayer("p1", hp: 0);
        var died = CombatLogic.HandleDeath(ref entity);
        Assert.True(died);
        Assert.True(entity.Dead);
    }

    [Fact]
    public void HandleDeath_KillsWhenHpNegative()
    {
        var entity = TestHelpers.CreatePlayer("p1", hp: -5);
        var died = CombatLogic.HandleDeath(ref entity);
        Assert.True(died);
        Assert.True(entity.Dead);
    }

    [Fact]
    public void HandleDeath_ReturnsFalseIfAlreadyDead()
    {
        var entity = TestHelpers.CreatePlayer("p1", hp: 0);
        entity.Dead = true;
        var died = CombatLogic.HandleDeath(ref entity);
        Assert.False(died);
    }

    [Fact]
    public void HandleDeath_ReturnsFalseIfHpPositive()
    {
        var entity = TestHelpers.CreatePlayer("p1", hp: 50);
        var died = CombatLogic.HandleDeath(ref entity);
        Assert.False(died);
        Assert.False(entity.Dead);
    }

    [Fact]
    public void HandleDeath_OneHp_DoesNotKill()
    {
        var entity = TestHelpers.CreatePlayer("p1", hp: 1);
        var died = CombatLogic.HandleDeath(ref entity);
        Assert.False(died);
        Assert.False(entity.Dead);
    }

    // --- ValidateAttack ---

    [Fact]
    public void ValidateAttack_TargetDead_ReturnsError()
    {
        var attacker = TestHelpers.CreatePlayer("p1", x: 0, y: 0);
        var target = TestHelpers.CreatePlayer("p2", x: 1, y: 0, hp: 0);
        target.Dead = true;

        var result = CombatLogic.ValidateAttack(in attacker, in target, nowTicks: 0);
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateAttack_OutOfRange_ReturnsError()
    {
        var attacker = TestHelpers.CreatePlayer("p1", x: 0, y: 0);
        var target = TestHelpers.CreatePlayer("p2", x: 100, y: 100);

        var result = CombatLogic.ValidateAttack(in attacker, in target, nowTicks: 0);
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateAttack_OnCooldown_ReturnsError()
    {
        var attacker = TestHelpers.CreatePlayer("p1", x: 0, y: 0);
        attacker.CooldownUntilTicks = 1000; // Cooldown until tick 1000
        var target = TestHelpers.CreatePlayer("p2", x: 1, y: 0);

        var result = CombatLogic.ValidateAttack(in attacker, in target, nowTicks: 500);
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateAttack_Valid_ReturnsNull()
    {
        var attacker = TestHelpers.CreatePlayer("p1", x: 0, y: 0);
        attacker.CooldownUntilTicks = 0;
        var target = TestHelpers.CreatePlayer("p2", x: 1, y: 0);

        var result = CombatLogic.ValidateAttack(in attacker, in target, nowTicks: 100);
        Assert.Null(result);
    }

    [Fact]
    public void ValidateAttack_ExactCooldownExpiry_ReturnsNull()
    {
        var attacker = TestHelpers.CreatePlayer("p1", x: 0, y: 0);
        attacker.CooldownUntilTicks = 100;
        var target = TestHelpers.CreatePlayer("p2", x: 1, y: 0);

        var result = CombatLogic.ValidateAttack(in attacker, in target, nowTicks: 100);
        Assert.Null(result);
    }

    // Attacker dead check is handled by InputHandler (same as Go:
    // "if entity == nil || entity.Dead { return }"), not by CombatLogic.

    // --- InRange ---

    [Fact]
    public void InRange_Within_ReturnsTrue()
    {
        var a = new Vec2(0f, 0f);
        var b = new Vec2(1f, 1f);
        Assert.True(CombatLogic.InRange(a, b, GameConstants.AttackRange));
    }

    [Fact]
    public void InRange_Outside_ReturnsFalse()
    {
        var a = new Vec2(0f, 0f);
        var b = new Vec2(100f, 100f);
        Assert.False(CombatLogic.InRange(a, b, GameConstants.AttackRange));
    }

    [Fact]
    public void InRange_ExactBoundary_ReturnsTrue()
    {
        var a = new Vec2(0f, 0f);
        var b = new Vec2(GameConstants.AttackRange, 0f);
        Assert.True(CombatLogic.InRange(a, b, GameConstants.AttackRange));
    }

    [Fact]
    public void InRange_SlightlyOverBoundary_ReturnsFalse()
    {
        var a = new Vec2(0f, 0f);
        var b = new Vec2(GameConstants.AttackRange + 0.01f, 0f);
        Assert.False(CombatLogic.InRange(a, b, GameConstants.AttackRange));
    }

    [Fact]
    public void InRange_SamePosition_ReturnsTrue()
    {
        var pos = new Vec2(5f, 5f);
        Assert.True(CombatLogic.InRange(pos, pos, GameConstants.AttackRange));
    }

    [Fact]
    public void InRange_ZeroRange_SamePosition_ReturnsTrue()
    {
        var pos = new Vec2(5f, 5f);
        Assert.True(CombatLogic.InRange(pos, pos, 0f));
    }

    [Fact]
    public void InRange_ZeroRange_DifferentPositions_ReturnsFalse()
    {
        var a = new Vec2(0f, 0f);
        var b = new Vec2(1f, 0f);
        Assert.False(CombatLogic.InRange(a, b, 0f));
    }
}
