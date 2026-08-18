using System.Diagnostics;
using System.Threading;

namespace GameServer.Server;

/// <summary>
/// Measures the rate the base tick timeline is <b>actually</b> advancing at, over a short
/// sliding window, entirely on the monotonic clock.
///
/// <para><b>Why this exists at all.</b> <c>/status</c> published a configured rate, a tick
/// counter and an uptime, and no achieved rate — so the obvious thing for an observer to do
/// was divide the counter by the uptime. Issue #147 is what that produces: "the tick loop
/// runs at 54Hz while advertising 60", filed against a server that was running at exactly
/// 60, propagated into an ADR and blamed for a client prediction defect before being closed
/// as not-a-defect. The loop was never wrong; the instrument was. This host's
/// <c>CLOCK_REALTIME</c> runs 10-17% fast against <c>CLOCK_MONOTONIC</c> (#153), and the
/// division mixed the two clocks — a <c>Stopwatch</c>-paced counter over a
/// <c>DateTime.UtcNow</c> duration.</para>
///
/// <para><b>The fix is not accuracy, it is removing the choice.</b> An observer that has to
/// supply its own clock will eventually supply a bad one, and the resulting number looks
/// exactly like a server defect. So the server measures its own rate and publishes it, and
/// no correct usage of the endpoint requires arithmetic across two fields.</para>
///
/// <para><b>Every timestamp here is <see cref="Stopwatch"/>.</b> There is deliberately no
/// overload taking a <see cref="System.DateTime"/>: a wall-clock-derived achieved rate would
/// reproduce #147 <i>inside</i> the server, which is strictly worse than the bug it
/// replaces, because it would carry the server's authority.</para>
///
/// <para>The caller supplies timestamps rather than this type reading the clock, so the
/// window logic is testable without sleeping.</para>
/// </summary>
public sealed class AchievedRateMeter
{
    /// <summary>
    /// Default window length in seconds.
    ///
    /// <para>Two, not one and not thirty. A window is a trade: short enough that a loop
    /// which has just started to degrade shows it while someone is still looking, long
    /// enough that the number does not jitter with a single late tick. It is also the
    /// interval a freshly started pod reports <see cref="AchievedHz"/> = 0 for, which is
    /// why it is not larger — the intended use is a person running one <c>curl</c> against
    /// a server they are suspicious of.</para>
    /// </summary>
    public const double DefaultWindowSeconds = 2.0;

    private readonly long _windowTicks;

    private ulong _windowStartTick;
    private long _windowStartTimestamp;
    private bool _started;

    /// <summary>
    /// Published rate. Written on the tick thread, read on the scrape/HTTP thread, so both
    /// sides go through <see cref="Volatile"/>. A torn double is not a hypothetical concern
    /// on a 32-bit target and the cost here is nothing.
    /// </summary>
    private double _achievedHz;

    /// <param name="windowSeconds">
    /// Window length. Values &lt;= 0 fall back to <see cref="DefaultWindowSeconds"/> rather
    /// than dividing by zero later.
    /// </param>
    public AchievedRateMeter(double windowSeconds = DefaultWindowSeconds)
    {
        double seconds = windowSeconds > 0 ? windowSeconds : DefaultWindowSeconds;
        WindowSeconds = seconds;
        _windowTicks = (long)(Stopwatch.Frequency * seconds);
    }

    /// <summary>The configured window length, in seconds.</summary>
    public double WindowSeconds { get; }

    /// <summary>
    /// The measured base-tick rate in Hz over the most recently completed window, or
    /// <b>0 when no window has completed yet</b> — i.e. for the first
    /// <see cref="WindowSeconds"/> of process life.
    ///
    /// <para>0 means "not measured yet", never "the loop has stopped". The two are
    /// distinguishable by <c>current_tick</c>, which is 0 only in the former case. This is
    /// documented rather than signalled with a sentinel because the field is JSON and a
    /// null there would break every reader that types it as a number.</para>
    /// </summary>
    public double AchievedHz => Volatile.Read(ref _achievedHz);

    /// <summary>
    /// Feed one base tick. Called once per iteration of the tick loop, on the tick thread.
    /// </summary>
    /// <param name="tick">The base tick counter after this tick ran.</param>
    /// <param name="timestamp">A <see cref="Stopwatch.GetTimestamp"/> value. Never a wall clock.</param>
    /// <remarks>
    /// O(1), allocation-free, and two comparisons in the common case — this runs inside the
    /// budget it is measuring, so it must not be a reason the measurement changes.
    /// </remarks>
    public void Sample(ulong tick, long timestamp)
    {
        if (!_started)
        {
            _windowStartTick = tick;
            _windowStartTimestamp = timestamp;
            _started = true;
            return;
        }

        long elapsed = timestamp - _windowStartTimestamp;
        if (elapsed < _windowTicks) return;

        // Guard both degenerate cases rather than trusting the caller: a non-monotonic or
        // repeated timestamp would divide by zero, and a tick counter that did not advance
        // would publish 0, which this type reserves for "not measured yet".
        if (elapsed > 0 && tick > _windowStartTick)
        {
            double seconds = elapsed / (double)Stopwatch.Frequency;
            Volatile.Write(ref _achievedHz, (tick - _windowStartTick) / seconds);
        }

        _windowStartTick = tick;
        _windowStartTimestamp = timestamp;
    }
}
