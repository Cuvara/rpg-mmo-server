using System.Diagnostics;
using GameServer.Server;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// The achieved-rate gauge (#144/#153) exists to stop an observer deriving a tick rate from
/// <c>current_tick / uptime_seconds</c>. That derivation mixed a <c>Stopwatch</c>-paced
/// counter with a <c>DateTime.UtcNow</c> duration and, on a host whose CLOCK_REALTIME runs
/// 10-17% fast, reported a healthy 60 Hz loop as 54 Hz — issue #147, filed, propagated into
/// an ADR, blamed for a client defect, and closed as not-a-defect.
///
/// <para>Timestamps are injected, so these test the window arithmetic exactly and without
/// sleeping. A test that slept would be timing the test host, which is the mistake under
/// repair.</para>
/// </summary>
public class AchievedRateMeterTests
{
    private static long Ticks(double seconds) => (long)(Stopwatch.Frequency * seconds);

    /// <summary>
    /// 0 is reserved for "no window has completed", which is a real state for the first
    /// couple of seconds of process life. It must never be confused with a stalled loop —
    /// `current_tick` is what distinguishes those, and this pins the contract the docs make.
    /// </summary>
    [Fact]
    public void BeforeTheFirstWindowCompletes_ReportsZero()
    {
        var meter = new AchievedRateMeter(windowSeconds: 2.0);
        long t0 = 1_000_000;

        Assert.Equal(0d, meter.AchievedHz);

        // A full second of 60 Hz ticks — real work, but less than one window.
        for (int i = 1; i <= 60; i++) meter.Sample((ulong)i, t0 + Ticks(i / 60.0));

        Assert.Equal(0d, meter.AchievedHz);
    }

    /// <summary>
    /// The headline case: a loop genuinely running at 60 Hz must read 60, not 54. The
    /// tolerance is tight on purpose — the error this replaces was ~10%, so a test that
    /// accepted 10% would accept the bug.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(15)]
    [InlineData(5)]
    public void AfterAWindow_ReportsTheRateTheTicksActuallyArrivedAt(int hz)
    {
        var meter = new AchievedRateMeter(windowSeconds: 2.0);
        long t0 = 5_000_000;

        // Three windows' worth, perfectly paced.
        int total = hz * 6;
        for (int i = 0; i <= total; i++) meter.Sample((ulong)i, t0 + Ticks((double)i / hz));

        Assert.InRange(meter.AchievedHz, hz * 0.99, hz * 1.01);
    }

    /// <summary>
    /// A loop that is genuinely slow must read slow. Without this the meter could satisfy
    /// every other test by returning the configured rate, which is the exact failure mode
    /// #144 is about: a field that reports configuration and looks like a measurement.
    /// </summary>
    [Fact]
    public void AHalfSpeedLoop_ReportsHalfTheRate()
    {
        var meter = new AchievedRateMeter(windowSeconds: 2.0);
        long t0 = 0;

        // 30 ticks per second on a server configured for 60.
        for (int i = 0; i <= 180; i++) meter.Sample((ulong)i, t0 + Ticks(i / 30.0));

        Assert.InRange(meter.AchievedHz, 29.5, 30.5);
    }

    /// <summary>
    /// The measurement must follow the loop rather than latch. A server that degrades after
    /// an hour of health is the case an operator actually wants this for.
    /// </summary>
    [Fact]
    public void WhenTheLoopDegrades_TheReportedRateFollowsItDown()
    {
        var meter = new AchievedRateMeter(windowSeconds: 2.0);
        long t = 0;
        ulong tick = 0;

        // 6s healthy at 60 Hz.
        for (int i = 0; i < 360; i++) { tick++; t += Ticks(1 / 60.0); meter.Sample(tick, t); }
        Assert.InRange(meter.AchievedHz, 59.4, 60.6);

        // 6s degraded to 20 Hz.
        for (int i = 0; i < 120; i++) { tick++; t += Ticks(1 / 20.0); meter.Sample(tick, t); }
        Assert.InRange(meter.AchievedHz, 19.8, 20.2);
    }

    /// <summary>
    /// A repeated or non-advancing timestamp must not divide by zero or publish a 0 that
    /// would read as "not measured yet". The tick thread feeds this; it must not be able to
    /// poison the value with a degenerate sample.
    /// </summary>
    [Fact]
    public void DegenerateSamples_DoNotCorruptTheLastGoodValue()
    {
        var meter = new AchievedRateMeter(windowSeconds: 1.0);
        long t = 0;
        ulong tick = 0;

        for (int i = 0; i < 180; i++) { tick++; t += Ticks(1 / 60.0); meter.Sample(tick, t); }
        double good = meter.AchievedHz;
        Assert.InRange(good, 59.4, 60.6);

        // Same timestamp, same tick, repeatedly: elapsed is huge relative to the window
        // only once, then zero. Neither may throw nor overwrite `good` with 0.
        for (int i = 0; i < 10; i++) meter.Sample(tick, t);

        Assert.Equal(good, meter.AchievedHz);
    }

    /// <summary>
    /// A non-positive window would divide by zero once the first sample landed. Falling back
    /// is preferred to throwing here because the caller is a constructor on the tick path.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void NonPositiveWindow_FallsBackToTheDefault(double windowSeconds)
    {
        var meter = new AchievedRateMeter(windowSeconds);
        Assert.Equal(AchievedRateMeter.DefaultWindowSeconds, meter.WindowSeconds);
    }

    /// <summary>
    /// The whole point, stated as a test. The meter is fed monotonic timestamps and returns
    /// the true rate; feeding it a clock running 16.65% fast (measured on this host, #153)
    /// is what produces the #147 reading. This documents the difference the gauge removes
    /// rather than asserting on any production code path.
    /// </summary>
    [Fact]
    public void AFastWallClockIsWhatProducedTheFiftyFourHertzReading()
    {
        const double skew = 1.1665; // CLOCK_REALTIME / CLOCK_MONOTONIC measured on this box
        const int trueHz = 60;

        var monotonic = new AchievedRateMeter(windowSeconds: 2.0);
        var wallClock = new AchievedRateMeter(windowSeconds: 2.0);

        for (int i = 0; i <= trueHz * 6; i++)
        {
            double realSeconds = (double)i / trueHz;
            monotonic.Sample((ulong)i, Ticks(realSeconds));
            // A wall clock running fast reports MORE elapsed time for the same interval,
            // so the same ticks look spread further apart, so the rate looks lower.
            wallClock.Sample((ulong)i, Ticks(realSeconds * skew));
        }

        Assert.InRange(monotonic.AchievedHz, 59.4, 60.6);
        // ~51.4 Hz — the phantom deficit, from a loop that never missed a tick.
        Assert.True(wallClock.AchievedHz < 53.0,
            $"expected the skewed clock to under-report; got {wallClock.AchievedHz:F2}Hz");
    }

    /// <summary>
    /// End-to-end wiring: a <see cref="TickLoop"/> that has actually run for longer than one
    /// window reports a rate near the one it was configured for. The unit tests above prove
    /// the arithmetic; this proves the loop feeds it, which is the half a pure unit test
    /// cannot cover and the half that was missing before.
    ///
    /// <para><b>It asks for a 200ms window rather than the 2s default, and so runs for well
    /// under a second.</b> The first version ran a real loop for three seconds, which in a
    /// parallel xUnit suite means three seconds of a busy tick loop competing with every
    /// other test for the cores it is trying to measure — and this suite already has a
    /// documented history of contention-sensitive failures. The window length is the thing
    /// under test only in the unit tests above; here it is scaffolding, so it should be as
    /// small as it can be while still completing.</para>
    ///
    /// <para>The tolerance is deliberately loose: a loaded host schedules the loop late and
    /// the achieved rate then genuinely IS lower. The claim is that the value is populated
    /// and in the right neighbourhood, not that this box hits 30 Hz exactly — asserting
    /// tightly here would be asserting on the test host, which is the mistake this whole
    /// issue is about.</para>
    /// </summary>
    [Fact]
    public async Task ARunningTickLoop_PublishesANonZeroAchievedRate()
    {
        // `using`: every other test in this suite disposes its world, and this one leaked
        // one. An EcsWorld owns a ReaderWriterLockSlim and an Arch world, and Arch tracks
        // worlds in process-global state — leaking them from a parallel suite is not a
        // tidiness point.
        using var world = new GameServer.World.EcsWorld();
        var connections = new GameServer.Net.ConnectionManager();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var handler = new GameServer.Input.InputHandler(world, logger, null, 30, null);
        var loop = new TickLoop(
            world, handler, connections, 30,
            global::Shared.GameLogic.Components.GameConstants.DefaultAoiRadius, logger,
            achievedRateWindowSeconds: 0.2);

        using var cts = new CancellationTokenSource();
        var run = loop.RunAsync(cts.Token);

        // One 200ms window plus margin for the loop to publish across it.
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        Assert.True(loop.CurrentTick > 0, "the loop did not tick at all");
        Assert.True(loop.AchievedTickHz > 0d,
            $"achieved rate was never published (current_tick={loop.CurrentTick})");
        Assert.InRange(loop.AchievedTickHz, 5d, 60d);
    }
}
