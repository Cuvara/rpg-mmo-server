using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using GameServer.World;
using GameServer.Server;

namespace GameServer.Tests.Server;

/// <summary>
/// A failing <c>SetWaitableTimer</c> / <c>CreateWaitableTimerExW</c> must degrade to the
/// Linux-equivalent <c>WaitOne</c> + <c>SpinOnce</c> pacing, once, and not busy-spin.
/// The failure is injected through an internal seam, so these run on every OS.
/// </summary>
[Collection(TickPacingTimingCollection.Name)]
public class TickSleeperFallbackTests
{
    private const int BaseHz = 60;

    private sealed class Counter : ISimulationPhase
    {
        public long Count;
        public void Tick(ulong currentTick) => Interlocked.Increment(ref Count);
    }

    [Fact]
    public void SetTimerFailure_FallsBackOnce_AndStopsUsingHighResolution()
    {
        using var cts = new CancellationTokenSource();
        using var timer = new ManualResetEvent(false);
        int reports = 0, setCalls = 0;
        using var sleeper = new TickSleeper(cts.Token, _ => reports++,
            () => timer, (_, _) => { setCalls++; return false; });

        Assert.True(sleeper.IsHighResolution);
        Assert.False(sleeper.SleepHighResolution(Stopwatch.Frequency / 200)); // 5 ms
        Assert.False(sleeper.IsHighResolution);
        Assert.True(sleeper.FellBack);

        Assert.False(sleeper.Sleep(2));
        Assert.Equal(1, setCalls);
        Assert.Equal(1, reports);
    }

    [Fact]
    public void CreateTimerFailure_UsesLegacySleep_AndReportsOnce()
    {
        using var cts = new CancellationTokenSource();
        int reports = 0;
        using var sleeper = new TickSleeper(cts.Token, _ => reports++, () => null, (_, _) => true);

        Assert.False(sleeper.IsHighResolution);
        Assert.Equal(1, reports);
        var sw = Stopwatch.StartNew();
        Assert.False(sleeper.Sleep(5));
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 3, 40);
    }

    [Fact]
    public void FailedSleep_WakesOnCancel()
    {
        using var cts = new CancellationTokenSource();
        using var timer = new ManualResetEvent(false);
        using var sleeper = new TickSleeper(cts.Token, null, () => timer, (_, _) => false);
        cts.CancelAfter(20);
        Assert.True(sleeper.SleepHighResolution(Stopwatch.Frequency * 2));
    }

    [Fact]
    public void TickLoop_WithFailingTimer_PacesCorrectly_WithBoundedCpu()
    {
        const int ticks = 300;
        using var world = new EcsWorld();
        var counter = new Counter();
        var loop = new TickLoop(
            world, new InputHandler(world, NullLogger.Instance), new ConnectionManager(),
            BaseHz, GameConstants.DefaultAoiRadius, NullLogger.Instance, simulationPhase: counter);
        int reports = 0;
        TickLoop.SleeperFactoryForTests = (ct, cb) =>
            new TickSleeper(ct, r => { reports++; cb(r); }, () => new ManualResetEvent(false), (_, _) => false);

        try
        {
            using var cts = new CancellationTokenSource();
            var proc = Process.GetCurrentProcess();
            var run = loop.RunAsync(cts.Token);
            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref counter.Count) < BaseHz && sw.Elapsed < TimeSpan.FromSeconds(10))
                Thread.Sleep(5); // warm-up
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            long n0 = Interlocked.Read(ref counter.Count);
            sw.Restart();
            while (Interlocked.Read(ref counter.Count) - n0 < ticks && sw.Elapsed < TimeSpan.FromSeconds(30))
                Thread.Sleep(10);
            double wall = sw.Elapsed.TotalSeconds;
            long done = Interlocked.Read(ref counter.Count) - n0;
            proc.Refresh();
            double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            cts.Cancel();
            run.Wait(TimeSpan.FromSeconds(5));

            Assert.True(done >= ticks, "tick loop did not reach the sample size");
            Assert.Equal(1, reports);
            Assert.InRange(done / wall, BaseHz * 0.9, BaseHz * 1.1);
            // A flat-out spin costs ~16 ms of CPU per 16.7 ms tick; the bounded path ~0.2 ms.
            Assert.True(cpuMs / done < 3.0, $"{cpuMs / done:F2} ms CPU per tick: tick wait is busy-spinning");
        }
        finally
        {
            TickLoop.SleeperFactoryForTests = null;
        }
    }
}
