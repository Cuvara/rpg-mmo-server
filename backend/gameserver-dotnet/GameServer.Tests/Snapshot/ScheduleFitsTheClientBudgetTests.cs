using GameServer.Server;
using Xunit.Abstractions;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// No band may schedule an entity to arrive after the client has run out of state to
/// interpolate towards.
/// </summary>
/// <remarks>
/// <para>The client holds <c>TargetDelay</c> 100ms plus <c>MaxExtrapolation</c> 50ms. Past
/// 150ms it is not looking at slightly old state, it has nothing to look at: the entity
/// holds its last position and then jumps. The shipped schedule's slowest band was 266ms
/// and its ceiling was 500ms, while the remark on that ceiling derived 150 and wrote 500.
/// Three people playing saw mobs step while players moved smoothly.</para>
///
/// <para>This test is a guard on a number that lives in the other repository, which is
/// exactly why it is worth asserting rather than trusting: nothing in this build fails when
/// a band is widened past what the client can absorb, and nothing in the client's build
/// fails when its buffer is narrowed. A failure here means one of the two moved — reconcile
/// them, do not relax the bound.</para>
/// </remarks>
public class ScheduleFitsTheClientBudgetTests
{
    private readonly ITestOutputHelper _out;

    public ScheduleFitsTheClientBudgetTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void NoTierIsSlowerThanTheClientCanInterpolateThrough()
    {
        Assert.Equal(150, ReplicationSchedule.ClientInterpolationBudgetMs);
        Assert.True(ReplicationSchedule.MaxIntervalMs <= ReplicationSchedule.ClientInterpolationBudgetMs,
            $"the interval ceiling is {ReplicationSchedule.MaxIntervalMs}ms, above the " +
            $"{ReplicationSchedule.ClientInterpolationBudgetMs}ms the client can cover");

        foreach (ReplicationSchedule.Tier t in ReplicationSchedule.Tiered.Tiers)
        {
            _out.WriteLine($"tier minScore={t.MinScore} intervalMs={t.IntervalMs}");
            Assert.True(t.IntervalMs <= ReplicationSchedule.ClientInterpolationBudgetMs,
                $"a band waits {t.IntervalMs}ms, which is {t.IntervalMs - ReplicationSchedule.ClientInterpolationBudgetMs}ms " +
                "past the point the client has anything left to interpolate towards");
        }
    }

    [Fact]
    public void AtTheShippedWorldRate_EveryBandIsStillDistinct()
    {
        // Bands that round to the same tick count are one band wearing two names — the
        // failure ADR-27 decision 4 records, where 133ms floored to "every tick" and went on
        // printing in the banner. Two bands that BOTH say 133ms would read as a three-speed
        // policy in the logs while behaving as a two-speed one.
        const int worldHz = 15;
        var ticks = ReplicationSchedule.Tiered.Tiers
            .Select(t => ReplicationSchedule.Tiered.IntervalTicksFor(t.MinScore, worldHz * 4))
            .ToList();

        _out.WriteLine("ticks per band: " + string.Join(", ", ticks));
        Assert.Equal(ticks.Count, ticks.Distinct().Count());
    }
}
