using System.Collections.Concurrent;
using System.Diagnostics;

namespace GameServer.Input;

/// <summary>
/// Counts refused input per account and maintains a decaying anomaly score over time.
/// <b>It observes and records. It never acts on a player.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Per ACCOUNT, not per connection.</b> A per-connection counter resets when the
/// cheater reconnects, which makes it a counter of how often they reconnect. Keyed on the
/// user id from the join token, this survives a reconnect and the reconnect hold window.
/// </para>
/// <para>
/// <b>Why it does not act.</b> Roadmap A2 lists log / flag / rate-limit / kick as the
/// options. This implements FLAG-AND-RECORD only, for a reason that is not timidity: the
/// false-positive rate of any threshold here is currently <b>unmeasured</b>, and most
/// rejection reasons rise with a player's latency (see <see cref="RejectionWeight"/>). A
/// detector that kicks before anyone has seen its false-positive curve gets switched off
/// after the first bad night, and then the telemetry goes with it. Recording first makes
/// the threshold choosable from evidence instead of invented; the enforcement step is a
/// later, smaller change once there is a baseline to choose against.
/// </para>
/// <para>
/// <b>Decay, not a fixed window.</b> The signal wanted is "sustained abnormality", and the
/// thing that must NOT trigger is a burst — a lag spike, a death, a zone full of
/// despawning mobs. An exponentially decaying score has that shape with one float per
/// account and no ring buffer: a burst decays away, a sustained rate converges on a
/// plateau proportional to it. It is also cheap enough to update on the tick thread.
/// </para>
/// <para>
/// <b>Threading.</b> Scores are updated from the tick thread and read by the status
/// endpoint. Unlike <c>AttackTelemetry</c> — plain <c>long</c> fields, written every tick,
/// read racily on purpose — this holds a map, and a map cannot be read while it is being
/// written. A <see cref="ConcurrentDictionary{TKey,TValue}"/> is affordable precisely
/// because rejections are rare by definition: the hot path pays nothing when nothing is
/// refused.
/// </para>
/// </remarks>
public sealed class InputAnomalyTracker
{
    /// <summary>
    /// Half-life of the anomaly score, in seconds. A burst of forged input decays to half
    /// its contribution after this long, so a one-off spike fades within a minute or two
    /// while a client sustaining the same rate holds a steady elevated score.
    /// </summary>
    public const double DefaultHalfLifeSeconds = 60.0;

    /// <summary>
    /// Accounts tracked before the tracker stops admitting new ones.
    /// </summary>
    /// <remarks>
    /// This is a memory bound, not a policy. Entries are keyed by account and outlive the
    /// connection deliberately, so without a cap a long-lived server accumulates one entry
    /// per account ever seen. At the cap, existing accounts keep updating and new ones are
    /// dropped — losing observations on a NEW account is strictly better than an unbounded
    /// map on a server whose whole point is to keep ticking.
    /// </remarks>
    public const int DefaultMaxTrackedAccounts = 4096;

    private sealed class AccountState
    {
        public readonly long[] ByReason = new long[InputRejection.All.Length];
        public long Total;

        /// <summary>Decayed suspicion score. Only weighted reasons contribute.</summary>
        public double Score;

        /// <summary>Timestamp the score was last decayed to, in stopwatch ticks.</summary>
        public long ScoreAtTicks;

        /// <summary>Last time anything was recorded, for idle eviction.</summary>
        public long LastSeenTicks;

        /// <summary>Times this account has crossed the alert threshold.</summary>
        public long Alerts;
    }

    private readonly ConcurrentDictionary<string, AccountState> _accounts = new();
    private readonly double _halfLifeSeconds;
    private readonly int _maxAccounts;
    private readonly double _alertScore;

    private long _droppedAccounts;
    private long _recordsSinceSweep;

    /// <summary>
    /// Records between idle sweeps, and how long an account may sit idle before it is
    /// dropped.
    /// </summary>
    /// <remarks>
    /// Swept lazily from <see cref="Record"/> rather than from a timer, because the map
    /// only ever GROWS in <see cref="Record"/> — so cleaning up there is sufficient by
    /// construction, and it avoids adding a background task and its shutdown handling for
    /// a map that is empty on a healthy server anyway.
    /// </remarks>
    private const int SweepEveryRecords = 1024;
    private static readonly TimeSpan IdleBeforeEviction = TimeSpan.FromHours(1);

    /// <summary>
    /// The score at or above which an account is considered anomalous.
    /// </summary>
    /// <remarks>
    /// <b>This default is a placeholder, and saying so is the point.</b> There is no
    /// measured baseline for an honest player's rejection rate on a real network — every
    /// figure this repository has is from loopback, where the latency that produces most
    /// rejections does not exist. The default is set so that a client sustaining forged
    /// input is flagged and ordinary play is not, on the evidence available; it is not a
    /// tuned number and must not be treated as one until the baseline in
    /// <c>docs/METRICS.md</c> has been measured against real players.
    /// </remarks>
    public const double DefaultAlertScore = 20.0;

    public InputAnomalyTracker(
        double halfLifeSeconds = DefaultHalfLifeSeconds,
        int maxAccounts = DefaultMaxTrackedAccounts,
        double alertScore = DefaultAlertScore)
    {
        _halfLifeSeconds = halfLifeSeconds > 0 ? halfLifeSeconds : DefaultHalfLifeSeconds;
        _maxAccounts = maxAccounts > 0 ? maxAccounts : DefaultMaxTrackedAccounts;
        _alertScore = alertScore > 0 ? alertScore : DefaultAlertScore;
    }

    /// <summary>Accounts currently tracked.</summary>
    public int TrackedAccounts => _accounts.Count;

    /// <summary>
    /// Observations discarded because <see cref="DefaultMaxTrackedAccounts"/> was reached.
    /// Non-zero means the tracker is no longer seeing the whole population.
    /// </summary>
    public long DroppedAccounts => Interlocked.Read(ref _droppedAccounts);

    /// <summary>
    /// Record one refused input. Returns true if this observation pushed the account to or
    /// past the alert threshold for the first time in this decay window.
    /// </summary>
    /// <remarks>
    /// Called from the tick thread at the rejection site. Returns a bool rather than
    /// raising an event so the caller decides what to do with it — today, count it.
    /// </remarks>
    public bool Record(string userId, InputRejectionReason reason)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        if (!_accounts.TryGetValue(userId, out AccountState? state))
        {
            if (_accounts.Count >= _maxAccounts)
            {
                Interlocked.Increment(ref _droppedAccounts);
                return false;
            }

            state = _accounts.GetOrAdd(userId, _ => new AccountState
            {
                ScoreAtTicks = Stopwatch.GetTimestamp(),
            });
        }

        if (Interlocked.Increment(ref _recordsSinceSweep) >= SweepEveryRecords)
        {
            Interlocked.Exchange(ref _recordsSinceSweep, 0);
            EvictIdle(IdleBeforeEviction);
        }

        long now = Stopwatch.GetTimestamp();

        lock (state)
        {
            state.ByReason[(int)reason]++;
            state.Total++;
            state.LastSeenTicks = now;

            double weight = Contribution(reason);
            if (weight <= 0) return false;

            bool wasBelow = Decay(state, now) < _alertScore;
            state.Score += weight;

            if (wasBelow && state.Score >= _alertScore)
            {
                state.Alerts++;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What one rejection of this reason adds to the score.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only forged input scores. Everything else contributes exactly zero</b> — and
    /// that is a deliberate narrowing, not an oversight.
    /// </para>
    /// <para>
    /// The first version of this gave latency-explicable reasons a weight of 0.1, on the
    /// reasoning that an extraordinary rate of them should eventually register. Its own
    /// test disproved it, and the measured reason is worth recording exactly, because the
    /// obvious explanation is the wrong one:
    /// </para>
    /// <para>
    /// At that weight, a player producing an out-of-range attack on every one of 200
    /// inputs lands <i>within a rounding artefact</i> of the default threshold of 20.
    /// A plain sum of 0.1 two hundred times is <c>20.000000000000014</c> — it
    /// <b>overshoots</b>, and on its own would have alerted. What put the real score under
    /// the line is that <see cref="Decay"/> runs before every addition, so the running
    /// total is shaved continuously and never quite reaches <c>200 x weight</c>: measured
    /// on this machine, <c>19.999998878470578</c>, short by <c>1.1e-06</c>.
    /// </para>
    /// <para>
    /// That shortfall is a function of <b>how fast the machine ran the loop</b> — slower
    /// hardware decays more between records and falls further short; faster hardware
    /// converges on the plain sum, which is over the line. So whether a maximally laggy
    /// player was flagged depended on the host's speed. That is not a threshold, it is a
    /// coin flip, and it is the concrete demonstration of the general point: a weight
    /// small enough to be safe and large enough to matter cannot be chosen without knowing
    /// the honest baseline, and <b>that baseline has never been measured</b> — every
    /// figure this repository has is from loopback, where the latency producing those
    /// rejections does not exist.
    /// </para>
    /// <para>
    /// So the score answers one question it can answer honestly: <i>how much input is this
    /// account sending that the shipped client cannot produce?</i> The latency-explicable
    /// reasons are still counted and still published per account on <c>/status</c> — they
    /// are just not evidence, and the score does not pretend they are. When a real
    /// baseline exists they can be given a weight chosen from it.
    /// </para>
    /// </remarks>
    private static double Contribution(InputRejectionReason reason) =>
        InputRejection.Weight(reason) switch
        {
            RejectionWeight.Benign => 0.0,

            // Zero until there is a measured honest baseline to pick a weight from.
            // Counted and visible, but never scored.
            RejectionWeight.LatencyExplicable => 0.0,

            RejectionWeight.Forged => 1.0,
            _ => 0.0,
        };

    /// <summary>Decay the score to <paramref name="now"/> and return it. Caller holds the lock.</summary>
    private double Decay(AccountState state, long now)
    {
        double elapsed = (now - state.ScoreAtTicks) / (double)Stopwatch.Frequency;
        state.ScoreAtTicks = now;

        if (elapsed > 0 && state.Score > 0)
        {
            state.Score *= Math.Pow(0.5, elapsed / _halfLifeSeconds);
            if (state.Score < 1e-6) state.Score = 0;
        }

        return state.Score;
    }

    /// <summary>
    /// Drop accounts idle for longer than <paramref name="idle"/>. Call periodically; the
    /// map is keyed by account and so does not shrink when a player disconnects.
    /// </summary>
    public int EvictIdle(TimeSpan idle)
    {
        long cutoff = Stopwatch.GetTimestamp() - (long)(idle.TotalSeconds * Stopwatch.Frequency);
        int removed = 0;

        foreach (var kv in _accounts)
        {
            AccountState s = kv.Value;
            bool stale;
            lock (s) { stale = s.LastSeenTicks < cutoff; }
            if (stale && _accounts.TryRemove(kv.Key, out _)) removed++;
        }

        return removed;
    }

    /// <summary>One account's observed totals, for the status endpoint.</summary>
    public readonly record struct AccountSnapshot(
        string UserId, long Total, double Score, long Alerts, long[] ByReason);

    /// <summary>
    /// The <paramref name="limit"/> accounts with the highest current score.
    /// </summary>
    /// <remarks>
    /// Ordered by SCORE and not by raw count, deliberately: the account with the most
    /// rejections is usually the one with the worst connection, and putting that player at
    /// the top of a list an operator reads as "most suspicious" is how a latency problem
    /// gets mistaken for cheating.
    /// </remarks>
    public List<AccountSnapshot> TopByScore(int limit)
    {
        long now = Stopwatch.GetTimestamp();
        var all = new List<AccountSnapshot>(_accounts.Count);

        foreach (var kv in _accounts)
        {
            AccountState s = kv.Value;
            lock (s)
            {
                double score = Decay(s, now);
                if (s.Total == 0) continue;
                all.Add(new AccountSnapshot(kv.Key, s.Total, score, s.Alerts, (long[])s.ByReason.Clone()));
            }
        }

        all.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (all.Count > limit) all.RemoveRange(limit, all.Count - limit);
        return all;
    }

    /// <summary>Accounts currently at or above the alert threshold.</summary>
    public int AccountsOverThreshold()
    {
        long now = Stopwatch.GetTimestamp();
        int n = 0;

        foreach (var kv in _accounts)
        {
            AccountState s = kv.Value;
            lock (s) { if (Decay(s, now) >= _alertScore) n++; }
        }

        return n;
    }
}
