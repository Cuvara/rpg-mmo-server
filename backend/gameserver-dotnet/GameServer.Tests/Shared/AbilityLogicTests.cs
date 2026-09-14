using Shared.GameLogic.Components;
using Shared.GameLogic.Content;
using Shared.GameLogic.Systems;
using Xunit;

namespace GameServer.Tests.Shared;

/// <summary>
/// Ability validation and resolution, the rules a predicting or presenting client shares
/// with the server.
/// </summary>
public class AbilityLogicTests
{
    private static AbilityDefinition Ability(
        AbilityTargeting targeting = AbilityTargeting.Entity,
        AbilityEffect effect = AbilityEffect.Damage,
        float range = 10f, float radius = 0f, int power = 20, int cooldownTicks = 5) =>
        new(1, "test", targeting, effect, range, radius, power, cooldownTicks);

    private static EntityState Entity(
        string id = "e", float x = 0f, float y = 0f, int hp = 100, int maxHp = 100,
        int attack = 10, int defense = 5, bool dead = false, ulong abilityCooldownUntil = 0) =>
        new()
        {
            Id = id,
            Type = "player",
            Position = new Vec2(x, y),
            Hp = hp,
            MaxHp = maxHp,
            Attack = attack,
            Defense = defense,
            Dead = dead,
            AbilityCooldownUntilTick = abilityCooldownUntil,
        };

    [Fact]
    public void SelfCast_NeedsNoTarget()
    {
        var caster = Entity();

        string? error = AbilityLogic.ValidateCast(
            in caster, Ability(AbilityTargeting.Self, AbilityEffect.Heal),
            in caster, hasTarget: false, in caster.Position, currentTick: 1);

        Assert.Null(error);
    }

    [Fact]
    public void EntityCast_WithoutTarget_IsRefused()
    {
        var caster = Entity();
        EntityState none = default;

        string? error = AbilityLogic.ValidateCast(
            in caster, Ability(), in none, hasTarget: false, in caster.Position, currentTick: 1);

        Assert.Same(AbilityLogic.MissingTargetRejection, error);
    }

    [Fact]
    public void EntityCast_OutOfRange_IsRefused()
    {
        var caster = Entity(x: 0f);
        var target = Entity("t", x: 50f);

        string? error = AbilityLogic.ValidateCast(
            in caster, Ability(range: 10f), in target, hasTarget: true, in caster.Position, currentTick: 1);

        Assert.Same(AbilityLogic.OutOfRangeRejection, error);
    }

    [Fact]
    public void DeadTarget_IsRefused_BeforeTheCooldownIsSpent()
    {
        var caster = Entity();
        var target = Entity("t", x: 1f, dead: true);

        string? error = AbilityLogic.ValidateCast(
            in caster, Ability(), in target, hasTarget: true, in caster.Position, currentTick: 1);

        Assert.Same(AbilityLogic.TargetDeadRejection, error);
    }

    [Fact]
    public void DeadCaster_IsRefused_EvenForAKnownAbility()
    {
        var caster = Entity(dead: true);
        var target = Entity("t", x: 1f);

        string? error = AbilityLogic.ValidateCast(
            in caster, Ability(), in target, hasTarget: true, in caster.Position, currentTick: 1);

        Assert.Same(AbilityLogic.CasterDeadRejection, error);
    }

    /// <summary>
    /// A dead caster is reported as dead, not as "unknown ability", even when the ability is
    /// also unknown. The player can act on one of those and not the other.
    /// </summary>
    [Fact]
    public void DeadCaster_OutranksUnknownAbility()
    {
        var caster = Entity(dead: true);
        EntityState none = default;

        string? error = AbilityLogic.ValidateCast(
            in caster, null, in none, hasTarget: false, in caster.Position, currentTick: 1);

        Assert.Same(AbilityLogic.CasterDeadRejection, error);
    }

    [Fact]
    public void UnknownAbility_IsRefused()
    {
        var caster = Entity();
        EntityState none = default;

        string? error = AbilityLogic.ValidateCast(
            in caster, null, in none, hasTarget: false, in caster.Position, currentTick: 1);

        Assert.Same(AbilityLogic.UnknownAbilityRejection, error);
    }

    [Fact]
    public void Cooldown_IsRefusedUntilItExpires_AndIsInclusiveAtTheBoundary()
    {
        var caster = Entity(abilityCooldownUntil: 10);
        var target = Entity("t", x: 1f);
        var ability = Ability();

        Assert.Same(AbilityLogic.CooldownRejection, AbilityLogic.ValidateCast(
            in caster, ability, in target, hasTarget: true, in caster.Position, currentTick: 9));

        // currentTick < CooldownUntilTick is the test, so the expiry tick itself is allowed.
        Assert.Null(AbilityLogic.ValidateCast(
            in caster, ability, in target, hasTarget: true, in caster.Position, currentTick: 10));
    }

    [Fact]
    public void GroundCast_MeasuresRangeToTheAimPoint_NotToTheEdgeOfTheArea()
    {
        var caster = Entity(x: 0f);
        EntityState none = default;
        var ability = Ability(AbilityTargeting.Ground, range: 10f, radius: 5f);

        var inRange = new Vec2(9f, 0f);
        Assert.Null(AbilityLogic.ValidateCast(
            in caster, ability, in none, hasTarget: false, in inRange, currentTick: 1));

        // 12 units away is out of range even though the 5-unit radius would reach back to 7.
        var beyond = new Vec2(12f, 0f);
        Assert.Same(AbilityLogic.OutOfRangeRejection, AbilityLogic.ValidateCast(
            in caster, ability, in none, hasTarget: false, in beyond, currentTick: 1));
    }

    [Fact]
    public void AbilityDamage_AddsPowerToAttackBeforeSubtractingDefense()
    {
        var caster = Entity(attack: 10);
        var target = Entity("t", defense: 5);

        // 10 attack + 20 power - 5 defense
        Assert.Equal(25, AbilityLogic.CalculateAbilityDamage(in caster, Ability(power: 20), in target));
    }

    [Fact]
    public void AbilityDamage_NeverFallsBelowTheMinimum()
    {
        var caster = Entity(attack: 1);
        var target = Entity("t", defense: 1000);

        Assert.Equal(GameConstants.MinDamage,
            AbilityLogic.CalculateAbilityDamage(in caster, Ability(power: 0), in target));
    }

    /// <summary>
    /// The clamp is in the shared logic, not at the call site, so the number APPLIED and the
    /// number REPORTED cannot differ. A "500" floating beside an unmoved health bar reads to
    /// a player as the heal being eaten by something.
    /// </summary>
    [Fact]
    public void Heal_IsClampedToMissingHealth()
    {
        var target = Entity("t", hp: 90, maxHp: 100);

        Assert.Equal(10, AbilityLogic.CalculateHeal(Ability(effect: AbilityEffect.Heal, power: 500), in target));
    }

    [Fact]
    public void Heal_OnFullHealth_IsZero()
    {
        var target = Entity("t", hp: 100, maxHp: 100);

        Assert.Equal(0, AbilityLogic.CalculateHeal(Ability(effect: AbilityEffect.Heal, power: 50), in target));
    }

    [Fact]
    public void CooldownUntil_WithNoCooldown_LeavesTheAbilityImmediatelyUsable()
    {
        var ability = Ability(cooldownTicks: 0);

        ulong until = AbilityLogic.CooldownUntil(ability, currentTick: 42);

        Assert.Equal(42ul, until);

        // And the validator agrees, because its test is strictly less-than.
        var caster = Entity(abilityCooldownUntil: until);
        var target = Entity("t", x: 1f);
        Assert.Null(AbilityLogic.ValidateCast(
            in caster, ability, in target, hasTarget: true, in caster.Position, currentTick: 42));
    }

    [Fact]
    public void IsInArea_UsesTheRadiusAroundTheAimPoint()
    {
        var ability = Ability(AbilityTargeting.Ground, range: 50f, radius: 5f);
        var aim = new Vec2(10f, 0f);

        Assert.True(AbilityLogic.IsInArea(in aim, Entity("a", x: 12f), ability));
        Assert.False(AbilityLogic.IsInArea(in aim, Entity("b", x: 20f), ability));
    }
}
