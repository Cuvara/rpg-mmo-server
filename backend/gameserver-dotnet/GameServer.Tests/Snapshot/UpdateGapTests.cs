using GameServer.Server;
using GameServer.Snapshot;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// <see cref="SnapshotDeltaState.MaxUpdateGap"/>: the wait between sends of an entity owed an
/// update, whichever deferral source withheld it (#421).
///
/// <para>The gauge is checked against the thing it claims to report — the gap between
/// consecutive snapshots carrying an entity, read off the encoded messages — rather than
/// against the two single-source gauges it exists to replace. Agreeing with those would prove
/// nothing: neither of them is the number.</para>
/// </summary>
public class UpdateGapTests
{
    private const int LiveKeyframes = GameConstants.DefaultKeyframeInterval;
    private readonly ITestOutputHelper _out;

    public UpdateGapTests(ITestOutputHelper output) => _out = output;

    private static SnapshotDeltaState State(int budget, ReplicationSchedule schedule) => new()
    {
        MaxSnapshotBytes = budget,
        ImportanceWeights = ImportanceSettings.Balanced.Weights,
        Schedule = schedule,
        TickHz = SimulationRates.DefaultCriticalHz,
        AoiRadius = GameConstants.DefaultAoiRadius,
        SelfId = "self",
    };

    private static List<EntityState> Crowd(int players)
    {
        var l = new List<EntityState> { TestHelpers.CreatePlayer("self", 0f, 0f) };
        for (int i = 0; i < players; i++)
            l.Add(TestHelpers.CreatePlayer($"p{i:D3}", (i % 17) * 0.3f, (i % 13) * 0.3f));
        return l;
    }

    private static void MoveAll(List<EntityState> world)
    {
        for (int i = 0; i < world.Count; i++)
        {
            EntityState e = world[i];
            e.Position = new Vec2(e.Position.X + 0.02f, e.Position.Y);
            world[i] = e;
        }
    }

    /// <summary>
    /// Runs the encoder over a crowd that moves every tick — so every entity is owed an update
    /// on every tick — and returns the largest gap between two snapshots carrying the same
    /// entity, read from the messages themselves.
    /// </summary>
    /// <param name="stride">Base ticks between encodes. Snapshots go out on WORLD ticks while
    /// the tick counter is the BASE tick, so a stride of 1 is not the live shape — the first
    /// live run found a defect a stride-1 test could not.</param>
    private static int ObservedMaxGap(SnapshotDeltaState state, List<EntityState> world, ulong ticks, ulong stride = 1)
    {
        var handles = new Dictionary<uint, string>();
        var lastSeen = new Dictionary<string, ulong>();
        int maxGap = 0;
        for (ulong t = stride; t <= ticks; t += stride)
        {
            MoveAll(world);
            SnapshotMessage msg = state.Encode(t, t, world, LiveKeyframes, intern: true, observer: new Vec2(0f, 0f));
            if (msg.Full) handles.Clear();
            foreach (EntitySnapshot e in msg.Entities)
            {
                string? id = e.Id;
                if (!string.IsNullOrEmpty(id)) { if (e.Handle != 0) handles[e.Handle] = id; }
                else handles.TryGetValue(e.Handle, out id);
                Assert.False(string.IsNullOrEmpty(id), $"tick {t}: handle {e.Handle} has no binding");
                if (lastSeen.TryGetValue(id!, out ulong prev) && (int)(t - prev) > maxGap)
                    maxGap = (int)(t - prev);
                lastSeen[id!] = t;
            }
        }
        return maxGap;
    }

    /// <summary>
    /// Schedule and budget together — the case neither single-source gauge covers. The gauge
    /// must equal the observed worst gap exactly, and must exceed both single-source gauges,
    /// which is the #421 finding: each stops counting where the other starts.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]   // 60/30
    [InlineData(3UL)]   // 60/20
    // Not 4 (60/15): tiered is refused at the shipped rate, and there the schedule never
    // defers, so there is no combined case to measure.
    public void ScheduleAndBudgetTogether_GaugeEqualsTheObservedGap(ulong stride)
    {
        SnapshotDeltaState state = State(budget: 512, ReplicationSchedule.Tiered);
        int observed = ObservedMaxGap(state, Crowd(60), 240 * stride, stride);

        _out.WriteLine($"stride={stride} observed={observed} maxUpdateGap={state.MaxUpdateGap} " +
                       $"maxStateAge={state.MaxStateAge} maxShedAge={state.MaxShedAge} " +
                       $"shed={state.EntitiesShed} deferred={state.EntitiesDeferredByInterval}");

        Assert.True(state.EntitiesShed > 0, "the budget never bit, so this is not the combined case");
        Assert.True(state.EntitiesDeferredByInterval > 0, "the schedule never deferred, so this is not the combined case");
        Assert.Equal(observed, state.MaxUpdateGap);
        Assert.True(state.MaxUpdateGap > state.MaxStateAge && state.MaxUpdateGap > state.MaxShedAge,
            $"the combined gap {state.MaxUpdateGap} did not exceed both single-source gauges " +
            $"(state {state.MaxStateAge}, shed {state.MaxShedAge})");
    }

    /// <summary>Each source alone: the gauge still equals what the messages show.</summary>
    [Theory]
    [InlineData(0, "tiered", 1UL)]
    [InlineData(0, "tiered", 2UL)]   // 60/30: two world ticks inside the 105ms ceiling
    [InlineData(512, "off", 1UL)]
    [InlineData(512, "off", 4UL)]
    public void EachSourceAlone_GaugeEqualsTheObservedGap(int budget, string profile, ulong stride)
    {
        var schedule = profile == "tiered" ? ReplicationSchedule.Tiered : ReplicationSchedule.Off;
        SnapshotDeltaState state = State(budget, schedule);
        int observed = ObservedMaxGap(state, Crowd(60), 240 * stride, stride);

        _out.WriteLine($"[{profile} budget={budget} stride={stride}] observed={observed} maxUpdateGap={state.MaxUpdateGap} " +
                       $"maxStateAge={state.MaxStateAge} maxShedAge={state.MaxShedAge}");

        Assert.True(observed > (int)stride, $"nothing was ever withheld (observed gap {observed}, stride {stride})");
        Assert.Equal(observed, state.MaxUpdateGap);
    }

    /// <summary>
    /// Nothing withheld, nothing reported: with neither source active every moving entity is
    /// sent every tick, and a gauge that read above zero here would be counting something else.
    /// </summary>
    [Fact]
    public void NeitherSource_ReportsZero()
    {
        SnapshotDeltaState state = State(budget: 0, ReplicationSchedule.Off);
        int observed = ObservedMaxGap(state, Crowd(60), 120);

        Assert.Equal(1, observed);
        Assert.Equal(0, state.MaxUpdateGap);
    }

    /// <summary>
    /// An entity that stood still was not stale while it stood still — the client's copy was
    /// correct. When it starts moving it is due at once (its last send is long past), so it
    /// waits for nothing and the gauge must not charge it the idle time.
    /// </summary>
    [Fact]
    public void IdleTimeIsNotAWait()
    {
        SnapshotDeltaState state = State(budget: 0, ReplicationSchedule.Tiered);
        var world = new List<EntityState>
        {
            TestHelpers.CreatePlayer("self", 0f, 0f),
            TestHelpers.CreatePlayer("idler", 1f, 1f),
        };

        for (ulong t = 1; t <= 200; t++)
        {
            if (t > 150)
            {
                EntityState e = world[1];
                e.Position = new Vec2(e.Position.X + 0.02f, e.Position.Y);
                world[1] = e;
            }
            state.Encode(t, t, world, int.MaxValue, intern: true, observer: new Vec2(0f, 0f));
        }

        int interval = ReplicationSchedule.Tiered.IntervalTicksFor(0f, SimulationRates.DefaultCriticalHz);
        _out.WriteLine($"maxUpdateGap={state.MaxUpdateGap} scheduleInterval={interval}");
        Assert.True(state.MaxUpdateGap <= interval,
            $"a 150-tick idle spell was charged as a wait: gauge {state.MaxUpdateGap}, " +
            $"schedule interval {interval}");
        Assert.True(state.OwedRecords <= 1, $"owed records leaked: {state.OwedRecords}");
    }
}
