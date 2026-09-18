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
    /// not-due entity is not a shed entity. 500ms is also the point past which the client's
    /// 100ms interpolation buffer plus 50ms extrapolation stops covering the gap at all.
    /// </remarks>
    public const int MaxIntervalMs = 500;

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
    /// <item><description><b>score ≥ 3 → 133ms.</b> A player at any distance, or a nearby
    /// mob. Two world ticks at 15Hz: one held frame, then a catch-up.</description></item>
    /// <item><description><b>otherwise → 266ms.</b> A distant mob that is merely moving.
    /// Four world ticks at 15Hz, which is the band BENCHMARK.md Part XIII measured at
    /// 44-46% on a realistic population.</description></item>
    /// </list>
    /// </remarks>
    public static ReplicationSchedule Tiered { get; } = new("tiered", new[]
    {
        new Tier(8f, 0),
        new Tier(3f, 133),
        new Tier(float.NegativeInfinity, 266),
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

    public static bool TryCreate(
        string? profile, bool importanceEnabled,
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
