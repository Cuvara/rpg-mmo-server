using GameServer.Input;
using GameServer.Observability;
using Xunit;

namespace GameServer.Tests.Input;

/// <summary>
/// Roadmap A1/A2: counting refused input, and scoring it per account.
/// </summary>
/// <remarks>
/// <para>
/// The thing worth testing is not that a counter increments. It is the two properties the
/// design rests on, both of which are silent when broken:
/// </para>
/// <para>
/// 1. <b>Every reason series exists at zero.</b> These are alarm counters whose healthy
/// reading is nothing at all, and an absent series reads identically to a broken scrape or
/// a build without the feature (<c>docs/METRICS.md</c>).
/// </para>
/// <para>
/// 2. <b>A laggy honest player does not look like a cheater.</b> Latency-explicable
/// rejections must not accumulate into an alert at any rate an honest client plausibly
/// produces, while forged input must. Get that backwards and the detector ranks the
/// worst-connected players as the most suspicious.
/// </para>
/// </remarks>
public class InputRejectionTelemetryTests
{
    // ---- The reason taxonomy ------------------------------------------------

    /// <summary>
    /// Every reason must have a distinct, stable, snake_case label. These are metric label
    /// values and part of the monitoring contract — a duplicate would silently merge two
    /// causes into one series.
    /// </summary>
    [Fact]
    public void EveryReasonHasAUniqueLabel()
    {
        var labels = InputRejection.All.Select(InputRejection.Label).ToList();

        Assert.Equal(labels.Count, labels.Distinct().Count());
        Assert.All(labels, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l));
            Assert.Equal(l.ToLowerInvariant(), l);
            Assert.DoesNotContain(' ', l);
            Assert.NotEqual("unknown", l);
        });
    }

    /// <summary>
    /// <see cref="InputRejection.All"/> must list every enum member, because it is what
    /// primes the metric series and what enumerates the <c>/status</c> breakdown. A reason
    /// missing from it would be counted but never published at zero — invisible until it
    /// fired, which is the exact failure this design exists to avoid.
    /// </summary>
    [Fact]
    public void AllContainsEveryEnumMember()
    {
        var declared = Enum.GetValues<InputRejectionReason>();
        Assert.Equal(declared.Length, InputRejection.All.Length);
        Assert.All(declared, r => Assert.Contains(r, InputRejection.All));
    }

    /// <summary>
    /// Only forged input may carry full weight. If a latency-explicable reason is ever
    /// reclassified upward, a player on a bad connection starts accruing suspicion — so
    /// this pins the classification itself, not just its use.
    /// </summary>
    [Fact]
    public void OnlyInvalidDirectionIsClassifiedAsForged()
    {
        var forged = InputRejection.All
            .Where(r => InputRejection.Weight(r) == RejectionWeight.Forged)
            .ToList();

        Assert.Equal(new[] { InputRejectionReason.InvalidDirection }, forged);
    }

    /// <summary>An unclassified validator reason must never manufacture suspicion.</summary>
    [Fact]
    public void UnclassifiedAttackReasonIsBenign()
    {
        Assert.Equal(RejectionWeight.Benign, InputRejection.Weight(InputRejectionReason.AttackOther));
    }

    // ---- The metric surface -------------------------------------------------

    /// <summary>
    /// Every reason reads zero before anything is recorded — the property that makes
    /// "no rejections" distinguishable from "no telemetry".
    /// </summary>
    [Fact]
    public void EveryReasonReadsZeroBeforeAnythingHappens()
    {
        using var metrics = new GameMetrics("map_test", $"test.{Guid.NewGuid():N}");

        Assert.Equal(0, metrics.InputsRejectedTotal);
        Assert.All(InputRejection.All, r => Assert.Equal(0, metrics.InputsRejected(r)));
    }

    [Fact]
    public void RecordingIncrementsOnlyThatReason()
    {
        using var metrics = new GameMetrics("map_test", $"test.{Guid.NewGuid():N}");

        metrics.RecordInputRejected(InputRejectionReason.InvalidDirection);
        metrics.RecordInputRejected(InputRejectionReason.InvalidDirection);
        metrics.RecordInputRejected(InputRejectionReason.AttackOutOfRange);

        Assert.Equal(2, metrics.InputsRejected(InputRejectionReason.InvalidDirection));
        Assert.Equal(1, metrics.InputsRejected(InputRejectionReason.AttackOutOfRange));
        Assert.Equal(0, metrics.InputsRejected(InputRejectionReason.StaleTick));
        Assert.Equal(3, metrics.InputsRejectedTotal);
    }

    // ---- The anomaly budget -------------------------------------------------

    private static InputAnomalyTracker Tracker(double alertScore = 5.0) =>
        new(halfLifeSeconds: 60.0, maxAccounts: 16, alertScore: alertScore);

    /// <summary>
    /// Benign reasons must never move the score. A player dying repeatedly, or racing the
    /// entity-removal path, is not evidence of anything.
    /// </summary>
    [Fact]
    public void BenignRejectionsNeverRaiseTheScore()
    {
        var tracker = Tracker();

        for (int i = 0; i < 1000; i++)
        {
            Assert.False(tracker.Record("u1", InputRejectionReason.DeadEntity));
            Assert.False(tracker.Record("u1", InputRejectionReason.EntityGone));
        }

        Assert.Equal(0, tracker.AccountsOverThreshold());

        // Counted for visibility even though they score nothing — the operator still needs
        // to see them, which is the difference between "not suspicious" and "not recorded".
        var top = tracker.TopByScore(10);
        Assert.Single(top);
        Assert.Equal(2000, top[0].Total);
        Assert.Equal(0.0, top[0].Score);
    }

    /// <summary>
    /// <b>The false-positive test.</b> A latency-explicable rejection on every single one
    /// of 200 inputs — far worse than any plausible honest connection — must still not
    /// raise an alert.
    /// </summary>
    [Fact]
    public void ASustainedlyLaggyPlayerDoesNotAlert()
    {
        var tracker = Tracker(alertScore: InputAnomalyTracker.DefaultAlertScore);

        for (int i = 0; i < 200; i++)
        {
            Assert.False(
                tracker.Record("laggy", InputRejectionReason.AttackOutOfRange),
                "a latency-explicable reason must not alert on its own");
        }

        Assert.Equal(0, tracker.AccountsOverThreshold());
    }

    /// <summary>
    /// Forged input, by contrast, must reach the threshold — and an order of magnitude
    /// sooner than a latency-explicable reason would.
    /// </summary>
    [Fact]
    public void ForgedInputReachesTheThreshold()
    {
        var tracker = Tracker(alertScore: InputAnomalyTracker.DefaultAlertScore);

        bool alerted = false;
        for (int i = 0; i < 200 && !alerted; i++)
            alerted = tracker.Record("forger", InputRejectionReason.InvalidDirection);

        Assert.True(alerted, "sustained forged input must cross the threshold");
        Assert.Equal(1, tracker.AccountsOverThreshold());
    }

    /// <summary>The alert fires once per crossing, not once per rejection after it.</summary>
    [Fact]
    public void AlertFiresOncePerCrossing()
    {
        var tracker = Tracker(alertScore: 3.0);

        int alerts = 0;
        for (int i = 0; i < 100; i++)
            if (tracker.Record("u1", InputRejectionReason.InvalidDirection)) alerts++;

        Assert.Equal(1, alerts);
    }

    /// <summary>
    /// The score is per ACCOUNT, so it is unaffected by reconnecting. This is the whole
    /// difference from the existing per-connection input budget, which resets on a new
    /// socket and therefore counts reconnects rather than behaviour.
    /// </summary>
    [Fact]
    public void ScoreSurvivesAcrossSessionsBecauseItIsKeyedByAccount()
    {
        var tracker = Tracker(alertScore: 1000.0);   // high, so nothing alerts here

        // "Session one".
        for (int i = 0; i < 3; i++) tracker.Record("u1", InputRejectionReason.InvalidDirection);
        double afterFirstSession = tracker.TopByScore(1)[0].Score;

        // "Session two" — a reconnect. Same account id, so the same entry.
        for (int i = 0; i < 3; i++) tracker.Record("u1", InputRejectionReason.InvalidDirection);
        var after = tracker.TopByScore(1)[0];

        Assert.Equal(1, tracker.TrackedAccounts);
        Assert.Equal(6, after.Total);
        Assert.True(after.Score > afterFirstSession,
            "a reconnect must accumulate onto the account's existing score, not reset it");

        // Deliberately NOT asserting an exact score. The score decays continuously, so any
        // equality against a computed constant would be a test of how fast the test ran —
        // the earlier version of this test balanced on the threshold boundary and failed
        // for exactly that reason.
    }

    /// <summary>Accounts are independent; one player's behaviour cannot implicate another.</summary>
    [Fact]
    public void AccountsAreScoredIndependently()
    {
        var tracker = Tracker(alertScore: 3.0);

        for (int i = 0; i < 10; i++) tracker.Record("noisy", InputRejectionReason.InvalidDirection);
        tracker.Record("quiet", InputRejectionReason.AttackOnCooldown);

        var top = tracker.TopByScore(10);
        Assert.Equal("noisy", top[0].UserId);
        Assert.Equal(1, tracker.AccountsOverThreshold());
    }

    /// <summary>
    /// Ordered by score rather than by raw count, so the worst-connected honest player does
    /// not head a list an operator reads as "most suspicious".
    /// </summary>
    [Fact]
    public void TopAccountsAreOrderedByScoreNotByRawCount()
    {
        var tracker = Tracker();

        // Many benign rejections: a high count, no suspicion.
        for (int i = 0; i < 500; i++) tracker.Record("laggy", InputRejectionReason.AttackOutOfRange);
        // A few forged ones: a low count, real suspicion.
        for (int i = 0; i < 3; i++) tracker.Record("forger", InputRejectionReason.InvalidDirection);

        var top = tracker.TopByScore(10);

        Assert.Equal("forger", top[0].UserId);
        Assert.True(top[0].Total < top[1].Total,
            "the more suspicious account has FEWER rejections — which is the point");
    }

    /// <summary>
    /// The account cap is a hard memory bound, and hitting it must be visible rather than
    /// silently losing observations.
    /// </summary>
    [Fact]
    public void AccountCapIsEnforcedAndCounted()
    {
        var tracker = new InputAnomalyTracker(maxAccounts: 4);

        for (int i = 0; i < 10; i++)
            tracker.Record($"u{i}", InputRejectionReason.InvalidDirection);

        Assert.Equal(4, tracker.TrackedAccounts);
        Assert.Equal(6, tracker.DroppedAccounts);
    }

    [Fact]
    public void EmptyUserIdIsIgnoredRatherThanTracked()
    {
        var tracker = Tracker();
        Assert.False(tracker.Record("", InputRejectionReason.InvalidDirection));
        Assert.Equal(0, tracker.TrackedAccounts);
    }

    /// <summary>Idle accounts are evicted, so the map does not grow without bound.</summary>
    [Fact]
    public void IdleAccountsAreEvicted()
    {
        var tracker = Tracker();
        tracker.Record("u1", InputRejectionReason.InvalidDirection);
        Assert.Equal(1, tracker.TrackedAccounts);

        Assert.Equal(1, tracker.EvictIdle(TimeSpan.Zero));
        Assert.Equal(0, tracker.TrackedAccounts);
    }
}
