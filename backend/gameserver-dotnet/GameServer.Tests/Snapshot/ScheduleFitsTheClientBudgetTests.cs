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

        // The budget is shared with the WIRE, and this is the assertion #413 added.
        //
        // The old bound was `MaxIntervalMs <= ClientInterpolationBudgetMs`, which passed
        // while the collision was live: it let the scheduler spend the client's entire cover
        // on deferral and assumed the network cost nothing. Measured over a relay, the
        // shipped `tiered` profile reached a per-entity p99 of 148-150ms on LOOPBACK — the
        // budget exhausted with no network at all — and 176-178ms at +/-25ms one-way jitter.
        Assert.True(
            ReplicationSchedule.MaxIntervalMs
                <= ReplicationSchedule.ClientInterpolationBudgetMs - ReplicationSchedule.LinkSpreadAllowanceMs,
            $"the interval ceiling is {ReplicationSchedule.MaxIntervalMs}ms against a " +
            $"{ReplicationSchedule.ClientInterpolationBudgetMs}ms client budget with " +
            $"{ReplicationSchedule.LinkSpreadAllowanceMs}ms reserved for the link. The scheduler " +
            "is spending cover the wire has already spent, which is #413: the same mobs step " +
            "for the same reason, by a different route.");

        foreach (ReplicationSchedule.Tier t in ReplicationSchedule.Tiered.Tiers)
        {
            _out.WriteLine($"tier minScore={t.MinScore} intervalMs={t.IntervalMs}");

            // Against the CEILING, not against the raw budget. A band is only allowed to
            // declare a wait it is actually allowed to take — otherwise it is a band wearing
            // a name it does not honour, which is the failure the rounding lesson records.
            Assert.True(t.IntervalMs <= ReplicationSchedule.MaxIntervalMs,
                $"a band declares {t.IntervalMs}ms against a {ReplicationSchedule.MaxIntervalMs}ms " +
                $"ceiling, so it will be clamped and will not wait what it says it waits");
        }
    }

    /// <summary>
    /// The consequence of reserving budget for the link, stated here rather than left to be
    /// discovered in a banner: <b>at the shipped world rate, tiering buys nothing.</b>
    ///
    /// <para>105ms of ceiling at 15Hz is one world tick, so every band collapses to "every
    /// tick" and the policy is a no-op. That is not a defect to fix by widening the ceiling
    /// — widening it is #413 — it is the measurement saying a 15Hz world rate has no room
    /// for deferral once the wire is paid for. At 30Hz there is room, and the bands separate
    /// again.</para>
    ///
    /// <para>This is asserted because the alternative is the exact failure ADR-27 decision 4
    /// records: a tier that silently stopped existing while <c>replication_schedule</c> went
    /// on printing it. A no-op nobody can see is worse than one nobody wants.</para>
    /// </summary>
    [Fact]
    public void TieringBuysNothingAtTheShippedWorldRate_AndNeedsAFasterOne()
    {
        var shipped = ReplicationSchedule.Tiered.Tiers
            .Select(t => ReplicationSchedule.Tiered.IntervalTicksFor(t.MinScore, SimulationRates.DefaultWorldHz))
            .ToList();
        _out.WriteLine($"ticks per band at the shipped {SimulationRates.DefaultWorldHz}Hz world rate: "
            + string.Join(", ", shipped));

        Assert.All(shipped, t => Assert.Equal(1, t));

        const int fasterHz = 30;
        var faster = ReplicationSchedule.Tiered.Tiers
            .Select(t => ReplicationSchedule.Tiered.IntervalTicksFor(t.MinScore, fasterHz))
            .ToList();
        _out.WriteLine($"ticks per band at {fasterHz}Hz: " + string.Join(", ", faster));

        // And the complement: if THIS stops holding, tiering has no usable world rate at all
        // and the profile should be removed rather than left configurable.
        Assert.Equal(faster.Count, faster.Distinct().Count());
    }

    [Fact]
    public void AtAWorldRateFastEnoughForThem_EveryBandIsStillDistinct()
    {
        // Bands that round to the same tick count are one band wearing two names — the
        // failure ADR-27 decision 4 records, where 133ms floored to "every tick" and went on
        // printing in the banner. Two bands that BOTH say 133ms would read as a three-speed
        // policy in the logs while behaving as a two-speed one.
        // 60Hz, and the `worldHz * 4` that used to compute it was a trap: the constant was
        // named `worldHz` and set to 15, then multiplied, so a test called
        // "AtTheShippedWorldRate" measured a rate four times the shipped one. It is spelled
        // out now, and the shipped rate has its own case in
        // TieringBuysNothingAtTheShippedWorldRate_AndNeedsAFasterOne — where the bands do
        // NOT stay distinct, which is the thing this test would have claimed to cover.
        const int worldHz = 60;
        var ticks = ReplicationSchedule.Tiered.Tiers
            .Select(t => ReplicationSchedule.Tiered.IntervalTicksFor(t.MinScore, worldHz))
            .ToList();

        _out.WriteLine("ticks per band: " + string.Join(", ", ticks));
        Assert.Equal(ticks.Count, ticks.Distinct().Count());
    }
}
