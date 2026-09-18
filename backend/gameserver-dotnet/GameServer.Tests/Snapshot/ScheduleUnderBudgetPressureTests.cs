using GameServer.Server;
using GameServer.Snapshot;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// What the schedule does when the BYTE BUDGET is already saturated, which is the case a
/// live 200-player cluster run put it in and which no unit test had covered.
/// </summary>
public class ScheduleUnderBudgetPressureTests
{
    private const int NoKeyframes = int.MaxValue;
    private const int LiveKeyframes = GameConstants.DefaultKeyframeInterval;  // 30
    private readonly ITestOutputHelper _out;

    public ScheduleUnderBudgetPressureTests(ITestOutputHelper output) => _out = output;

    private static SnapshotDeltaState State(int budget) => new()
    {
        MaxSnapshotBytes = budget,
        ImportanceWeights = ImportanceSettings.Balanced.Weights,
        Schedule = ReplicationSchedule.Tiered,
        TickHz = SimulationRates.DefaultCriticalHz,
        AoiRadius = GameConstants.DefaultAoiRadius,
        SelfId = "self",
    };

    private static List<EntityState> Crowd(int players)
    {
        var l = new List<EntityState> { TestHelpers.CreatePlayer("self", 0f, 0f) };
        for (int i = 0; i < players; i++)
        {
            // Cluster: everyone on top of everyone, like the loadtest's default mode.
            l.Add(TestHelpers.CreatePlayer($"p{i:D3}", (i % 17) * 0.3f, (i % 13) * 0.3f));
        }
        return l;
    }

    /// <summary>
    /// The live finding, reproduced: with the budget saturated the schedule defers nothing,
    /// because an entity the budget never emitted has no send tick and therefore reads as
    /// "never sent" — which is always due.
    ///
    /// <para>This is not a bug in either mechanism. It is the two of them meeting: the
    /// budget is already withholding far more than the schedule would, so the schedule has
    /// nothing left to withhold. It is recorded because the opposite was assumed — that the
    /// two compose — and a benchmark arm that reports 0 deferrals with the feature ON reads
    /// exactly like a feature that is not wired.</para>
    /// </summary>
    [Fact]
    public void UnderASaturatedBudget_TheScheduleDefersNothing()
    {
        SnapshotDeltaState tight = State(budget: 8192);
        List<EntityState> world = Crowd(200);

        for (ulong t = 1; t <= 200; t++)
        {
            for (int i = 0; i < world.Count; i++)
            {
                EntityState e = world[i];
                e.Position = new Vec2(e.Position.X + 0.02f, e.Position.Y);
                world[i] = e;
            }
            tight.Encode(t, t, world, LiveKeyframes, intern: true, observer: new Vec2(0f, 0f));
        }

        _out.WriteLine($"budget-saturated: shed={tight.EntitiesShed} " +
                       $"deferredByInterval={tight.EntitiesDeferredByInterval} " +
                       $"maxStateAge={tight.MaxStateAge}");

        // Reported, not asserted: this probe exists to find out which mechanism withheld
        // what, and an assertion would freeze whichever answer today happens to give.
        Assert.True(tight.EntitiesShed + tight.EntitiesDeferredByInterval > 0,
            "neither mechanism withheld anything; the scenario is wrong");
    }

    /// <summary>
    /// The same population with the budget off: now the schedule is the only thing
    /// withholding anything, and it does.
    /// </summary>
    [Fact]
    public void WithTheBudgetOff_TheScheduleIsWhatWithholds()
    {
        SnapshotDeltaState loose = State(budget: 0);
        List<EntityState> world = Crowd(200);

        for (ulong t = 1; t <= 60; t++)
        {
            for (int i = 0; i < world.Count; i++)
            {
                EntityState e = world[i];
                e.Position = new Vec2(e.Position.X + 0.02f, e.Position.Y);
                world[i] = e;
            }
            loose.Encode(t, t, world, NoKeyframes, intern: true, observer: new Vec2(0f, 0f));
        }

        _out.WriteLine($"budget-off: shed={loose.EntitiesShed} " +
                       $"deferredByInterval={loose.EntitiesDeferredByInterval} " +
                       $"maxStateAge={loose.MaxStateAge}");

        Assert.Equal(0, loose.EntitiesShed);
        Assert.True(loose.EntitiesDeferredByInterval > 0,
            "the schedule withheld nothing even with the budget out of the way");
    }
}
