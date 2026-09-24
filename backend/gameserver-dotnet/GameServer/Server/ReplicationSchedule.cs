using System.Linq;
using System;
using System.Globalization;
using System.Text;

namespace GameServer.Server;

/// <summary>
/// How often an entity of a given importance is re-sent to one connection.
///
/// <para><b>What this is not.</b> It is not a bandwidth cap — that is
/// <c>GAMESERVER_MAX_SNAPSHOT_BYTES</c>, which decides how much of one snapshot may be
/// spent. It is not interest — that is <c>GAMESERVER_AOI_RADIUS</c>, which decides whether
/// an entity is a candidate at all. This decides, among entities that are candidates and
/// whose state this client has not been told, which ones are due <i>this</i> world tick.</para>
///
/// <para><b>Intervals are configured in milliseconds, never in ticks.</b> A tick count is
/// only meaningful next to the rate that advances it, and the rate is deployment
/// configuration (<c>SIM_WORLD_HZ</c>). "Every 4 ticks" silently means 266ms at 15Hz and
/// 133ms at 30Hz, so a deployment that raised its world rate to get smoother movement would
/// have halved the staleness it configured without editing it. Milliseconds convert through
/// <see cref="SimulationRates"/>, exactly as <c>GameConstants.AttackCooldownTicks</c> does
/// for the same reason.</para>
/// </summary>
public sealed class ReplicationSchedule
{
    public const string EnvVar = "GAMESERVER_REPLICATION_SCHEDULE";

    /// <summary>
    /// The longest an entity may wait, whatever its score. Nothing may be scheduled slower
    /// than this.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a preference. The starvation argument requires every interval
    /// to be FINITE: an entity that is dirty but never due would never enter the candidate
    /// list, and therefore never age, and therefore never be promoted by the aging order —
    /// it would be starved by a mechanism the existing starvation test cannot see, because a
    /// not-due entity is not a shed entity.
    ///
    /// <para><b>It was 500, and the sentence justifying it named 150.</b> The original
    /// remark read "500ms is also the point past which the client's 100ms interpolation
    /// buffer plus 50ms extrapolation stops covering the gap at all" — and 100 + 50 is 150.
    /// The ceiling was set 3.3x above the number its own reasoning derived, and the slowest
    /// shipped band (266ms) sat in between, so a distant mob was scheduled to arrive
    /// 116ms after the client had run out of anything to interpolate towards. Three people
    /// playing saw exactly that, as mobs moving in visible steps while players moved
    /// smoothly.</para>
    ///
    /// <para><b>And then it was 150, and that was still the whole budget (#413).</b> The
    /// paragraph above is kept as it stood because it is the reasoning this replaces: it
    /// corrected the constant from 500 and kept the assumption underneath it, that the
    /// scheduler may spend the client's cover down to the last millisecond because the wire
    /// costs nothing. Measured over a relay, the wire costs 33-41ms at ±25ms one-way jitter
    /// and 83-87ms at ±60ms, and the shipped <c>tiered</c> profile reached 148-150ms
    /// <b>on loopback</b> — the budget exhausted with no network in the picture. The ceiling
    /// is now the budget minus <see cref="LinkSpreadAllowanceMs"/>, so the same mobs cannot
    /// step for the same reason by a different route.</para>
    /// </remarks>
    public const int MaxIntervalMs = ClientInterpolationBudgetMs - LinkSpreadAllowanceMs;

    /// <summary>
    /// How much of the client's cover is reserved for the <b>network</b>, leaving the rest
    /// for deferral. 45ms, measured, at a link of ±25ms one-way jitter.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Without it the scheduler spends the client's entire
    /// budget on deferral and assumes the wire is instantaneous. It is not: the gap a client
    /// waits for news of one entity is the scheduler's interval <i>plus</i> however much the
    /// link spread the two arrivals apart, and both come out of the same 150ms.</para>
    ///
    /// <para><b>Measured, not derived</b>, by <c>EntityIntervalUnderAdversityTests</c> — a
    /// real client over a relay, timing the gap between consecutive snapshots carrying a
    /// given entity. Two runs, per-entity p99 in ms:</para>
    ///
    /// <code>
    /// profile  link      p99        the link's share
    /// off      clean     74-79      -
    /// off      ±25ms     111-115    33-41
    /// off      ±60ms     161-162    83-87
    /// tiered   clean     148-150    -          &lt;- at the budget with NO network
    /// tiered   ±25ms     176-178    28
    /// tiered   ±60ms     218-223    67-75
    /// </code>
    ///
    /// <para><b>45 is a choice, not a measurement.</b> It is the ±25ms column rounded up, so
    /// it is a statement about which link this schedule promises to serve — and, just as
    /// importantly, which it does not. It covers ±25ms one-way comfortably. It does
    /// <b>not</b> cover ±60ms, whose share measured 83–87ms: on that link the budget is
    /// exceeded with the scheduler deferring nothing at all, and no value of this constant
    /// changes that. Whoever raises it is promising a worse link and must re-measure the
    /// column they are promising.</para>
    ///
    /// <para>The corresponding client-side reading, from a real Unity client over a relay at
    /// 40ms one-way plus ±60ms jitter: <c>snapshotsApplied</c> 14.8/s, unchanged from clean,
    /// with resyncs, rejected, dropped and clamped all zero and <c>rtt</c> 110ms median /
    /// 179ms p95. The client survives a link far worse than the one tiering needs, so the
    /// constraint here is this budget arithmetic and not client robustness.</para>
    ///
    /// <para>The row that matters most is the fourth: the shipped
    /// <c>tiered</c> profile reached <b>148-150ms on loopback</b>, exhausting the budget
    /// before a single millisecond of network. The 17ms of headroom its 133ms band was
    /// supposed to leave did not survive tick quantisation.</para>
    ///
    /// <para><b>±60ms one-way is outside what this design can serve at all.</b> At a 15Hz
    /// world rate one interval alone is 66.7ms and the link adds 83-87, so the budget is
    /// exceeded with the scheduler deferring nothing. No ceiling fixes that; closing it means
    /// a larger <c>TargetDelay</c> on the client, which costs input latency for everyone in
    /// order to serve the worst link.</para>
    ///
    /// <para><b>The consequence at the shipped world rate, stated rather than discovered.</b>
    /// 105ms at 60/15 is one world tick, so <see cref="Tiered"/>'s slow band no longer defers
    /// and the profile buys nothing there — asserted by
    /// <c>ScheduleFitsTheClientBudgetTests.TieringBuysNothingAtTheShippedWorldRate_AndNeedsAFasterOne</c>
    /// rather than left to be found in a banner. Tiering needs a world rate around 30Hz
    /// before any band both fits the budget and differs from every tick.</para>
    /// </remarks>
    public const int LinkSpreadAllowanceMs = 45;

    /// <summary>
    /// How long a client can cover a gap with no new state: <c>TargetDelay</c> (100ms) plus
    /// <c>MaxExtrapolation</c> (50ms), the shipped defaults in the netcode package's
    /// <c>InterpolationConfig</c>.
    /// </summary>
    /// <remarks>
    /// This is the number that decides whether a deferral is invisible or is a stutter, and
    /// it lives on the CLIENT. Naming it here, with its derivation, is the smallest honest
    /// version of a shared constant: a server interval above it is not "slightly stale", it
    /// is a gap the client has nothing to fill. If the client's config changes, this must
    /// change with it — grep <c>ClientInterpolationBudgetMs</c>.
    /// </remarks>
    public const int ClientInterpolationBudgetMs = 150;

    private readonly Tier[] _tiers;

    private ReplicationSchedule(string profile, Tier[] tiers)
    {
        Profile = profile;
        _tiers = tiers;
    }

    /// <summary>One band: everything scoring at least <see cref="MinScore"/> waits
    /// <see cref="IntervalMs"/>.</summary>
    public readonly struct Tier
    {
        public Tier(float minScore, int intervalMs)
        {
            MinScore = minScore;
            IntervalMs = intervalMs;
        }

        public readonly float MinScore;
        public readonly int IntervalMs;
    }

    public string Profile { get; }

    /// <summary>True when every entity is due every world tick, i.e. today's behaviour.</summary>
    public bool Enabled => _tiers.Length > 0;

    public IReadOnlyList<Tier> Tiers => _tiers;

    /// <summary>The shipped default: no tiering, every dirty entity due every world tick.</summary>
    public static ReplicationSchedule Off { get; } = new("off", Array.Empty<Tier>());

    /// <summary>
    /// The candidate policy, in the bands BENCHMARK.md Part XIII §35 measured.
    /// </summary>
    /// <remarks>
    /// Thresholds are read against the <c>balanced</c> importance weights (change 10,
    /// combat 6, type 3, distance 2), and they only mean anything next to those numbers —
    /// which is why turning tiering on without importance weights is refused.
    /// <list type="bullet">
    /// <item><description><b>score ≥ 8 → every tick.</b> Any untold HP or action change
    /// alone clears this (change is 10), as does an attacking player. These are the updates
    /// a client cannot reconstruct.</description></item>
    /// <item><description><b>everything else → <see cref="MaxIntervalMs"/>.</b> A player at
    /// any distance, or any mob. It is the ceiling itself rather than a number of its own, so
    /// the band can never declare a wait longer than it is allowed to take: a band that says
    /// 133ms and behaves as 66ms is the "one band wearing two names" failure this file
    /// already records, arriving from the other direction.</description></item>
    /// </list>
    ///
    /// <para><b>It was 133ms, and #413 is why it is not.</b> 133 was two world ticks at 15Hz,
    /// chosen to sit inside the client's 150ms of cover with 17ms to spare. Measured over a
    /// relay, the shipped profile reached a per-entity p99 of <b>148-150ms on loopback</b>:
    /// quantisation had eaten the 17ms before any network. With
    /// <see cref="LinkSpreadAllowanceMs"/> reserved for the wire the ceiling is 105ms, and at
    /// 60/15 that is one world tick — so this band no longer defers at the shipped rate.</para>
    ///
    /// <para><b>Two bands, not three, and that is a consequence rather than a preference.</b>
    /// The third band was 266ms, chosen because BENCHMARK.md Part XIII measured 44-46% on a
    /// realistic population at four world ticks. It is above
    /// <see cref="ClientInterpolationBudgetMs"/>, so what it actually bought was a distant
    /// mob arriving 116ms after the client had stopped having anything to interpolate
    /// towards. At a 15Hz world rate and a 150ms client budget, the only intervals that fit
    /// are one tick (66ms) and two (133ms) — a third band needs a faster world rate or a
    /// client that holds more, and inventing one here just moves the cost somewhere it is
    /// not measured. The measured price of dropping it is 13 points of saving —
    /// BENCHMARK.md Part XVI §48.</para>
    /// </remarks>
    public static ReplicationSchedule Tiered { get; } = new("tiered", new[]
    {
        new Tier(8f, 0),
        new Tier(float.NegativeInfinity, MaxIntervalMs),
    });

    /// <summary>
    /// Interval in WORLD ticks for a score, at a given world rate. 1 means "every tick".
    /// </summary>
    public int IntervalTicksFor(float score, int worldHz)
    {
        if (_tiers.Length == 0 || worldHz <= 0) return 1;

        for (int i = 0; i < _tiers.Length; i++)
        {
            if (score >= _tiers[i].MinScore) return TicksFor(_tiers[i].IntervalMs, worldHz);
        }
        return 1;
    }

    /// <summary>
    /// Interval in BASE ticks for a score, clamped so the wait an entity <b>actually</b>
    /// takes stays inside <see cref="MaxIntervalMs"/>.
    /// </summary>
    /// <param name="score">Importance score.</param>
    /// <param name="baseHz">Base (critical) tick rate — the counter intervals are in.</param>
    /// <param name="worldEvery">Base ticks per world tick, i.e. per emission.</param>
    /// <remarks>
    /// <para><b>The ceiling is in base ticks and emission is not.</b> Snapshots go out on
    /// world ticks, so an interval of N base ticks is served on the next world tick at or
    /// after N: the real wait is <c>ceil(N / worldEvery)</c> world periods, always a whole
    /// number of them. A ceiling landing between two periods therefore rounds UP and buys
    /// nothing at all.</para>
    ///
    /// <para>This was invisible while the ceiling was 150ms, because the band under it was
    /// 133ms — 8 base ticks at 60/15, exactly 2 periods, so the rounding had nothing to do.
    /// #413 tightened the ceiling to 105ms, which is 6 base ticks, which is still served at
    /// 8: the constant changed and the behaviour did not, and the live measurement went on
    /// reading 133ms while the unit test read 105. It was caught only because the two
    /// disagreed — a ceiling change validated by unit tests alone would have shipped as a
    /// no-op.</para>
    /// </remarks>
    public int IntervalTicksFor(float score, int baseHz, int worldEvery)
    {
        int ticks = IntervalTicksFor(score, baseHz);
        if (worldEvery <= 1 || baseHz <= 0) return ticks;

        int worldHz = baseHz / worldEvery;
        if (worldHz <= 0) return ticks;

        int periods = (ticks + worldEvery - 1) / worldEvery;
        while (periods > 1 && periods * 1000 / worldHz > MaxIntervalMs) periods--;
        return periods * worldEvery;
    }

    /// <summary>
    /// The wait an entity actually takes, in milliseconds, for an interval of
    /// <paramref name="baseTicks"/> at these rates — the number the client's budget is spent
    /// against, and the one <see cref="MaxIntervalMs"/> is a bound on.
    /// </summary>
    public static int EffectiveIntervalMs(int baseTicks, int baseHz, int worldEvery)
    {
        if (baseHz <= 0) return 0;
        if (worldEvery <= 1) return baseTicks * 1000 / baseHz;
        int worldHz = baseHz / worldEvery;
        if (worldHz <= 0) return baseTicks * 1000 / baseHz;
        int periods = Math.Max(1, (baseTicks + worldEvery - 1) / worldEvery);
        return periods * 1000 / worldHz;
    }

    /// <summary>
    /// Milliseconds to world ticks: nearest, at least 1, capped at
    /// <see cref="MaxIntervalMs"/>.
    /// </summary>
    /// <remarks>
    /// <b>Nearest, and flooring was tried first and was wrong.</b> Flooring looks like the
    /// conservative choice -- every error lands on the side of less staleness -- but a tick
    /// timeline is coarse, and an interval that is a hair under a whole number of ticks
    /// floors to the tick BELOW it. 133ms at 15Hz is 1.995 ticks, so the middle band floored
    /// to 1 and became "every tick": the tier still appeared in the banner and in
    /// <c>/status</c>, and did nothing. A policy whose middle band silently does not exist is
    /// worse than one that is 0.3ms later than asked, and it was only caught by reading a
    /// running server's <c>replication_schedule</c> line.
    /// </remarks>
    public static int TicksFor(int intervalMs, int worldHz)
    {
        if (intervalMs <= 0 || worldHz <= 0) return 1;
        int ms = Math.Min(intervalMs, MaxIntervalMs);
        int ticks = (int)(((long)ms * worldHz + 500L) / 1000L);

        // The ceiling is on the interval an entity ACTUALLY waits, not on the number
        // someone typed. Rounding 500ms up to 8 ticks at 15Hz would wait 533ms and quietly
        // exceed the bound this constant exists to enforce, so the tick count is floored
        // against it here rather than the millisecond value being clamped above.
        int ceiling = (int)((long)MaxIntervalMs * worldHz / 1000L);
        if (ceiling >= 1 && ticks > ceiling) ticks = ceiling;

        return ticks < 1 ? 1 : ticks;
    }

    /// <summary>
    /// The wait each band actually produces, in milliseconds, at these rates — one entry per
    /// band, in declaration order. What the operator is really configuring.
    /// </summary>
    public int[] EffectiveIntervalsMs(int criticalHz, int worldHz)
    {
        int[] periods = EffectiveIntervalWorldTicks(criticalHz, worldHz);
        var result = new int[periods.Length];
        for (int i = 0; i < periods.Length; i++) result[i] = periods[i] * 1000 / worldHz;
        return result;
    }

    /// <summary>
    /// The wait each band actually produces, in WORLD TICKS — one entry per band, in
    /// declaration order.
    /// </summary>
    /// <remarks>
    /// The tick count is what makes a collapse legible: two bands reading "0ms" and "105ms"
    /// look like a policy, and the same two reading "1 world tick" and "1 world tick" do not.
    /// It is the number the refusal message leads with for that reason.
    /// </remarks>
    public int[] EffectiveIntervalWorldTicks(int criticalHz, int worldHz)
    {
        if (_tiers.Length == 0 || criticalHz <= 0 || worldHz <= 0 || criticalHz % worldHz != 0)
            return Array.Empty<int>();

        int worldEvery = criticalHz / worldHz;
        var result = new int[_tiers.Length];
        for (int i = 0; i < _tiers.Length; i++)
        {
            int baseTicks = IntervalTicksFor(_tiers[i].MinScore, criticalHz, worldEvery);
            result[i] = Math.Max(1, (baseTicks + worldEvery - 1) / worldEvery);
        }
        return result;
    }

    /// <summary>
    /// Whether every band produces the same wait at these rates, i.e. the policy is a no-op
    /// wearing the name of a policy.
    /// </summary>
    /// <remarks>
    /// Computed with the SAME arithmetic the live path uses —
    /// <see cref="IntervalTicksFor(float,int,int)"/> then
    /// <see cref="EffectiveIntervalMs"/> — rather than from the declared millisecond values.
    /// Reading the declared values is how this stayed invisible: the bands say 0 and 105 and
    /// look distinct, while both are served on every world tick at 60/15.
    /// </remarks>
    public bool BandsCollapseAt(int criticalHz, int worldHz)
    {
        int[] effective = EffectiveIntervalsMs(criticalHz, worldHz);
        if (effective.Length < 2) return false;
        for (int i = 1; i < effective.Length; i++)
            if (effective[i] != effective[0]) return false;
        return true;
    }

    /// <summary>
    /// The slowest world rate at or above <paramref name="fromWorldHz"/> that divides
    /// <paramref name="criticalHz"/> and separates the bands, or 0 if none does.
    /// </summary>
    private int SeparatingWorldRate(int criticalHz, int fromWorldHz)
    {
        for (int w = fromWorldHz + 1; w <= criticalHz; w++)
        {
            if (criticalHz % w != 0) continue;
            if (!BandsCollapseAt(criticalHz, w)) return w;
        }
        return 0;
    }

    public static bool TryCreate(
        string? profile, bool importanceEnabled,
        out ReplicationSchedule? schedule, out string? error)
        => TryCreate(profile, importanceEnabled, criticalHz: 0, worldHz: 0, out schedule, out error);

    /// <summary>
    /// Parse and validate, including against the rates the server will actually run at.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the rates belong in the gate.</b> A schedule is a set of waits in
    /// milliseconds and the rates decide which of them exist. At 60/15 the only waits are
    /// 66.7ms and 133.3ms, and with a 105ms ceiling every band rounds to the first — so
    /// <c>tiered</c> parses, prints two bands in the startup banner, and behaves as one.
    /// </para>
    ///
    /// <para>That exact failure is already recorded twice in this file: 133ms flooring to
    /// "every tick" while the tier went on appearing in the banner and in <c>/status</c>, and
    /// a third band at 266ms whose only effect was arriving after the client had stopped
    /// being able to use it. Both were found by reading a running server. A comment did not
    /// stop the second, so this is a refusal: the server will not start rather than start
    /// with a policy that does nothing.</para>
    ///
    /// <para><paramref name="criticalHz"/> and <paramref name="worldHz"/> are the raw
    /// configured values, read before <see cref="SimulationRates"/> has validated them. The
    /// check is skipped when they are unusable, so a bad rate is reported by the rate
    /// validator rather than by a confusing message from here.</para>
    /// </remarks>
    public static bool TryCreate(
        string? profile, bool importanceEnabled, int criticalHz, int worldHz,
        out ReplicationSchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;

        string name = string.IsNullOrWhiteSpace(profile) ? "off" : profile.Trim().ToLowerInvariant();
        switch (name)
        {
            case "off":
                schedule = Off;
                return true;

            case "tiered":
                if (!importanceEnabled)
                {
                    // Refused rather than degraded. With every weight zero every score is
                    // zero, so every entity lands in the bottom band and the schedule
                    // becomes "defer everything equally" -- a uniform 266ms downgrade for
                    // every entity in the world, which is the opposite of what the operator
                    // asked for and looks from the outside like the feature working.
                    error = $"{EnvVar}=tiered needs importance weights, but " +
                            $"{ImportanceSettings.EnvVar} is legacy (every factor zero). " +
                            "Every score would be zero, every entity would land in the " +
                            "slowest band, and the result would be a uniform staleness " +
                            "increase rather than a policy. Set " +
                            $"{ImportanceSettings.EnvVar}=balanced.";
                    return false;
                }
                if (criticalHz > 0 && worldHz > 0 && criticalHz % worldHz == 0
                    && Tiered.BandsCollapseAt(criticalHz, worldHz))
                {
                    int[] effective = Tiered.EffectiveIntervalsMs(criticalHz, worldHz);
                    int separating = Tiered.SeparatingWorldRate(criticalHz, worldHz);
                    int[] worldTicks = Tiered.EffectiveIntervalWorldTicks(criticalHz, worldHz);
                    error =
                        $"{EnvVar}=tiered has no usable band at {criticalHz}/{worldHz}. " +
                        $"Configured intervals {string.Join("ms, ", Tiered.Tiers.Select(t => t.IntervalMs))}ms " +
                        $"all resolve to {string.Join(", ", worldTicks)} world ticks — the same " +
                        $"{effective[0]}ms wait for every band — because snapshots are " +
                        $"emitted every {1000.0 / worldHz:F1}ms and the ceiling is " +
                        $"{MaxIntervalMs}ms ({ClientInterpolationBudgetMs}ms of client cover " +
                        $"minus {LinkSpreadAllowanceMs}ms reserved for the link). The policy " +
                        "would print two bands and behave as one, which is the failure this " +
                        "refusal exists to prevent. " +
                        (separating > 0
                            ? $"Raise SIM_WORLD_HZ to {separating} to separate them, or set " +
                              $"{EnvVar}=off."
                            : $"No world rate dividing {criticalHz} separates them; set {EnvVar}=off.");
                    return false;
                }

                schedule = Tiered;
                return true;

            default:
                error = $"{EnvVar}={profile} is not a known schedule. Want \"off\" (every " +
                        "dirty entity due every world tick) or \"tiered\".";
                return false;
        }
    }

    public string Describe(int worldHz)
    {
        if (!Enabled) return "off — every dirty entity due every world tick";

        var sb = new StringBuilder(Profile);
        sb.Append(" (");
        for (int i = 0; i < _tiers.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            Tier t = _tiers[i];
            if (float.IsNegativeInfinity(t.MinScore)) sb.Append("else");
            else sb.Append("score>=").Append(t.MinScore.ToString("0.##", CultureInfo.InvariantCulture));
            int ticks = TicksFor(t.IntervalMs, worldHz);
            sb.Append(ticks <= 1 ? ": every tick" : $": {t.IntervalMs}ms = {ticks} ticks");
        }
        sb.Append(')');
        return sb.ToString();
    }

    public override string ToString() => Describe(SimulationRates.DefaultWorldHz);
}
