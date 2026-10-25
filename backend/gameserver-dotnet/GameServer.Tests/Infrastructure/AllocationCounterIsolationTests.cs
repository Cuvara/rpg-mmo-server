using System.Diagnostics;
using Xunit.Abstractions;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// Pins the SC-003 measurement fix (<see cref="ThreadAllocation"/>): a zero-allocation window
/// that starts from an empty allocation context is exact while background GCs run, and still
/// reports a real allocation made inside it.
/// </summary>
public class AllocationCounterIsolationTests(ITestOutputHelper output)
{
    private const int Windows = 25;
    private static object? _sink;

    /// <summary>
    /// One window per gen2 GC: the thread allocates one small object (leaving a partly used
    /// allocation context), reads the counter, spins without allocating until a gen2 GC has
    /// completed, reads again. Returns the counter delta, and whether a background GC was
    /// among those collected.
    /// </summary>
    private static long Window(bool quiesce, bool inject, out bool background)
    {
        long bgcBefore = GC.GetGCMemoryInfo(GCKind.Background).Index; // allocates: outside the window
        _sink = new byte[100];
        int gen2 = GC.CollectionCount(2);
        long before = quiesce ? ThreadAllocation.Begin() : GC.GetAllocatedBytesForCurrentThread();
        long deadline = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency; // a struct read: allocates nothing
        if (inject) _sink = new byte[24];
        while (GC.CollectionCount(2) == gen2 && Stopwatch.GetTimestamp() < deadline) Thread.SpinWait(20);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        background = GC.GetGCMemoryInfo(GCKind.Background).Index != bgcBefore;
        return delta;
    }

    [Fact]
    public void QuiescedWindow_IsExact_WhileBackgroundGcsRun()
    {
        using var pressure = BackgroundGcPressure.Start();
        Window(quiesce: true, inject: false, out _); // warm the helper's own code
        int backgroundWindows = 0;
        long worst = 0;
        for (int i = 0; i < Windows; i++)
        {
            long delta = Window(quiesce: true, inject: false, out bool bg);
            if (bg) backgroundWindows++;
            worst = Math.Max(worst, delta);
        }

        output.WriteLine($"{Windows} windows, {backgroundWindows} with a background GC, worst delta {worst} B");
        Assert.Equal(0L, worst);
    }

    [Theory]
    [InlineData(24)]
    public void QuiescedWindow_StillCountsARealAllocation_WhileBackgroundGcsRun(int injected)
    {
        using var pressure = BackgroundGcPressure.Start();
        Window(quiesce: true, inject: true, out _);
        for (int i = 0; i < Windows; i++)
        {
            long delta = Window(quiesce: true, inject: true, out _);
            Assert.True(delta >= injected, $"window {i}: a {injected} B allocation inside the window read as {delta} B");
        }
    }

    [Fact]
    public void PlainRead_Overcounts_OnlyWhenABackgroundGcRuns_Diagnostic()
    {
        // Evidence for the mechanism, not an assertion about the runtime: a read that does not
        // start from an empty context may see the unused tail of the allocation context after
        // a background GC (void_allocation does not subtract it). Reported, never asserted,
        // because a runtime that fixes this would make the number zero and the guards correct.
        using var pressure = BackgroundGcPressure.Start();
        Window(quiesce: false, inject: false, out _);
        int overcounted = 0, background = 0;
        long worst = 0;
        for (int i = 0; i < Windows; i++)
        {
            long delta = Window(quiesce: false, inject: false, out bool bg);
            if (bg) background++;
            if (delta != 0) overcounted++;
            worst = Math.Max(worst, delta);
        }

        output.WriteLine($"plain read: {overcounted}/{Windows} windows overcounted ({background} had a background GC), worst {worst} B");
    }
}
