using GameServer.Observability;
using Xunit;

namespace GameServer.Tests.Observability;

/// <summary>
/// The frame arrival-order probe behind ADR-22's "does the nonce-as-sequence rule need a
/// sliding window" question (<c>backend/docs/BENCHMARK.md</c> Part XII).
/// </summary>
/// <remarks>
/// The probe reads zero in production, which is exactly why it needs tests: a counter whose
/// correct value is zero is indistinguishable from a counter that does not work. These pin
/// that it would actually fire.
/// </remarks>
public class FrameOrderProbeTests
{
    [Fact]
    public void MonotonicFramesProduceNoInversions()
    {
        var probe = new FrameOrderProbe();
        ulong cursor = 0;

        for (ulong t = 1; t <= 100; t++)
            Assert.False(probe.Observe(ref cursor, t));

        Assert.Equal(100, probe.FramesObserved);
        Assert.Equal(0, probe.Inversions);
        Assert.Equal(0, probe.Duplicates);
        Assert.Equal(0, probe.ForwardGaps);
    }

    /// <summary>The case the whole measurement exists to detect.</summary>
    [Fact]
    public void AnOutOfOrderFrameIsCountedAsAnInversion()
    {
        var probe = new FrameOrderProbe();
        ulong cursor = 0;

        probe.Observe(ref cursor, 1);
        probe.Observe(ref cursor, 2);
        probe.Observe(ref cursor, 3);

        Assert.True(probe.Observe(ref cursor, 2), "a frame below the highest seen is out of order");

        Assert.Equal(1, probe.Inversions);
        Assert.Equal(0, probe.Duplicates);
        Assert.Equal(1, probe.LargestBackwardJump);
    }

    /// <summary>
    /// A duplicate is counted apart from an inversion: both are refused by a strict
    /// monotonic rule, but only one of them means the transport failed to order.
    /// </summary>
    [Fact]
    public void ARepeatedTickIsADuplicateNotAnInversion()
    {
        var probe = new FrameOrderProbe();
        ulong cursor = 0;

        probe.Observe(ref cursor, 1);
        Assert.True(probe.Observe(ref cursor, 1));

        Assert.Equal(1, probe.Duplicates);
        Assert.Equal(0, probe.Inversions);
    }

    /// <summary>
    /// A gap is accepted and moves the cursor forward. A strict rule MUST do this: a lost
    /// frame leaves a hole, and refusing to pass it turns packet loss into a disconnect.
    /// </summary>
    [Fact]
    public void AForwardGapIsAcceptedAndCounted()
    {
        var probe = new FrameOrderProbe();
        ulong cursor = 0;

        probe.Observe(ref cursor, 1);
        Assert.False(probe.Observe(ref cursor, 50), "a gap forward is not a reordering");

        Assert.Equal(1, probe.ForwardGaps);
        Assert.Equal(0, probe.Inversions);
        Assert.Equal(50UL, cursor);
    }

    /// <summary>The worst backward jump is what a window would have to span.</summary>
    [Fact]
    public void LargestBackwardJumpTracksTheWorstInversion()
    {
        var probe = new FrameOrderProbe();
        ulong cursor = 0;

        probe.Observe(ref cursor, 100);
        probe.Observe(ref cursor, 95);   // back 5
        probe.Observe(ref cursor, 60);   // back 40
        probe.Observe(ref cursor, 99);   // back 1

        Assert.Equal(3, probe.Inversions);
        Assert.Equal(40, probe.LargestBackwardJump);
    }

    /// <summary>
    /// The first frame establishes the cursor rather than being judged against zero — a
    /// reconnecting client legitimately starts a new session at any tick.
    /// </summary>
    [Fact]
    public void TheFirstFrameOnAConnectionIsNotAnInversion()
    {
        var probe = new FrameOrderProbe();
        ulong cursor = 0;

        Assert.False(probe.Observe(ref cursor, 9999));
        Assert.Equal(0, probe.Inversions);
        Assert.Equal(9999UL, cursor);
    }

    /// <summary>Connections are independent — one client's ticks cannot implicate another's.</summary>
    [Fact]
    public void CursorsAreIndependentPerConnection()
    {
        var probe = new FrameOrderProbe();
        ulong a = 0, b = 0;

        probe.Observe(ref a, 100);
        Assert.False(probe.Observe(ref b, 1), "a second connection starts its own cursor");

        Assert.Equal(0, probe.Inversions);
    }
}
