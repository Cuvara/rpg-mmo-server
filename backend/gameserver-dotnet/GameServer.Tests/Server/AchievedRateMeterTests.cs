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
    /// The floor the end-to-end test below must not assert against, pinned with injected
    /// timestamps so it costs nothing and cannot flake.
    ///
    /// <para>The meter publishes <c>tickDelta / elapsed</c> and only when
    /// <c>elapsed &gt;= WindowSeconds</c>, so a loop slow enough to land a single tick in a
    /// window can never publish more than <c>1 / WindowSeconds</c> — and publishes exactly
    /// that only if <c>elapsed</c> is precisely the window length, which no real schedule
    /// hits. The published values are therefore quantised, with nothing between
    /// <c>1 / WindowSeconds</c> and <c>2 / WindowSeconds</c>.</para>
    ///
    /// <para><b>This is why the wiring test below must not carry an absolute lower
    /// bound.</b> It used to assert <c>InRange(achieved, 5d, 60d)</c> against a 0.2s window,
    /// and 5.0 is exactly <c>1 / 0.2</c> — so that bound was not "loose", it sat precisely
    /// on this cliff and was really asserting "the host scheduled at least two ticks into
    /// the last completed window". On a contended box it does not, and the test failed for
    /// a reason that has nothing to do with the meter (#200).</para>
    /// </summary>
    [Theory]
    [InlineData(0.2)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AWindowContainingASingleTick_CannotPublishMoreThanTheReciprocalOfTheWindow(
        double windowSeconds)
    {
        var meter = new AchievedRateMeter(windowSeconds);

        // Two samples, one tick apart, separated by a hair more than one window: the
        // cheapest possible completed window, and the densest a single-tick window can be.
        meter.Sample(0, 0);
        meter.Sample(1, Ticks(windowSeconds * 1.001));

        Assert.True(meter.AchievedHz > 0d, "a completed window must publish something");
        Assert.True(meter.AchievedHz < 1.0 / windowSeconds,
            $"a single-tick window over {windowSeconds}s published {meter.AchievedHz:F3}Hz; " +
            $"the reciprocal of the window ({1.0 / windowSeconds:F3}Hz) is an unreachable " +
            "ceiling for this case, so it is not a usable lower bound for a live-loop test");
    }

    /// <summary>
    /// End-to-end wiring: a <see cref="TickLoop"/> that has actually run reports a rate it
    /// measured rather than one it was configured with. The unit tests above prove the
    /// arithmetic to within 1%; this proves the loop feeds the meter, which is the half a
    /// pure unit test cannot cover.
    ///
    /// <para><b>What it must not do is time the test host.</b> This test failed roughly one
    /// run in thirteen in the full parallel suite and never in isolation (#200). It waited a
    /// fixed 600ms, then read the gauge once and required the reading to be at least 5Hz.
    /// Both halves of that were claims about scheduling: with a 0.2s window the loop has to
    /// land two ticks inside the final window to clear 5Hz, and it has to complete any
    /// window at all inside 600ms to publish a non-zero value. Feeding the real meter a
    /// uniformly-late schedule shows the two failure bands exactly — a per-tick delay of
    /// 201-305ms trips the range check, and anything past ~310ms leaves the gauge at 0 and
    /// trips the published-at-all check instead. A saturated thread pool delays a
    /// <c>Task.Delay(33)</c> by that much, and the suite has a documented history of exactly
    /// this kind of contention (#201, #153).</para>
    ///
    /// <para><b>So it waits for the condition rather than for a duration.</b> It polls until
    /// a window has actually published, with a generous <see cref="Stopwatch"/> cap that
    /// only expires if the loop is genuinely not running. On a healthy host that is quicker
    /// than the old fixed sleep, so the test also stops contributing 600ms of busy loop to
    /// everyone else's contention; on a loaded one it simply waits longer instead of
    /// failing.</para>
    ///
    /// <para><b>And the assertions are relative, not absolute.</b> The upper bound is kept,
    /// because a reading above the configured rate is the direction that means a defect —
    /// double-counted ticks, or a duration measured on a different clock than the counter,
    /// which is #147 itself. The lower bound is expressed against the run's own average
    /// rather than a fixed Hz, so it still catches a meter that under-reports what the loop
    /// did while making no claim about how fast this box happens to be.</para>
    /// </summary>
    [Fact]
    public async Task ARunningTickLoop_PublishesANonZeroAchievedRate()
    {
        const int ConfiguredHz = 30;
        const double WindowSeconds = 0.2;

        // Long enough to see several windows on a healthy host.
        const double MinimumRunMs = 600;

        // Only trips if the loop is not running at all. Deliberately far above any
        // plausible scheduling delay: the point is that a slow host waits, not fails.
        const double CapMs = 10_000;

        // `using`: every other test in this suite disposes its world, and this one leaked
        // one. An EcsWorld owns a ReaderWriterLockSlim and an Arch world, and Arch tracks
        // worlds in process-global state — leaking them from a parallel suite is not a
        // tidiness point.
        using var world = new GameServer.World.EcsWorld();
        var connections = new GameServer.Net.ConnectionManager();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var handler = new GameServer.Input.InputHandler(world, logger, null, ConfiguredHz, null);
        var loop = new TickLoop(
            world, handler, connections, ConfiguredHz,
            global::Shared.GameLogic.Components.GameConstants.DefaultAoiRadius, logger,
            achievedRateWindowSeconds: WindowSeconds);

        using var cts = new CancellationTokenSource();

        // Stopwatch, never DateTime: this host's CLOCK_REALTIME runs 10-17% fast and has
        // been seen stepping backwards (#153), and a wall-clock deadline here would be the
        // very mistake the gauge under test exists to remove.
        var clock = Stopwatch.StartNew();
        var run = loop.RunAsync(cts.Token);

        // The meter is a SLIDING window, so reading it once reads whichever window happened
        // to close last — and on a contended host that can be a window the loop was
        // descheduled through. Keeping the best window observed asks "did any window measure
        // the loop correctly", which is the wiring claim; it does not ask the host to
        // schedule the final 200ms fairly, which is a claim no test can honestly make.
        double best = 0d;
        while (clock.Elapsed.TotalMilliseconds < CapMs)
        {
            await Task.Delay(20);

            double observed = loop.AchievedTickHz;
            if (observed > best) best = observed;

            if (clock.Elapsed.TotalMilliseconds >= MinimumRunMs && best > 0d) break;
        }

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }
        clock.Stop();

        double elapsedSeconds = clock.Elapsed.TotalSeconds;
        ulong ticks = loop.CurrentTick;
        double averageHz = elapsedSeconds > 0 ? ticks / elapsedSeconds : 0d;

        Assert.True(ticks > 0, $"the loop did not tick at all in {elapsedSeconds * 1000:F0}ms");

        Assert.True(best > 0d,
            $"achieved rate was never published in {elapsedSeconds * 1000:F0}ms " +
            $"(current_tick={ticks}, window={WindowSeconds}s). The loop completed " +
            $"{ticks} ticks, so it was running; the gauge is not being fed.");

        // Upper bound, and the arithmetic behind it matters because the obvious bound is
        // wrong. A short window can legitimately measure FASTER than the configured rate:
        // when TickLoop falls behind it replays the lost ticks with no sleep at all until it
        // is caught up or MaxLagTicks behind (TickLoop.RunAsync), so a window straddling a
        // recovery holds up to `window / period + MaxLagTicks` ticks — 6 + 8 = 14 inside
        // 200ms on a 30Hz loop, or 70Hz. This was measured, not reasoned: an earlier version
        // of this assertion capped at 1.5x and a contended run published 49.18Hz, which is a
        // truthful reading of a loop that really did advance that many ticks in that window.
        // The old `InRange(..., 5d, 60d)` had the same latent failure at its top end.
        //
        // So the bound is the one statement that is exactly true regardless of scheduling:
        // a window cannot contain more ticks than the whole run produced. That still catches
        // a meter that double-counts, or one that divides by a duration from a clock other
        // than the one that paced the counter — the #147 defect in its over-reporting
        // direction — while making no assumption about how this host scheduled anything.
        double impossibleHz = ticks / WindowSeconds;
        Assert.True(best <= impossibleHz,
            $"achieved rate {best:F2}Hz needs more ticks inside one {WindowSeconds}s window " +
            $"than the loop ran in the entire test ({ticks} ticks, so at most " +
            $"{impossibleHz:F2}Hz). The meter is over-reporting: it is either counting ticks " +
            "twice or dividing by a duration from a different clock than the counter.");

        // Lower bound, relative to what this run actually managed rather than to a fixed Hz.
        // Over a run spanning several windows at least one window must be as dense as the
        // average, so half the average is generous — but it still fails a meter that reports
        // far below what the loop did, which is the #147 direction.
        Assert.True(best >= averageHz * 0.5,
            $"best published window was {best:F2}Hz while the loop averaged {averageHz:F2}Hz " +
            $"({ticks} ticks in {elapsedSeconds * 1000:F0}ms); no window measured what the " +
            "loop was actually doing");
    }
}
