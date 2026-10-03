namespace GameServer.Tests.Shared;

/// <summary>
/// Lag-compensation history and the ADR-29 rewind clamp (200 ms).
/// </summary>
public class HitboxHistoryTests
{
    [Theory]
    [InlineData(15, 200, 5)]  // ceil(3.0) + 2
    [InlineData(20, 200, 6)]  // 4 + 2
    [InlineData(60, 200, 14)] // 12 + 2
    [InlineData(30, 210, 9)]  // ceil(6.3) = 7, + 2
    [InlineData(15, 0, 2)]
    public void Capacity_FromTickRateAndCap(int rate, int maxMs, int expected)
    {
        Assert.Equal(expected, HitboxHistory.CapacityFor(rate, maxMs));
        Assert.Equal(expected, new HitboxHistory(rate, 4, maxMs).CapacityTicks);
    }

    [Theory]
    // current, render, rate, expected
    [InlineData(1000ul, 0ul, 15, 1000ul)]    // not sent: no rewind
    [InlineData(1000ul, 1001ul, 15, 1000ul)] // future: ignored
    [InlineData(1000ul, 1000ul, 15, 1000ul)]
    [InlineData(1000ul, 998ul, 15, 998ul)]
    [InlineData(1000ul, 997ul, 15, 997ul)]   // exactly 200 ms at 15 Hz
    [InlineData(1000ul, 996ul, 15, 997ul)]   // beyond: clamped
    [InlineData(1000ul, 1ul, 15, 997ul)]
    [InlineData(5000ul, 4900ul, 60, 4988ul)]
    [InlineData(2ul, 1ul, 15, 1ul)]
    [InlineData(1000ul, 990ul, 0, 1000ul)]   // no rate: no rewind
    public void ClampRewindTick_Adr29(ulong current, ulong render, int rate, ulong expected)
    {
        Assert.Equal(expected, HitboxHistory.ClampRewindTick(current, render, rate, HitboxHistory.MaxRewindMs));
    }

    [Fact]
    public void ClampRewindTick_NeverExceedsCap()
    {
        for (int rate = 1; rate <= 128; rate++)
        {
            ulong tick = HitboxHistory.ClampRewindTick(10_000, 1, rate, HitboxHistory.MaxRewindMs);
            double rewoundMs = (10_000 - tick) * 1000.0 / rate;
            Assert.True(rewoundMs <= HitboxHistory.MaxRewindMs, $"rate {rate}: {rewoundMs} ms");
        }
    }

    [Fact]
    public void ClampRewind_DropsAlphaWhenClampedOrCurrent()
    {
        HitboxHistory.ClampRewind(1000, 998, 0.25f, 15, 200, out ulong t, out float a);
        Assert.Equal(998ul, t);
        Assert.Equal(0.25f, a);

        HitboxHistory.ClampRewind(1000, 900, 0.25f, 15, 200, out t, out a);
        Assert.Equal(997ul, t);
        Assert.Equal(0f, a);

        HitboxHistory.ClampRewind(1000, 1000, 0.5f, 15, 200, out t, out a);
        Assert.Equal(1000ul, t);
        Assert.Equal(0f, a);

        HitboxHistory.ClampRewind(1000, 999, float.NaN, 15, 200, out _, out a);
        Assert.Equal(0f, a);

        HitboxHistory.ClampRewind(1000, 999, 3f, 15, 200, out _, out a);
        Assert.True(a < 1f);
    }

    [Fact]
    public void RecordAndInterpolate()
    {
        var h = new HitboxHistory(15, 8);
        h.Record(100, 1, new Vec3(0f, 0f, 0f));
        h.Record(101, 1, new Vec3(2f, 4f, 1f));

        Assert.True(h.TryGetAt(1, 100, 0f, out Vec3 p0));
        Assert.Equal(Vec3.Zero, p0);
        Assert.True(h.TryGetAt(1, 100, 0.25f, out Vec3 p1));
        Assert.Equal(new Vec3(0.5f, 1f, 0.25f), p1);
        Assert.True(h.TryGetAt(1, 101, 0.5f, out Vec3 p2)); // no tick 102: holds
        Assert.Equal(new Vec3(2f, 4f, 1f), p2);
        Assert.False(h.TryGetAt(2, 100, 0f, out _));        // unknown key
        Assert.False(h.TryGetAt(1, 99, 0f, out _));         // never recorded
    }

    [Fact]
    public void RecordingSameKeyTwice_Overwrites()
    {
        var h = new HitboxHistory(15, 1);
        Assert.True(h.Record(5, 9, new Vec3(1f, 1f, 1f)));
        Assert.True(h.Record(5, 9, new Vec3(2f, 2f, 2f)));
        Assert.False(h.Record(5, 10, Vec3.Zero)); // full
        Assert.True(h.TryGetExact(9, 5, out Vec3 p));
        Assert.Equal(new Vec3(2f, 2f, 2f), p);
    }

    [Fact]
    public void OldTicksAreEvicted_AndTickZeroIsNeverRecorded()
    {
        var h = new HitboxHistory(15, 4); // 5 ticks
        for (ulong t = 1; t <= 10; t++) h.Record(t, 1, new Vec3(t, 0f, 0f));
        Assert.False(h.HasTick(5));
        Assert.True(h.HasTick(6));
        Assert.True(h.TryGetExact(1, 10, out Vec3 p));
        Assert.Equal(10f, p.X);
        Assert.False(h.Record(0, 1, Vec3.Zero));
    }

    [Fact]
    public void BeginTick_ClearsAStaleSlot()
    {
        var h = new HitboxHistory(15, 4); // 5 ticks
        h.Record(3, 1, Vec3.Zero);
        h.BeginTick(8); // same slot as 3, nobody recorded
        Assert.False(h.HasTick(3));
        Assert.True(h.HasTick(8));
        Assert.False(h.TryGetExact(1, 8, out _));
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var h = new HitboxHistory(15, 4);
        h.Record(7, 1, Vec3.Zero);
        h.Clear();
        Assert.False(h.HasTick(7));
    }

    [Fact]
    public void FullWindowIsAlwaysAvailable()
    {
        // The rewind target and its tick + 1 partner must both still be held.
        foreach (int rate in new[] { 10, 15, 20, 30, 60 })
        {
            var h = new HitboxHistory(rate, 2);
            const ulong current = 1000;
            for (ulong t = current - 40; t <= current; t++) h.Record(t, 1, new Vec3(t, 0f, 0f));
            ulong rewind = HitboxHistory.ClampRewindTick(current, 1, rate, HitboxHistory.MaxRewindMs);
            Assert.True(h.HasTick(rewind), $"rate {rate}");
            Assert.True(h.HasTick(rewind + 1), $"rate {rate}");
        }
    }

    [Fact]
    public void RecordAndLookup_DoNotAllocate()
    {
        var h = new HitboxHistory(15, 64);
        h.Record(1, 0, Vec3.Zero);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong t = 2; t < 500; t++)
        {
            for (int k = 0; k < 64; k++) h.Record(t, k, new Vec3(k, t, 0f));
            h.TryGetAt(17, t - 1, 0.5f, out _);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
