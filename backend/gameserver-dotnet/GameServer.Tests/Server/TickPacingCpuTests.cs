using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit.Abstractions;
using GameServer.World;
using GameServer.Server;

namespace GameServer.Tests.Server;

/// <summary>Timing tests measure process-wide CPU and wake-up jitter, so they run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TickPacingTimingCollection
{
    public const string Name = "TickPacingTiming";
}

/// <summary>
/// Process CPU cost of the tick loop's wait on an idle world at 60 Hz. The wait is a coarse
/// sleep plus a bounded spin, so its CPU cost per tick is the spin: a regression that spins
/// for the whole residual (or for ~2 ms, as <c>SpinOnce(sleep1Threshold: -1)</c> after a
/// truncated millisecond sleep did) shows up here and nowhere in the pacing tests.
///
/// <para>Set <c>TICK_CPU_BENCH_TICKS</c> (default 600, one 10 s run) to 2000 or more for a
/// reference measurement; the result is written to the test output as
/// <c>cpu_us_per_tick</c>. The ceiling is generous (an idle tick plus the bounded spin is
/// well below it) so a noisy shared runner does not flake, while a spin of 1-2 ms does fail.</para>
/// </summary>
[Collection(TickPacingTimingCollection.Name)]
public class TickPacingCpuTests
{
    private const int BaseHz = 60;
    private const int WarmupTicks = BaseHz;
    private const double MaxCpuMsPerTick = 0.8;

    private readonly ITestOutputHelper _output;
    public TickPacingCpuTests(ITestOutputHelper output) => _output = output;

    private sealed class Counter : ISimulationPhase
    {
        public long Count;
        public long FirstStamp, LastStamp;
        public void Tick(ulong currentTick)
        {
            long n = Interlocked.Increment(ref Count);
            if (n == 1) FirstStamp = Stopwatch.GetTimestamp();
            LastStamp = Stopwatch.GetTimestamp();
        }
    }

    [Fact]
    public void IdleTickLoop_CpuPerTick_IsBounded()
    {
        int ticks = int.TryParse(Environment.GetEnvironmentVariable("TICK_CPU_BENCH_TICKS"), out var n) && n > 0 ? n : 600;

        using var world = new EcsWorld();
        var counter = new Counter();
        var loop = new TickLoop(
            world, new InputHandler(world, NullLogger.Instance), new ConnectionManager(),
            BaseHz, GameConstants.DefaultAoiRadius, NullLogger.Instance, simulationPhase: counter);

        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);
        using var proc = Process.GetCurrentProcess();

        WaitForCount(counter, WarmupTicks);
        proc.Refresh();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        long c0 = Interlocked.Read(ref counter.Count);
        long t0 = Stopwatch.GetTimestamp();

        WaitForCount(counter, c0 + ticks);
        proc.Refresh();
        TimeSpan cpu1 = proc.TotalProcessorTime;
        long c1 = Interlocked.Read(ref counter.Count);
        long t1 = Stopwatch.GetTimestamp();

        cts.Cancel();
        run.Wait(TimeSpan.FromSeconds(5));

        long measured = c1 - c0;
        double cpuMsPerTick = (cpu1 - cpu0).TotalMilliseconds / measured;
        double wallMsPerTick = (t1 - t0) * 1000.0 / Stopwatch.Frequency / measured;
        _output.WriteLine(
            $"os={Environment.OSVersion.Platform} ticks={measured} wall_ms_per_tick={wallMsPerTick:F3} " +
            $"cpu_us_per_tick={cpuMsPerTick * 1000:F1}");

        Assert.InRange(wallMsPerTick, 1000.0 / BaseHz * 0.97, 1000.0 / BaseHz * 1.03);
        Assert.True(cpuMsPerTick < MaxCpuMsPerTick,
            $"tick loop burns {cpuMsPerTick:F3} ms CPU per {1000.0 / BaseHz:F2} ms tick on an idle world");
    }

    private static void WaitForCount(Counter c, long target)
    {
        var sw = Stopwatch.StartNew();
        while (Interlocked.Read(ref c.Count) < target)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromMinutes(5), "tick loop stalled");
            Thread.Sleep(20);
        }
    }
}
