using Shared.GameLogic.Content;

namespace GameServer.Tests.Content;

/// <summary>
/// ADR-30 content schema: stats, statuses and effect-list abilities in
/// <see cref="ContentDatabase"/> and <see cref="ContentValidation"/>, plus the mapping of the
/// protocol 2 ability shape onto the new one. Placeholder numbers only.
/// </summary>
public class StatusContentTests
{
    private static StatusDefinition Status(
        uint id,
        string? key = null,
        int duration = 10,
        int maxStacks = 1,
        PeriodicSpec periodic = default,
        StatModifier[]? modifiers = null,
        CrowdControl cc = CrowdControl.None,
        int slow = 0) =>
        new(id, key ?? "status_" + id, duration, maxStacks, periodic, modifiers ?? Array.Empty<StatModifier>(), cc, slow);

    private static AbilityDefinition Ability(
        uint id, AbilityDelivery delivery, EffectSpec[] effects,
        float range = 10f, float radius = 0f, ProjectileSpec projectile = default) =>
        new(id, "ability " + id, delivery, effects, range, radius, cooldownTicks: 5, projectile);

    private static List<string> Validate(
        StatDefinition[]? stats = null, StatusDefinition[]? statuses = null, AbilityDefinition[]? abilities = null)
    {
        var db = new ContentDatabase(
            Array.Empty<ItemDefinition>(),
            abilities ?? Array.Empty<AbilityDefinition>(),
            stats ?? Array.Empty<StatDefinition>(),
            statuses ?? Array.Empty<StatusDefinition>(),
            "test");
        var errors = new List<string>();
        bool ok = ContentValidation.Validate(db, errors);
        Assert.Equal(errors.Count == 0, ok);
        return errors;
    }

    // ── Legacy ability shape ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(AbilityTargeting.Self, AbilityEffect.Heal)]
    [InlineData(AbilityTargeting.Entity, AbilityEffect.Damage)]
    [InlineData(AbilityTargeting.Ground, AbilityEffect.Damage)]
    public void LegacyConstructor_MapsOntoDeliveryAndASingleEffect(AbilityTargeting targeting, AbilityEffect effect)
    {
        var a = new AbilityDefinition(3, "legacy", targeting, effect, 8f, 2f, power: 25, cooldownTicks: 4);

        Assert.Equal((AbilityDelivery)targeting, a.Delivery);
        Assert.Equal(targeting, a.Targeting);
        Assert.Equal(effect, a.Effect);
        Assert.Equal(25, a.Power);
        var only = Assert.Single(a.Effects);
        Assert.Equal(effect == AbilityEffect.Heal ? EffectSpec.Heal(25) : EffectSpec.Damage(25), only);
        Assert.Equal(ProjectileSpec.None, a.Projectile);
    }

    [Fact]
    public void LegacyView_OfAnEffectListAbility_ReadsTheFirstDirectEffect()
    {
        var a = Ability(1, AbilityDelivery.Entity, new[] { EffectSpec.ApplyStatus(2), EffectSpec.Heal(7), EffectSpec.Damage(9) });
        Assert.Equal(AbilityEffect.Heal, a.Effect);
        Assert.Equal(7, a.Power);

        var statusOnly = Ability(2, AbilityDelivery.Entity, new[] { EffectSpec.ApplyStatus(2) });
        Assert.Equal(AbilityEffect.Damage, statusOnly.Effect);
        Assert.Equal(0, statusOnly.Power);
    }

    [Fact]
    public void Projectile_ReadsAsGroundToLegacyCallers_AndNeedsAim()
    {
        var a = Ability(1, AbilityDelivery.Projectile, new[] { EffectSpec.Damage(5) },
            projectile: new ProjectileSpec(20f, 0.5f, 30f));
        Assert.Equal(AbilityTargeting.Ground, a.Targeting);
        Assert.True(a.NeedsAim);
        Assert.False(a.NeedsTarget);
        Assert.Equal(20f, a.Projectile.Speed);
    }

    [Fact]
    public void EffectList_IsCopied_SoTheCallersArrayCannotChangeIt()
    {
        var effects = new[] { EffectSpec.Damage(5) };
        var a = Ability(1, AbilityDelivery.Self, effects);
        effects[0] = EffectSpec.Heal(99);
        Assert.Equal(EffectSpec.Damage(5), a.GetEffect(0));
    }

    [Fact]
    public void ALegacyAbilityWithAnUndefinedEffect_IsStillReported()
    {
        var a = new AbilityDefinition(1, "x", AbilityTargeting.Self, (AbilityEffect)7, 0f, 0f, 1, 0);
        var errors = Validate(abilities: new[] { a });
        Assert.Contains(errors, e => e.Contains("effect '7'"));
    }

    // ── Database ────────────────────────────────────────────────────────────────

    [Fact]
    public void Database_LooksUpStatsAndStatuses_AndZeroIsNeverAnId()
    {
        var db = new ContentDatabase(
            Array.Empty<ItemDefinition>(), Array.Empty<AbilityDefinition>(),
            new[] { new StatDefinition(1, "mana", 50) }, new[] { Status(1) }, "h");

        Assert.Equal(1, db.StatCount);
        Assert.Equal(1, db.StatusCount);
        Assert.Equal("mana", db.GetStat(1).Key);
        Assert.Equal(50, db.GetStat(1).DefaultValue);
        Assert.True(db.TryGetStatus(1, out _));
        Assert.False(db.TryGetStat(0, out _));
        Assert.False(db.TryGetStatus(0, out _));
        Assert.False(db.TryGetStatus(2, out _));
        Assert.Throws<KeyNotFoundException>(() => db.GetStatus(2));
        Assert.Equal(0, ContentDatabase.Empty.StatusCount);
    }

    [Fact]
    public void DuplicateStatOrStatusIds_AreRefusedAtConstruction()
    {
        var ex1 = Assert.Throws<ArgumentException>(() => new ContentDatabase(
            Array.Empty<ItemDefinition>(), Array.Empty<AbilityDefinition>(),
            new[] { new StatDefinition(2, "a", 0), new StatDefinition(2, "b", 0) },
            Array.Empty<StatusDefinition>(), "h"));
        Assert.Contains("Duplicate stat id 2", ex1.Message);

        var ex2 = Assert.Throws<ArgumentException>(() => new ContentDatabase(
            Array.Empty<ItemDefinition>(), Array.Empty<AbilityDefinition>(),
            Array.Empty<StatDefinition>(), new[] { Status(3), Status(3, key: "other") }, "h"));
        Assert.Contains("Duplicate status id 3", ex2.Message);
    }

    // ── Validation: stats ─────────────────────────────────────────────────────────

    [Fact]
    public void ValidStatsAndStatuses_PassValidation()
    {
        var stats = new[] { new StatDefinition(1, "mana", 100), new StatDefinition(2, "fire_resist", 0) };
        var statuses = new[]
        {
            Status(1, "burn", duration: 30, maxStacks: 3, periodic: PeriodicSpec.DamageOverTime(10, 4)),
            Status(2, "regen", duration: 0, periodic: PeriodicSpec.HealOverTime(5, 2)),
            Status(3, "warcry", modifiers: new[]
            {
                StatModifier.For(StatModifierTarget.Attack, 5, 100),
                StatModifier.For(StatModifierTarget.Speed, 0, 150),
                StatModifier.ForStat(2, 10, 0),
            }),
            Status(4, "frost", cc: CrowdControl.Slow | CrowdControl.Silence, slow: 400),
            Status(5, "marker"), // no effect at all: a legitimate tag status
        };
        var abilities = new[]
        {
            Ability(1, AbilityDelivery.Projectile, new[] { EffectSpec.Damage(10), EffectSpec.ApplyStatus(1) },
                projectile: new ProjectileSpec(25f, 0.4f, 40f)),
            Ability(2, AbilityDelivery.Ground, new[] { EffectSpec.ApplyStatus(4) }, range: 15f, radius: 3f),
        };

        Assert.Empty(Validate(stats, statuses, abilities));
    }

    [Fact]
    public void StatIdZero_BadKeys_AndDuplicateKeys_AreReported()
    {
        var errors = Validate(stats: new[]
        {
            new StatDefinition(0, "zero", 0),
            new StatDefinition(1, "Mana", 0),
            new StatDefinition(2, "hp_regen", 0),
            new StatDefinition(3, "hp_regen", 0),
            new StatDefinition(4, "", 0),
        });

        Assert.Contains(errors, e => e.StartsWith("stat: id is 0"));
        Assert.Contains(errors, e => e.Contains("stat 1") && e.Contains("lowercase"));
        Assert.Contains(errors, e => e.Contains("stat 2 and stat 3") && e.Contains("hp_regen"));
        Assert.Contains(errors, e => e.Contains("stat 4") && e.Contains("key is empty"));
        Assert.Equal(4, errors.Count);
    }

    // ── Validation: statuses ─────────────────────────────────────────────────────

    public static TheoryData<string, StatusDefinition, string> BadStatuses() => new()
    {
        { "id zero", Status(0), "status: id is 0" },
        { "negative duration", Status(1, duration: -1), "durationTicks is -1" },
        { "zero stacks", Status(1, maxStacks: 0), "maxStacks is 0" },
        { "absurd stacks", Status(1, maxStacks: 1_000_000), "maxStacks is 1000000" },
        { "periodic no interval", Status(1, periodic: PeriodicSpec.DamageOverTime(0, 3)), "intervalTicks is 0" },
        { "periodic no amount", Status(1, periodic: PeriodicSpec.HealOverTime(2, 0)), "amount is 0" },
        { "periodic negative amount", Status(1, periodic: PeriodicSpec.DamageOverTime(2, -5)), "amount is -5" },
        { "half-written periodic", Status(1, periodic: new PeriodicSpec(PeriodicKind.None, 3, 1)), "is not periodic" },
        { "unknown periodic kind", Status(1, periodic: new PeriodicSpec((PeriodicKind)9, 3, 1)), "periodic kind '9'" },
        { "modifier on missing stat", Status(1, modifiers: new[] { StatModifier.ForStat(42, 1, 0) }), "stat 42 does not exist" },
        { "modifier statId on combat stat", Status(1, modifiers: new[] { new StatModifier(StatModifierTarget.Attack, 3, 1, 0) }), "statId is 3" },
        { "speed additive", Status(1, modifiers: new[] { StatModifier.For(StatModifierTarget.Speed, 2, 0) }), "multiplier-only" },
        { "multiplier below -1000", Status(1, modifiers: new[] { StatModifier.For(StatModifierTarget.Defense, 0, -1001) }), "-1001" },
        { "no-op modifier", Status(1, modifiers: new[] { StatModifier.For(StatModifierTarget.Defense, 0, 0) }), "does nothing" },
        { "unknown modifier target", Status(1, modifiers: new[] { StatModifier.For((StatModifierTarget)77, 1, 0) }), "target '77'" },
        { "unknown cc flag", Status(1, cc: (CrowdControl)64), "unknown flags 0x40" },
        { "slow without amount", Status(1, cc: CrowdControl.Slow), "slowPermille is 0" },
        { "slow above 1000", Status(1, cc: CrowdControl.Slow, slow: 1001), "slowPermille is 1001" },
        { "slow amount without flag", Status(1, slow: 200), "does not include Slow" },
        { "bad key", Status(1, key: "Bad Key"), "lowercase" },
    };

    [Theory]
    [MemberData(nameof(BadStatuses))]
    public void BadStatuses_AreReported_WithTheIdAndTheField(string what, StatusDefinition status, string expected)
    {
        _ = what;
        var errors = Validate(statuses: new[] { status });
        Assert.Contains(errors, e => e.Contains(expected));
        if (status.Id != 0) Assert.All(errors, e => Assert.StartsWith("status 1", e));
    }

    [Fact]
    public void DuplicateStatusKeys_AreReported()
    {
        var errors = Validate(statuses: new[] { Status(1, "burn"), Status(2, "burn") });
        Assert.Contains("status 1 and status 2: both use key 'burn'", Assert.Single(errors));
    }

    // ── Validation: effect-list abilities ─────────────────────────────────────────

    [Fact]
    public void AbilityReferencingAMissingStatus_IsReported()
    {
        var errors = Validate(
            statuses: new[] { Status(1) },
            abilities: new[] { Ability(4, AbilityDelivery.Entity, new[] { EffectSpec.Damage(1), EffectSpec.ApplyStatus(9) }) });
        Assert.Contains("ability 4: effect 1: applies status 9, which does not exist", Assert.Single(errors));
    }

    [Fact]
    public void AbilityEffectEdgeCases_AreReported()
    {
        var errors = Validate(abilities: new[]
        {
            Ability(1, AbilityDelivery.Self, Array.Empty<EffectSpec>()),
            Ability(2, AbilityDelivery.Self, new[] { EffectSpec.ApplyStatus(0) }),
            Ability(3, AbilityDelivery.Self, new[] { EffectSpec.Damage(-1) }),
            Ability(4, AbilityDelivery.Self, new[] { new EffectSpec((EffectKind)5, 1, 0) }),
            Ability(5, AbilityDelivery.Self, new[] { new EffectSpec(EffectKind.Heal, 1, 3) }),
            Ability(6, AbilityDelivery.Self, Enumerable.Repeat(EffectSpec.Damage(1), ContentValidation.MaxAbilityEffects + 1).ToArray()),
            Ability(7, (AbilityDelivery)9, new[] { EffectSpec.Damage(1) }),
        });

        Assert.Contains(errors, e => e.StartsWith("ability 1: has no effects"));
        Assert.Contains(errors, e => e.StartsWith("ability 2:") && e.Contains("status 0"));
        Assert.Contains(errors, e => e.StartsWith("ability 3:") && e.Contains("power is -1"));
        Assert.Contains(errors, e => e.StartsWith("ability 4:") && e.Contains("effect kind '5'"));
        Assert.Contains(errors, e => e.StartsWith("ability 5:") && e.Contains("names status 3"));
        Assert.Contains(errors, e => e.StartsWith("ability 6:") && e.Contains("limit is"));
        Assert.Contains(errors, e => e.StartsWith("ability 7:") && e.Contains("targeting '9'"));
    }

    [Fact]
    public void Projectile_NeedsPositiveFiniteSpeedRadiusAndRange_ButNotAbilityRange()
    {
        Assert.Empty(Validate(abilities: new[]
        {
            Ability(1, AbilityDelivery.Projectile, new[] { EffectSpec.Damage(1) }, range: 0f,
                projectile: new ProjectileSpec(10f, 0.5f, 20f)),
        }));

        var errors = Validate(abilities: new[]
        {
            Ability(2, AbilityDelivery.Projectile, new[] { EffectSpec.Damage(1) },
                projectile: new ProjectileSpec(float.NaN, 0f, float.PositiveInfinity)),
        });
        Assert.Contains(errors, e => e.Contains("projectile speed is NaN"));
        Assert.Contains(errors, e => e.Contains("projectile radius is 0"));
        Assert.Contains(errors, e => e.Contains("projectile range is"));
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void GroundAndEntityRangeRules_StillApply_ToEffectListAbilities()
    {
        var errors = Validate(abilities: new[]
        {
            Ability(1, AbilityDelivery.Entity, new[] { EffectSpec.Damage(1) }, range: 0f),
            Ability(2, AbilityDelivery.Ground, new[] { EffectSpec.Damage(1) }, range: 5f, radius: 0f),
        });
        Assert.Contains(errors, e => e.StartsWith("ability 1:") && e.Contains("range"));
        Assert.Contains(errors, e => e.StartsWith("ability 2:") && e.Contains("radius"));
    }
}
