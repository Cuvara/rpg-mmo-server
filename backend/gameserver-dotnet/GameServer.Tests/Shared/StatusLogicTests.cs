using Shared.GameLogic.Content;

namespace GameServer.Tests.Shared;

/// <summary>
/// <see cref="StatusSet"/>: stacking, ticking, expiry, effective stats and crowd control.
/// Every number here is a placeholder chosen to make the arithmetic visible, not content.
/// </summary>
public class StatusLogicTests
{
    private static StatusDefinition Status(
        uint id,
        int duration = 10,
        int maxStacks = 1,
        PeriodicSpec periodic = default,
        StatModifier[]? modifiers = null,
        CrowdControl cc = CrowdControl.None,
        int slow = 0) =>
        new(id, "status_" + id, duration, maxStacks, periodic, modifiers ?? Array.Empty<StatModifier>(), cc, slow);

    private static StatusTickResult[] Tick(StatusSet set, ulong tick)
    {
        var buf = new StatusTickResult[set.MaxResultsPerTick];
        int n = set.Tick(tick, buf);
        return buf.AsSpan(0, n).ToArray();
    }

    // ── Apply / stack / refresh ─────────────────────────────────────────────────

    [Fact]
    public void Apply_AddsThenStacksThenRefreshesAtTheCap()
    {
        var set = new StatusSet();
        var def = Status(1, duration: 10, maxStacks: 2);

        Assert.Equal(StatusApplyResult.Added, set.Apply(def, 100, "a"));
        Assert.Equal(StatusApplyResult.Stacked, set.Apply(def, 103, "b"));
        Assert.Equal(StatusApplyResult.Refreshed, set.Apply(def, 105, "c"));

        Assert.Equal(1, set.Count);
        Assert.True(set.TryGet(1, out var s));
        Assert.Equal(2, s.Stacks);
        Assert.Equal(100UL, s.AppliedTick);       // first application is kept
        Assert.Equal(115UL, s.ExpiresTick);       // refreshed from the latest one
        Assert.Equal("c", s.SourceId);            // most recent applier
    }

    [Fact]
    public void ZeroDuration_MeansUntilRemoved()
    {
        var set = new StatusSet();
        set.Apply(Status(1, duration: 0), 5, null);

        Assert.True(set.TryGet(1, out var s));
        Assert.Equal(0UL, s.ExpiresTick);
        Assert.Empty(Tick(set, 1_000_000));
        Assert.True(set.Contains(1));
        Assert.True(set.Remove(1));
        Assert.False(set.Remove(1));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void AFullSet_RejectsANewStatus_ButStillRefreshesAnActiveOne()
    {
        var set = new StatusSet(2);
        var a = Status(1, maxStacks: 3);
        Assert.Equal(StatusApplyResult.Added, set.Apply(a, 0, null));
        Assert.Equal(StatusApplyResult.Added, set.Apply(Status(2), 0, null));
        Assert.Equal(StatusApplyResult.Rejected, set.Apply(Status(3), 0, null));
        Assert.Equal(StatusApplyResult.Stacked, set.Apply(a, 1, null));
        Assert.False(set.Contains(3));
    }

    [Fact]
    public void Apply_ThroughContent_RejectsUnknownIds()
    {
        var db = new ContentDatabase(
            Array.Empty<ItemDefinition>(), Array.Empty<AbilityDefinition>(),
            Array.Empty<StatDefinition>(), new[] { Status(4) }, "h");
        var set = new StatusSet();

        Assert.Equal(StatusApplyResult.Added, StatusLogic.Apply(db, 4, set, 0, null));
        Assert.Equal(StatusApplyResult.Rejected, StatusLogic.Apply(db, 5, set, 0, null));
        Assert.Equal(StatusApplyResult.Rejected, StatusLogic.Apply(db, 0, set, 0, null));
    }

    // ── Tick ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dot_TicksEveryInterval_IncludingOnTheExpiryTick_ThenExpires()
    {
        var set = new StatusSet();
        set.Apply(Status(7, duration: 10, periodic: PeriodicSpec.DamageOverTime(2, 3)), 100, "caster");

        var all = new List<(ulong tick, StatusTickResult r)>();
        for (ulong t = 100; t <= 112; t++)
        {
            foreach (var r in Tick(set, t)) all.Add((t, r));
        }

        var dmgTicks = all.Where(x => x.r.Kind == StatusTickKind.PeriodicDamage).Select(x => x.tick).ToArray();
        Assert.Equal(new ulong[] { 102, 104, 106, 108, 110 }, dmgTicks);
        Assert.All(all.Where(x => x.r.Kind == StatusTickKind.PeriodicDamage), x =>
        {
            Assert.Equal(3, x.r.Amount);
            Assert.Equal(7u, x.r.StatusId);
            Assert.Equal("caster", x.r.SourceId);
        });

        var expiry = Assert.Single(all, x => x.r.Kind == StatusTickKind.Expired);
        Assert.Equal(110UL, expiry.tick);
        Assert.Equal(0, set.Count);

        // Periodic results come before the expiry within the same tick.
        int lastDmg = all.FindLastIndex(x => x.r.Kind == StatusTickKind.PeriodicDamage);
        int exp = all.FindIndex(x => x.r.Kind == StatusTickKind.Expired);
        Assert.True(lastDmg < exp);
    }

    [Fact]
    public void PeriodicAmount_ScalesWithStacks_AndReapplyingDoesNotResetTheRhythm()
    {
        var set = new StatusSet();
        var hot = Status(2, duration: 0, maxStacks: 5, periodic: PeriodicSpec.HealOverTime(4, 10));
        set.Apply(hot, 0, null);
        Assert.Empty(Tick(set, 3));
        set.Apply(hot, 3, null); // second stack one tick before the heal is due

        var r = Assert.Single(Tick(set, 4));
        Assert.Equal(StatusTickKind.PeriodicHeal, r.Kind);
        Assert.Equal(20, r.Amount);
        Assert.Equal(2, r.Stacks);
    }

    [Fact]
    public void SkippedTicks_AreCaughtUp_InOrder()
    {
        var set = new StatusSet();
        set.Apply(Status(1, duration: 0, periodic: PeriodicSpec.DamageOverTime(1, 1)), 0, null);
        Assert.Equal(5, Tick(set, 5).Length);
        Assert.Empty(Tick(set, 5));
    }

    [Fact]
    public void AFullResultBuffer_LosesNothing_TheNextCallContinues()
    {
        var set = new StatusSet();
        set.Apply(Status(1, duration: 3, periodic: PeriodicSpec.DamageOverTime(1, 1)), 0, null);
        set.Apply(Status(2, duration: 3, periodic: PeriodicSpec.DamageOverTime(1, 2)), 0, null);

        var small = new StatusTickResult[2];
        var seen = new List<StatusTickResult>();
        int n;
        do
        {
            n = set.Tick(3, small);
            seen.AddRange(small.AsSpan(0, n).ToArray());
        }
        while (n > 0);

        // 3 periodic + 1 expiry each, status 1 fully before status 2 (slot order).
        Assert.Equal(8, seen.Count);
        Assert.Equal(new uint[] { 1, 1, 1, 1, 2, 2, 2, 2 }, seen.Select(s => s.StatusId).ToArray());
        Assert.Equal(StatusTickKind.Expired, seen[3].Kind);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void Removal_KeepsApplicationOrder()
    {
        var set = new StatusSet();
        for (uint id = 1; id <= 4; id++) set.Apply(Status(id, duration: 0), 0, null);
        set.Remove(2);
        Assert.Equal(new uint[] { 1, 3, 4 }, Enumerable.Range(0, set.Count).Select(i => set.GetAt(i).StatusId).ToArray());
    }

    [Fact]
    public void Tick_DoesNotAllocate()
    {
        var set = new StatusSet();
        set.Apply(Status(1, duration: 0, periodic: PeriodicSpec.DamageOverTime(1, 1),
            modifiers: new[] { StatModifier.For(StatModifierTarget.Attack, 5, 100) }), 0, "s");
        var buf = new StatusTickResult[set.MaxResultsPerTick];
        set.Tick(1, buf); // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong t = 2; t < 1000; t++)
        {
            set.Tick(t, buf);
            _ = set.EffectiveAttack(10);
            _ = set.EffectiveSpeed(5f);
            _ = set.CanMove;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ── Effective stats ─────────────────────────────────────────────────────────

    [Fact]
    public void Modifiers_AddBeforeMultiplying_AndScaleWithStacks()
    {
        var set = new StatusSet();
        var buff = Status(1, duration: 0, maxStacks: 3,
            modifiers: new[] { StatModifier.For(StatModifierTarget.Attack, 2, 100) });
        set.Apply(buff, 0, null);
        set.Apply(buff, 0, null);

        // (10 + 2*2) * (1000 + 100*2) / 1000 = 14 * 1.2 = 16.8 -> 16 (truncated)
        Assert.Equal(16, set.EffectiveAttack(10));
        Assert.Equal(10, set.EffectiveDefense(10)); // other stats untouched
    }

    [Fact]
    public void Modifiers_FromSeveralStatuses_Combine_OrderIndependently()
    {
        var a = Status(1, duration: 0, modifiers: new[] { StatModifier.For(StatModifierTarget.Defense, 0, 500) });
        var b = Status(2, duration: 0, modifiers: new[] { StatModifier.For(StatModifierTarget.Defense, 0, -300) });

        var ab = new StatusSet();
        ab.Apply(a, 0, null);
        ab.Apply(b, 0, null);
        var ba = new StatusSet();
        ba.Apply(b, 0, null);
        ba.Apply(a, 0, null);

        Assert.Equal(120, ab.EffectiveDefense(100));
        Assert.Equal(ab.EffectiveDefense(100), ba.EffectiveDefense(100));
    }

    [Fact]
    public void EffectiveStats_AreFloored_AndMaxHpNeverReachesZero()
    {
        var set = new StatusSet();
        set.Apply(Status(1, duration: 0, modifiers: new[]
        {
            StatModifier.For(StatModifierTarget.Attack, -50, 0),
            StatModifier.For(StatModifierTarget.MaxHp, 0, -1000),
            StatModifier.ForStat(9, -50, 0),
        }), 0, null);

        Assert.Equal(0, set.EffectiveAttack(10));
        Assert.Equal(1, set.EffectiveMaxHp(100));
        Assert.Equal(-40, set.EffectiveStat(9, 10));   // content stats may go negative
        Assert.Equal(10, set.EffectiveStat(8, 10));    // a different content stat is untouched
    }

    [Fact]
    public void EffectiveSpeed_IsBitExactIdentityWithNoModifiers()
    {
        var set = new StatusSet();
        float odd = 5.123457f;
        Assert.Equal(BitConverter.SingleToInt32Bits(odd), BitConverter.SingleToInt32Bits(set.EffectiveSpeed(odd)));
        Assert.Equal(1000, set.SpeedMultiplierPermille);
    }

    [Fact]
    public void Slows_TakeTheStrongest_AndCombineWithSpeedModifiers()
    {
        var set = new StatusSet();
        set.Apply(Status(1, cc: CrowdControl.Slow, slow: 300), 0, null);
        set.Apply(Status(2, cc: CrowdControl.Slow, slow: 500), 0, null);
        Assert.Equal(500, set.SlowPermille);
        Assert.Equal(500, set.SpeedMultiplierPermille);
        Assert.Equal(2.5f, set.EffectiveSpeed(5f));

        set.Apply(Status(3, modifiers: new[] { StatModifier.For(StatModifierTarget.Speed, 0, 200) }), 0, null);
        // 1200 * (1000 - 500) / 1000 = 600
        Assert.Equal(600, set.SpeedMultiplierPermille);
        Assert.Equal(3f, set.EffectiveSpeed(5f));
        Assert.True(set.CanMove); // slow is not root
    }

    // ── Crowd control ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CrowdControl.None, true, true, true)]
    [InlineData(CrowdControl.Stun, false, false, false)]
    [InlineData(CrowdControl.Root, false, true, true)]
    [InlineData(CrowdControl.Silence, true, false, true)]
    [InlineData(CrowdControl.Slow, true, true, true)]
    [InlineData(CrowdControl.Root | CrowdControl.Silence, false, false, true)]
    public void CrowdControl_Queries(CrowdControl cc, bool canMove, bool canCast, bool canAct)
    {
        var set = new StatusSet();
        set.Apply(Status(1, cc: cc, slow: (cc & CrowdControl.Slow) != 0 ? 100 : 0), 0, null);

        Assert.Equal(cc, set.ActiveCrowdControl);
        Assert.Equal(canMove, set.CanMove);
        Assert.Equal(canCast, set.CanCast);
        Assert.Equal(canAct, set.CanAct);
    }

    [Fact]
    public void CrowdControl_EndsWhenTheStatusExpires()
    {
        var set = new StatusSet();
        set.Apply(Status(1, duration: 2, cc: CrowdControl.Stun), 10, null);
        Assert.False(set.CanAct);
        Tick(set, 11);
        Assert.False(set.CanAct);
        Tick(set, 12);
        Assert.True(set.CanAct);
    }

    // ── Snapshot export ──────────────────────────────────────────────────────────

    [Fact]
    public void CopyTo_WritesSnapshotEntries_InApplicationOrder()
    {
        var set = new StatusSet();
        set.Apply(Status(5, duration: 20, maxStacks: 3), 100, "x");
        set.Apply(Status(5, duration: 20, maxStacks: 3), 101, "x");
        set.Apply(Status(2, duration: 0), 100, null);

        var buf = new StatusEffectData[4];
        int n = set.CopyTo(buf);

        Assert.Equal(2, n);
        Assert.Equal(new StatusEffectData(5, 2, 121, "x"), buf[0]);
        Assert.Equal(new StatusEffectData(2, 1, 0, null), buf[1]);
        Assert.Equal(1, set.CopyTo(buf.AsSpan(0, 1)));
    }

    // ── Events ─────────────────────────────────────────────────────────────────

    [Fact]
    public void StatusEvents_CarryTheEffectId()
    {
        var applied = GameEventData.StatusApplied("src", "tgt", 7, stacks: 2);
        Assert.Equal(GameEventType.StatusApplied, applied.Type);
        Assert.Equal(7u, applied.EffectId);
        Assert.Equal(2, applied.Amount);

        var dot = GameEventData.PeriodicDamage("src", "tgt", 12, 7);
        Assert.Equal(GameEventType.Damage, dot.Type);
        Assert.Equal(GameEventFlags.Periodic, dot.Flags);
        Assert.Equal(7u, dot.EffectId);

        Assert.Equal(7u, GameEventData.StatusRemoved("tgt", 7).EffectId);
        Assert.Equal(0u, GameEventData.Damage("a", "b", 1).EffectId);
        Assert.Equal(9, (int)GameEventType.ProjectileHit);
    }
}
