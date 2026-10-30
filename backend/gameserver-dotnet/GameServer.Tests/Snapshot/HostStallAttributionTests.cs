using GameServer.Server;
using GameServer.Tests.Infrastructure;
using static GameServer.Tests.Snapshot.EntityIntervalUnderAdversityTests;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// The rule that decides whether an over-budget reading in
/// <see cref="EntityIntervalUnderAdversityTests"/> may be reported as inconclusive (#510).
/// The rule is what keeps that test honest, so it is pinned on its own: a reading is only
/// excused by a stall the sentinel actually recorded, and never otherwise.
/// </summary>
public class HostStallAttributionTests
{
    private static readonly double Budget = ReplicationSchedule.ClientInterpolationBudgetMs;

    private static Gap Over(double start, double extra = 100) => new(start, start + Budget + extra);

    [Fact]
    public void AQuietHostExplainsNothing()
    {
        // The case that must keep failing: a long gap and a sentinel that saw no stall.
        Assert.Equal(1, CountUnexplained([Over(1000)], []));
    }

    [Fact]
    public void AGapOnARecordedStallIsExplained()
    {
        var stalls = new[] { new HostStallSentinel.Stall(1100, 1400) };
        Assert.Equal(0, CountUnexplained([Over(1000)], stalls));
    }

    [Fact]
    public void AStallTooShortToExplainAGapOfThatSizeExplainsNothing()
    {
        var stalls = new[] { new HostStallSentinel.Stall(1100, 1130) }; // 30ms
        Assert.Equal(1, CountUnexplained([Over(1000)], stalls));
    }

    [Fact]
    public void AStallFarFromTheGapExplainsNothing()
    {
        var stalls = new[] { new HostStallSentinel.Stall(100, 400) };
        Assert.Equal(1, CountUnexplained([Over(5000)], stalls));
    }

    [Fact]
    public void ARecoveryGapThatFollowsAnExplainedGapIsExplained()
    {
        var stalls = new[] { new HostStallSentinel.Stall(1100, 1400) };
        var first = Over(1000);
        var second = new Gap(first.EndMs, first.EndMs + Budget + 50);
        Assert.Equal(0, CountUnexplained([second, first], stalls));
    }

    [Fact]
    public void AChainNeverStartsWithoutAStall()
    {
        var first = Over(1000);
        var second = new Gap(first.EndMs, first.EndMs + Budget + 50);
        Assert.Equal(2, CountUnexplained([first, second], []));
    }

    [Fact]
    public void OneUncoveredGapAmongCoveredOnesIsStillReported()
    {
        var stalls = new[] { new HostStallSentinel.Stall(1100, 1400) };
        Assert.Equal(1, CountUnexplained([Over(1000), Over(9000)], stalls));
    }

    [Fact]
    public void GapsWithinTheBudgetAreNotOffendersAtAll()
    {
        Assert.Equal(0, CountUnexplained([new Gap(0, Budget)], []));
    }
}
