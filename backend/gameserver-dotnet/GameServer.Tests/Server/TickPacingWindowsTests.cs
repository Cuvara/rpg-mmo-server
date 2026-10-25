using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit.Abstractions;
using GameServer.World;
using GameServer.Server;

namespace GameServer.Tests.Server;

/// <summary>
/// Windows tick pacing. Without a high-resolution timer the dedicated tick thread wakes on
/// the 15.625 ms system quantum and issues ticks in bursts on quantum boundaries
/// (stdev of 4-tick intervals ~6.9 ms). With <see cref="TickSleeper"/> it is ~1 ms.
///
/// <para>CI noise: a shared runner can stall a thread for tens of ms (GC, neighbours), which
/// inflates a stdev over any single run. The test therefore takes the best of three
/// attempts. The failure it guards (quantised wake-ups) is systematic: it reproduces in
/// every attempt at ~7 ms, so best-of-three cannot hide it, while a one-off stall cannot
/// fail the test. Thresholds: before the fix &gt; 3 ms, after &lt; 1.5 ms.</para>
/// </summary>
public class TickPacingWindowsTests
{
    private const int BaseHz = 60;
    private const int Ticks = 240;
    private const int Window = 4;
    private const int Attempts = 3;
    private const double MaxStdevMs = 1.5;

    private readonly ITestOutputHelper _output;
    public TickPacingWindowsTests(ITestOutputHelper output) => _output = output;

    private sealed class Stamper : ISimulationPhase
    {
        public readonly long[] Stamps = new long[Ticks + 1];
        public int Count;
        public void Tick(ulong currentTick)
        {
            if (Count < Stamps.Length) Stamps[Count++] = Stopwatch.GetTimestamp();
        }
    }

    private static double MeasureStdevMs()
    {
        using var world = new EcsWorld();
        var stamper = new Stamper();
        var loop = new TickLoop(
            world, new InputHandler(world, NullLogger.Instance), new ConnectionManager(),
            BaseHz, GameConstants.DefaultAoiRadius, NullLogger.Instance, simulationPhase: stamper);

        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        var sw = Stopwatch.StartNew();
        while (stamper.Count < stamper.Stamps.Length && sw.Elapsed < TimeSpan.FromSeconds(30))
            Thread.Sleep(20);
        cts.Cancel();
        run.Wait(TimeSpan.FromSeconds(5));

        Assert.True(stamper.Count >= stamper.Stamps.Length, "tick loop did not reach the sample size");

        // Skip the first second: JIT/tiering warm-up is not pacing.
        var iv = new List<double>();
        for (int i = BaseHz + Window; i < stamper.Stamps.Length; i += Window)
            iv.Add((stamper.Stamps[i] - stamper.Stamps[i - Window]) * 1000.0 / Stopwatch.Frequency);
        double mean = iv.Average();
        return Math.Sqrt(iv.Sum(x => (x - mean) * (x - mean)) / iv.Count);
    }

    [SkippableFact]
    public void TickIntervals_AreNotQuantisedToTheWindowsTimerTick()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "Windows-only: the 15.625 ms timer quantum does not exist on Linux/macOS.");

        double best = double.MaxValue;
        for (int a = 0; a < Attempts && best >= MaxStdevMs; a++)
        {
            double s = MeasureStdevMs();
            _output.WriteLine($"attempt {a + 1}: stdev of {Window}-tick intervals = {s:F2} ms");
            best = Math.Min(best, s);
        }

        Assert.True(best < MaxStdevMs,
            $"4-tick interval stdev {best:F2} ms >= {MaxStdevMs} ms: tick loop is quantised to the OS timer");
    }

    [SkippableFact]
    public void TickSleeper_UsesHighResolutionTimerOnWindows_AndWakesOnCancel()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: high-resolution waitable timer.");

        using var cts = new CancellationTokenSource();
        using var sleeper = new TickSleeper(cts.Token);
        Assert.True(sleeper.IsHighResolution);

        var sw = Stopwatch.StartNew();
        Assert.False(sleeper.Sleep(5));
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 3, 12);

        cts.CancelAfter(20);
        sw.Restart();
        Assert.True(sleeper.Sleep(2000));
        Assert.True(sw.Elapsed.TotalMilliseconds < 500);
    }
}
