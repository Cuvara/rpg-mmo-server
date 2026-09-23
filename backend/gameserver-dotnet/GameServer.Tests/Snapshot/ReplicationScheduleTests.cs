using GameServer.Server;
using GameServer.Snapshot;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace GameServer.Tests.Snapshot;

public class ReplicationScheduleTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public ReplicationScheduleTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const int TickHz = SimulationRates.DefaultCriticalHz;   // 60, the BASE rate
    // 60/30, not the shipped 60/15, and that is deliberate (#413).
    //
    // The interval ceiling now reserves ReplicationSchedule.LinkSpreadAllowanceMs of the
    // client's cover for the network, which leaves 105ms for deferral. Emission happens on
    // world ticks, so at 60/15 the only waits that exist are 66.7ms and 133.3ms: 105 sits
    // between them and every band collapses to "every tick". The schedule defers NOTHING at
    // the shipped rate, which is asserted on purpose by
    // ScheduleFitsTheClientBudgetTests.TieringBuysNothingAtTheShippedWorldRate_AndNeedsAFasterOne.
    //
    // These cases are about the schedule's MECHANICS — aging, self-exemption, edges, keyframe
    // coverage, convergence after a deferral — which are rate-independent. Running them where
    // the feature is inert would leave every one of them passing vacuously; their own guards
    // ("nothing was deferred, so this proves nothing") caught exactly that when the ceiling
    // changed, which is why they are re-homed rather than relaxed.
    private const int WorldEvery = 2;   // 60/30
    private const int NoKeyframes = int.MaxValue;

    private static SnapshotDeltaState Tiered() => new()
    {
        ImportanceWeights = ImportanceSettings.Balanced.Weights,
        Schedule = ReplicationSchedule.Tiered,
        TickHz = TickHz,
        WorldEvery = WorldEvery,
        AoiRadius = GameConstants.DefaultAoiRadius,
    };

    // ── Milliseconds, never ticks ────────────────────────────────────────────────

    /// <summary>
    /// The same configured interval must mean the same WALL TIME at any world rate. A tick
    /// count only means something next to the rate that advances it, so a deployment that
    /// raised SIM_WORLD_HZ for smoother movement would otherwise have halved the staleness
    /// it configured without editing it.
    /// </summary>
    [Theory]
    // ROUNDING, where the ceiling does not reach. This is the lesson the 133/15 row used to
    // carry: 99ms at 30Hz is 2.97 ticks, so nearest gives 3 and flooring gives 2 — and a
    // band that floors to the tick below is the tier that silently stopped existing while
    // still printing in the banner (ADR-27 decision 4).
    [InlineData(99, 30, 3)]
    [InlineData(66, 15, 1)]
    // THE CEILING, which binds on the actual wait rather than on the typed value. These rows
    // have now been rewritten twice and are kept rather than deleted, because what changed
    // both times is what the numbers MEAN and a deleted row is a rule nobody can see was
    // ever tested:
    //   originally  500ms ceiling  ->  133/15 = 2,  133/30 = 4,  266/15 = 4,  266/30 = 8
    //   then #?     150ms ceiling  ->  133/15 = 2,  133/30 = 4,  266/15 = 2,  266/30 = 4
    //   now  #413   105ms ceiling  ->  133/15 = 1,  133/30 = 3,  266/15 = 1,  266/30 = 3
    // The third line is the link allowance arriving: 105ms at 15Hz is ONE world tick, so
    // every interval anyone can type collapses to "every tick" at the shipped rate.
    [InlineData(133, 15, 1)]
    [InlineData(133, 30, 3)]
    [InlineData(266, 15, 1)]
    [InlineData(266, 30, 3)]
    [InlineData(500, 15, 1)]
    public void IntervalsAreConfiguredInMillisecondsAndConvertWithTheWorldRate(
        int ms, int worldHz, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ReplicationSchedule.TicksFor(ms, worldHz));
    }

    /// <summary>
    /// Every interval is finite and capped. An entity that is dirty but never due would
    /// never enter the candidate list, never age, and never be promoted by the aging
    /// order — starved by a mechanism the existing starvation test cannot see, because a
    /// not-due entity is not a shed entity.
    /// </summary>
    [Fact]
    public void NoIntervalCanExceedTheCeiling()
    {
        int max = ReplicationSchedule.TicksFor(int.MaxValue, TickHz);
        Assert.Equal(ReplicationSchedule.TicksFor(ReplicationSchedule.MaxIntervalMs, TickHz), max);

        foreach (ReplicationSchedule.Tier t in ReplicationSchedule.Tiered.Tiers)
        {
            Assert.True(t.IntervalMs <= ReplicationSchedule.MaxIntervalMs,
                $"tier at score {t.MinScore} asks for {t.IntervalMs}ms");
            Assert.True(ReplicationSchedule.Tiered.IntervalTicksFor(t.MinScore, TickHz) >= 1);
        }
    }

    /// <summary>
    /// The server refuses to start with a schedule whose bands all collapse to one wait at
    /// the configured rates (#413).
    ///
    /// <para>At 60/15 with a 105ms ceiling the only wait that exists is one world tick, so
    /// <c>tiered</c> would parse, print two bands in the banner, and behave as one. This file
    /// already records that exact failure twice — a tier flooring to "every tick" while still
    /// appearing in the banner and in <c>/status</c>, and a 266ms band whose only effect was
    /// arriving after the client could use it. Both were found by reading a running server.
    /// A comment did not stop the second, so it is a boot failure now.</para>
    /// </summary>
    [Fact]
    public void TieredIsRefused_WhenTheWorldRateCollapsesEveryBandIntoOne()
    {
        Assert.False(ReplicationSchedule.TryCreate(
            "tiered", importanceEnabled: true,
            criticalHz: 60, worldHz: SimulationRates.DefaultWorldHz,
            out ReplicationSchedule? schedule, out string? error));
        Assert.Null(schedule);
        Assert.NotNull(error);
        _out.WriteLine(error);

        // The message has to carry the numbers, because "no usable band" alone sends the
        // reader to look for a band that is missing rather than at the rate that removed it.
        Assert.Contains("60/15", error);
        Assert.Contains($"{ReplicationSchedule.MaxIntervalMs}ms", error);
        Assert.Contains("SIM_WORLD_HZ", error);

        // The rate the message RECOMMENDS must actually work. Asserting that the message
        // names some number proves nothing; asserting the advice is actionable is the point,
        // and it is the assertion that would catch a search returning a rate that does not
        // divide the base rate or does not in fact separate the bands.
        var recommended = System.Text.RegularExpressions.Regex.Match(
            error!, @"Raise SIM_WORLD_HZ to (\d+)");
        Assert.True(recommended.Success, $"the message gave no rate to raise to: {error}");
        int rate = int.Parse(recommended.Groups[1].Value);

        Assert.True(ReplicationSchedule.TryCreate(
            "tiered", importanceEnabled: true, criticalHz: 60, worldHz: rate,
            out ReplicationSchedule? atRecommended, out string? stillWrong), stillWrong);
        Assert.NotNull(atRecommended);
        Assert.False(atRecommended!.BandsCollapseAt(60, rate),
            $"the message recommends SIM_WORLD_HZ={rate}, at which the bands still collapse");
    }

    /// <summary>
    /// And it is accepted at a rate where the bands are real, so the refusal above is a
    /// statement about the rate rather than about the profile.
    /// </summary>
    [Fact]
    public void TieredIsAccepted_AtAWorldRateThatSeparatesTheBands()
    {
        Assert.True(ReplicationSchedule.TryCreate(
            "tiered", importanceEnabled: true, criticalHz: 60, worldHz: 30,
            out ReplicationSchedule? schedule, out string? error), error);
        Assert.NotNull(schedule);

        int[] effective = schedule!.EffectiveIntervalsMs(60, 30);
        Assert.Equal(effective.Length, effective.Distinct().Count());
    }

    /// <summary>
    /// Unusable rates are the rate validator's business, not this gate's. A zero or
    /// non-dividing rate must not produce a confusing message about bands.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(60, 0)]
    [InlineData(60, 7)]     // does not divide
    public void TheCollapseGate_IsSkippedForRatesTheRateValidatorWillReject(int criticalHz, int worldHz)
    {
        Assert.True(ReplicationSchedule.TryCreate(
            "tiered", importanceEnabled: true, criticalHz, worldHz,
            out ReplicationSchedule? schedule, out string? error), error);
        Assert.NotNull(schedule);
    }

    /// <summary>Tiering without importance weights would defer everything equally — a
    /// uniform staleness increase wearing the name of a policy.</summary>
    [Fact]
    public void TieredWithoutImportanceWeights_IsRefused()
    {
        Assert.False(ReplicationSchedule.TryCreate("tiered", importanceEnabled: false,
            out ReplicationSchedule? s, out string? err));
        Assert.Null(s);
        Assert.Contains("legacy", err);
    }

    [Fact]
    public void OffIsTheDefaultAndDefersNothing()
    {
        Assert.True(ReplicationSchedule.TryCreate(null, importanceEnabled: true,
            out ReplicationSchedule? s, out _));
        Assert.False(s!.Enabled);
        Assert.Equal(1, s.IntervalTicksFor(0f, TickHz));
        Assert.Equal(1, s.IntervalTicksFor(100f, TickHz));
    }

    // ── The mandatory behavioural rules ──────────────────────────────────────────

    /// <summary>
    /// An entity this connection has never been sent bypasses the schedule. Its "last sent"
    /// is never; a client that receives a handle it has no binding for must ask for a
    /// keyframe, which costs far more than the update withheld.
    /// </summary>
    [Fact]
    public void NewlyVisibleEntity_IsSentImmediately()
    {
        SnapshotDeltaState state = Tiered();
        var world = new List<EntityState> { TestHelpers.CreatePlayer("p", 0f, 0f) };
        state.Encode(Base(1), Base(1), world, NoKeyframes, intern: true, observer: new Vec2(0, 0));

        // A distant mob appears on tick 2: lowest possible tier, but never sent before.
        world.Add(TestHelpers.CreateMob("newcomer", 45f, 0f));
        SnapshotMessage msg = state.Encode(Base(2), Base(2), world, NoKeyframes, intern: true,
            observer: new Vec2(0, 0));

        Assert.Contains(msg.Entities, e => e.Id == "newcomer");
        Assert.Equal(0, state.EntitiesDeferredByInterval);
    }

    /// <summary>
    /// A keyframe is the complete visible set and the client discards anything it does not
    /// list. Applying intervals there would not make an entity late — it would make it
    /// vanish until some later delta happened to carry it.
    /// </summary>
    [Fact]
    public void At60Over30_KeyframeCarriesEveryVisibleEntityWhateverTheirTier()
    {
        SnapshotDeltaState state = Tiered();
        var world = new List<EntityState> { TestHelpers.CreatePlayer("self", 0f, 0f) };
        for (int i = 0; i < 20; i++) world.Add(TestHelpers.CreateMob($"m{i}", 40f + (i * 0.1f), 0f));

        // Run a while under a schedule, so plenty of entities are mid-interval.
        for (ulong t = 1; t <= 10; t++)
        {
            Drift(world, t);
            state.Encode(Base(t), Base(t), world, NoKeyframes, intern: true, observer: new Vec2(0, 0));
        }
        Assert.True(state.EntitiesDeferredByInterval > 0, "nothing was deferred, so this proves nothing");

        state.RequestFull();
        SnapshotMessage key = state.Encode(Base(11), Base(11), world, NoKeyframes, intern: true,
            observer: new Vec2(0, 0));

        Assert.True(key.Full);
        Assert.Equal(world.Count, key.Entities.Count);
    }

    /// <summary>
    /// Health and the action retrigger counter are occurrences, not states: withholding one
    /// is a dropped event, because the next snapshot carries only the state afterwards.
    /// </summary>
    [Fact]
    public void At60Over30_AnEdgeIsNeverDeferred()
    {
        SnapshotDeltaState state = Tiered();
        var mob = TestHelpers.CreateMob("mob", 45f, 0f);
        var world = new List<EntityState> { mob };

        state.Encode(Base(1), Base(1), world, NoKeyframes, intern: true, observer: new Vec2(0, 0));

        // Tick 2: only position moved -> bottom tier, not due.
        mob.Position = new Vec2(45.1f, 0f);
        world[0] = mob;
        SnapshotMessage m2 = state.Encode(Base(2), Base(2), world, NoKeyframes, intern: true, observer: new Vec2(0, 0));
        Assert.Empty(m2.Entities);

        // Tick 3: it takes a hit. HP is an edge and must go out immediately.
        mob.Hp -= 9;
        world[0] = mob;
        SnapshotMessage m3 = state.Encode(Base(3), Base(3), world, NoKeyframes, intern: true, observer: new Vec2(0, 0));
        Assert.Single(m3.Entities);

        // Tick 4: it swings. The action is an edge too.
        mob.Position = new Vec2(45.2f, 0f);
        mob.Action = SimAction.Attacking;
        world[0] = mob;
        SnapshotMessage m4 = state.Encode(Base(4), Base(4), world, NoKeyframes, intern: true, observer: new Vec2(0, 0));
        Assert.Single(m4.Entities);
    }

    /// <summary>
    /// The starvation rule the existing budget test cannot see. A not-due entity is not a
    /// shed entity, so <c>_shedAge</c> stays empty and <c>max_shed_age</c> reads a healthy
    /// zero while the entity goes stale — which is why this asserts on the age of what the
    /// client actually holds instead.
    /// </summary>
    [Fact]
    public void At60Over30_ScheduleStarvationIsBoundedByTheConfiguredInterval()
    {
        SnapshotDeltaState state = Tiered();
        var world = new List<EntityState> { TestHelpers.CreatePlayer("self", 0f, 0f) };
        for (int i = 0; i < 30; i++) world.Add(TestHelpers.CreateMob($"m{i}", 44f + (i * 0.05f), 0f));

        var lastCarried = new Dictionary<string, ulong>();
        ulong worstGap = 0;

        for (ulong t = 1; t <= 300; t++)
        {
            Drift(world, t);
            SnapshotMessage msg = state.Encode(Base(t), Base(t), world, NoKeyframes, intern: true,
                observer: new Vec2(0, 0));

            foreach (EntitySnapshot e in msg.Entities)
            {
                if (string.IsNullOrEmpty(e.Id)) continue;
                if (lastCarried.TryGetValue(e.Id, out ulong prev) && t - prev > worstGap)
                    worstGap = t - prev;
                lastCarried[e.Id] = t;
            }
        }

        Assert.True(state.EntitiesDeferredByInterval > 0, "nothing was deferred, so this proves nothing");
        Assert.Equal(world.Count, lastCarried.Count);

        // Bounded by the CONFIGURED slowest tier rather than by luck, whatever that tier is
        // — it is read off the schedule here rather than written as a number, so the bound
        // followed the ceiling from 266ms to 150ms to #413's 105ms without this assertion
        // having to be re-derived each time.
        int slowest = ReplicationSchedule.Tiered.IntervalTicksFor(0f, TickHz);
        Assert.True(worstGap <= (ulong)slowest + 2,
            $"worst gap {worstGap} ticks against a configured slowest tier of {slowest}");
        Assert.True(state.MaxStateAge <= slowest + 2,
            $"MaxStateAge {state.MaxStateAge} exceeded the configured slowest tier {slowest}");
    }

    /// <summary>
    /// The convergence rule: a deferred entity's next send carries its CURRENT state, never
    /// a queued intermediate one.
    /// </summary>
    [Fact]
    public void At60Over30_AfterADeferralTheNextSendCarriesTheLatestState()
    {
        SnapshotDeltaState state = Tiered();
        var mob = TestHelpers.CreateMob("mob", 45f, 0f);
        var world = new List<EntityState> { mob };
        state.Encode(Base(1), Base(1), world, NoKeyframes, intern: true, observer: new Vec2(0, 0));

        // Move it every tick. Intermediate positions are skipped, never queued.
        //
        // The X is captured at the moment it is carried rather than by holding on to the
        // SnapshotMessage: that object is POOLED and reused by the next encode, so a
        // reference kept across the loop describes whatever the last tick produced.
        float carriedX = float.NaN;
        ulong carriedAt = 0;
        for (ulong t = 2; t <= 8; t++)
        {
            mob.Position = new Vec2(45f + (t * 0.5f), 0f);
            world[0] = mob;
            SnapshotMessage m = state.Encode(Base(t), Base(t), world, NoKeyframes, intern: true,
                observer: new Vec2(0, 0));
            if (m.Entities.Count > 0 && carriedAt == 0)
            {
                carriedAt = t;
                carriedX = m.Entities[0].X;
            }
        }

        Assert.True(carriedAt > 2, "the entity was never deferred, so convergence was not tested");
        Assert.Equal(45f + (carriedAt * 0.5f), carriedX, 3);
    }

    /// <summary>The per-entity send-tick bookkeeping must not grow for entities that left.</summary>
    [Fact]
    public void SendTickRecords_DoNotLeakWhenEntitiesLeaveTheAoi()
    {
        SnapshotDeltaState state = Tiered();
        var world = new List<EntityState> { TestHelpers.CreatePlayer("self", 0f, 0f) };
        for (int i = 0; i < 40; i++) world.Add(TestHelpers.CreateMob($"m{i}", 10f + (i * 0.2f), 0f));

        for (ulong t = 1; t <= 20; t++)
        {
            Drift(world, t);
            state.Encode(Base(t), Base(t), world, NoKeyframes, intern: true, observer: new Vec2(0, 0));
        }

        // Everything but self leaves.
        var alone = new List<EntityState> { world[0] };
        for (ulong t = 21; t <= 40; t++)
        {
            state.Encode(Base(t), Base(t), alone, NoKeyframes, intern: true, observer: new Vec2(0, 0));
        }

        Assert.True(state.DeferralRecords <= 1,
            $"{state.DeferralRecords} deferral records survived a full AOI turnover");
    }


    /// <summary>
    /// The BASE tick that snapshot number <paramref name="snapshot"/> is built on.
    /// </summary>
    /// <remarks>
    /// Snapshots are built on world ticks, but the encoder is handed
    /// <c>TickLoop.CurrentTick</c>, which advances at the CRITICAL rate -- four times faster
    /// at the 60/15 default. Feeding these tests a counter that advanced by 1 per snapshot
    /// is precisely why they all passed against a server whose schedule deferred nothing,
    /// so the conversion is done here, once, in the same direction production does it.
    /// </remarks>
    private static ulong Base(ulong snapshot) => snapshot * (ulong)WorldEvery;

    private static void Drift(List<EntityState> world, ulong t)
    {
        for (int i = 0; i < world.Count; i++)
        {
            EntityState e = world[i];
            e.Position = new Vec2(e.Position.X + 0.03f, e.Position.Y + (i % 3) * 0.01f);
            world[i] = e;
        }
    }
}
