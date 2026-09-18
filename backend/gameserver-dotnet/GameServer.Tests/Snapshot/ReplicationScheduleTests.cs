using GameServer.Server;
using GameServer.Snapshot;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace GameServer.Tests.Snapshot;

public class ReplicationScheduleTests
{
    private const int TickHz = SimulationRates.DefaultCriticalHz;   // 60, the BASE rate
    private const int WorldEvery = TickHz / SimulationRates.DefaultWorldHz;   // 4
    private const int NoKeyframes = int.MaxValue;

    private static SnapshotDeltaState Tiered() => new()
    {
        ImportanceWeights = ImportanceSettings.Balanced.Weights,
        Schedule = ReplicationSchedule.Tiered,
        TickHz = TickHz,
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
    [InlineData(266, 15, 4)]    // 266ms at 15Hz -> 4 ticks == 267ms
    [InlineData(266, 30, 8)]    // same wall time at double the rate
    [InlineData(133, 15, 2)]    // 133ms -> 2 ticks; flooring made this 1 and deleted the tier
    [InlineData(133, 30, 4)]
    [InlineData(500, 15, 7)]    // the ceiling binds on ACTUAL wait, not on the typed value
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
    public void Keyframe_CarriesEveryVisibleEntity_WhateverTheirTier()
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
    public void AnEdgeIsNeverDeferred()
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
    public void ScheduleStarvation_IsBoundedByTheConfiguredInterval()
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

        // The slowest tier is 266ms; at 15Hz that is 3 world ticks. Headroom for the budget
        // sort interleaving, but bounded by the CONFIGURED interval rather than by luck.
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
    public void AfterADeferral_TheNextSendCarriesTheLatestState()
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
