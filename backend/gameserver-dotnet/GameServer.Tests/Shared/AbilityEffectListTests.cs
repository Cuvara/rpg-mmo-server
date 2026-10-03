using Shared.GameLogic.Content;

namespace GameServer.Tests.Shared;

/// <summary>
/// ADR-30 additions to <see cref="AbilityLogic"/>: projectile delivery, crowd-control gating
/// and per-effect resolution.
/// </summary>
public class AbilityEffectListTests
{
    private static EntityState Entity(float x = 0f, float y = 0f, int hp = 100, int maxHp = 100,
        int attack = 10, int defense = 5, bool dead = false) =>
        new()
        {
            Id = "e",
            Type = "player",
            Position = new Vec2(x, y),
            Hp = hp,
            MaxHp = maxHp,
            Attack = attack,
            Defense = defense,
            Dead = dead,
        };

    private static AbilityDefinition Projectile() =>
        new(1, "bolt", AbilityDelivery.Projectile, new[] { EffectSpec.Damage(10) }, 0f, 0f, 5,
            new ProjectileSpec(20f, 0.5f, 30f));

    private static StatusSet With(CrowdControl cc)
    {
        var set = new StatusSet();
        set.Apply(new StatusDefinition(1, "cc", 0, 1, PeriodicSpec.None, Array.Empty<StatModifier>(), cc,
            (cc & CrowdControl.Slow) != 0 ? 100 : 0), 0, null);
        return set;
    }

    [Fact]
    public void Projectile_AcceptsAnAimBeyondItsRange_BecauseAimIsOnlyADirection()
    {
        var caster = Entity();
        var aim = new Vec2(1000f, 0f);
        Assert.Null(AbilityLogic.ValidateCast(in caster, Projectile(), in caster, false, in aim, 1));
    }

    [Fact]
    public void Projectile_RefusesAnAimOnTheCaster_WhichHasNoDirection()
    {
        var caster = Entity(3f, 4f);
        var aim = new Vec2(3f, 4f);
        Assert.Same(AbilityLogic.MissingAimRejection,
            AbilityLogic.ValidateCast(in caster, Projectile(), in caster, false, in aim, 1));
    }

    [Theory]
    [InlineData(CrowdControl.Stun, true)]
    [InlineData(CrowdControl.Silence, true)]
    [InlineData(CrowdControl.Root, false)]
    [InlineData(CrowdControl.Slow, false)]
    public void CrowdControl_GatesCasting(CrowdControl cc, bool refused)
    {
        var caster = Entity();
        var aim = new Vec2(5f, 0f);
        string? error = AbilityLogic.ValidateCast(in caster, Projectile(), in caster, false, in aim, 1, With(cc));
        if (refused) Assert.Same(AbilityLogic.CannotCastRejection, error);
        else Assert.Null(error);
    }

    [Fact]
    public void ADeadCaster_IsToldItIsDead_BeforeItIsToldItIsSilenced()
    {
        var caster = Entity(dead: true);
        var aim = new Vec2(5f, 0f);
        Assert.Same(AbilityLogic.CasterDeadRejection,
            AbilityLogic.ValidateCast(in caster, Projectile(), in caster, false, in aim, 1, With(CrowdControl.Silence)));
    }

    [Fact]
    public void NullStatuses_BehaveLikeTheOriginalOverload()
    {
        var caster = Entity();
        var aim = new Vec2(5f, 0f);
        Assert.Null(AbilityLogic.ValidateCast(in caster, Projectile(), in caster, false, in aim, 1, null));
    }

    [Fact]
    public void EffectDamageAndHeal_FollowTheLegacyRules()
    {
        var caster = Entity(attack: 10);
        var target = Entity(hp: 90, maxHp: 100, defense: 5);

        Assert.Equal(15, AbilityLogic.CalculateEffectDamage(in caster, EffectSpec.Damage(10), in target));
        Assert.Equal(GameConstants.MinDamage, AbilityLogic.CalculateEffectDamage(in caster, EffectSpec.Damage(0), Entity(defense: 500)));
        Assert.Equal(10, AbilityLogic.CalculateEffectHeal(EffectSpec.Heal(50), in target));
        Assert.Equal(0, AbilityLogic.ClampHeal(-5, in target));

        // Same answer as the legacy single-effect path for the same numbers.
        var legacy = new AbilityDefinition(1, "x", AbilityTargeting.Entity, AbilityEffect.Damage, 5f, 0f, 10, 0);
        Assert.Equal(AbilityLogic.CalculateAbilityDamage(in caster, legacy, in target),
            AbilityLogic.CalculateEffectDamage(in caster, legacy.GetEffect(0), in target));
    }
}
