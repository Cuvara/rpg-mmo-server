using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace GameServer.Input;

/// <summary>
/// Audits the rate of ACCEPTED attacks per account against what the cooldown permits.
/// <b>It observes and records. It never acts on a player.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not already covered by the per-attack cooldown check.</b>
/// <see cref="Shared.GameLogic.Systems.CombatLogic.ValidateAttack"/> compares the current
/// simulation tick against <c>CooldownUntilTick</c>, which lives on the attacker's ENTITY.
/// That makes the check exact for one entity and structurally blind to anything that gives
/// an account a different one: a fresh entity starts at <c>CooldownUntilTick = 0</c>, and
/// <see cref="Persistence.PlayerState"/> persists <c>UserId, X, Y, Hp, MaxHp, MapId</c> and
/// <b>no cooldown</b>, so nothing carries it across.
/// </para>
/// <para>
/// <b>No live exploit is claimed, and the difference matters.</b> The obvious route --
/// disconnect and rejoin -- does NOT work today: a reconnect inside the hold window
/// reattaches the SAME entity (<c>Server/GameServer.cs</c>, "Acquire or reattach entity"),
/// so the cooldown survives. The two paths that do produce a fresh entity are a map
/// transfer, which removes the entity with no hold, and an absence longer than the hold
/// TTL. Both cost a round trip through another server or 30 seconds, which is far slower
/// than the 500 ms cooldown they would be resetting, so neither multiplies anyone's attack
/// rate in practice. <b>This audit is therefore defence in depth on a path that is
/// currently closed, not a patch for an open hole</b> -- and the reason to have it anyway
/// is in the next paragraph.
/// </para>
/// <para>
/// <b>Why the accepted path and not the rejected one.</b> Refused attacks are already
/// counted per reason and scored per account by <see cref="InputRejectionReason.AttackOnCooldown"/>
/// and <see cref="InputAnomalyTracker"/> (roadmap A1/A2). A cheater who exceeds the rate
/// through a hole generates <b>no rejections at all</b>, so the rejection telemetry reads
/// exactly like an honest player. That blind spot is what this closes.
/// </para>
/// <para>
/// <b>Everything here is measured in SIMULATION TICKS, never wall-clock.</b> The cooldown it
/// is auditing against is tick-based, so a wall-clock window would compare two different
/// clocks and drift against the one that matters; this host's <c>CLOCK_REALTIME</c> also runs
/// 10-17% fast and has been observed stepping backwards (#153). The only wall-clock use is
/// evicting idle accounts, where a wrong answer costs memory and nothing else.
/// </para>
/// <para>
/// <b>It does not act.</b> Same reason as <see cref="InputAnomalyTracker"/>: no threshold
/// here has a measured false-positive rate against real players, and a detector that kicks
/// before anyone has seen its false-positive curve gets switched off after the first bad
/// night, taking the telemetry with it.
/// </para>
/// </remarks>
public sealed class AttackRateAudit
{
    /// <summary>
    /// Window length. Long enough that one honest burst cannot fill it, short enough that a
    /// sustained doubling is flagged within seconds rather than minutes.
    /// </summary>
    public const double DefaultWindowSeconds = 10.0;

    /// <summary>
    /// Extra accepted attacks tolerated inside the window before flagging.
    /// </summary>
    /// <remarks>
    /// <b>Not a fudge factor for the boundary — the window here is a true sliding one and has
    /// no boundary artefact to absorb.</b> It exists because the permitted count is a
    /// division of two integers (<see cref="Shared.GameLogic.Components.GameConstants.AttackCooldownTicks"/>
    /// rounds UP), so the exact permitted rate is slightly below the integer bound, and a
    /// player attacking perfectly on cooldown for the whole window would otherwise sit one
    /// attack from a flag through no fault of their own.
    /// </remarks>
    public const int DefaultSlack = 2;

    /// <summary>Accounts tracked before new ones are dropped.</summary>
    public const int DefaultMaxTrackedAccounts = 4096;

    private static readonly TimeSpan IdleBeforeEviction = TimeSpan.FromHours(1);
    private const int SweepEveryRecords = 4096;

    private sealed class AccountState
    {
        /// <summary>
        /// Ring of the ticks at which the last <c>_permitted</c> attacks were accepted.
        /// Fixed size and allocated once per account: this runs on the tick thread inside
        /// the world write lock, which is a no-allocation path.
        /// </summary>
        public ulong[] Ticks = Array.Empty<ulong>();

        /// <summary>Next write index into <see cref="Ticks"/>.</summary>
        public int Next;

        /// <summary>Accepted attacks seen for this account, all time.</summary>
        public long Accepted;

        /// <summary>How many times this account has been flagged.</summary>
        public long Violations;

        /// <summary>Whether the account is currently over the rate, so each crossing flags once.</summary>
        public bool Over;

        public long LastSeenTicks;
    }

    private readonly ConcurrentDictionary<string, AccountState> _accounts = new(StringComparer.Ordinal);
    private readonly int _maxAccounts;
    private readonly int _windowTicks;
    private readonly int _permitted;
    private long _violations;
    private long _droppedAccounts;
    private int _recordsSinceSweep;

    /// <param name="tickRate">Base simulation ticks per second.</param>
    /// <param name="cooldownTicks">
    /// Ticks one attack puts an entity on cooldown for — the same value
    /// <see cref="InputHandler"/> writes to <c>CooldownUntilTick</c>.
    /// </param>
    public AttackRateAudit(
        int tickRate,
        int cooldownTicks,
        double windowSeconds = DefaultWindowSeconds,
        int slack = DefaultSlack,
        int maxAccounts = DefaultMaxTrackedAccounts)
    {
        if (tickRate <= 0) throw new ArgumentOutOfRangeException(nameof(tickRate));
        if (cooldownTicks <= 0) throw new ArgumentOutOfRangeException(nameof(cooldownTicks));

        if (windowSeconds <= 0) windowSeconds = DefaultWindowSeconds;
        if (slack < 0) slack = DefaultSlack;
        _maxAccounts = maxAccounts > 0 ? maxAccounts : DefaultMaxTrackedAccounts;

        _windowTicks = (int)Math.Ceiling(windowSeconds * tickRate);
        if (_windowTicks < 1) _windowTicks = 1;

        // How many attacks one entity can land in the window if it attacks the instant the
        // cooldown expires, every time: the first is free, then one per cooldown.
        _permitted = 1 + _windowTicks / cooldownTicks + slack;
    }

    /// <summary>Attacks permitted inside the window before a flag. Exposed for tests and docs.</summary>
    public int Permitted => _permitted;

    /// <summary>Window length in simulation ticks.</summary>
    public int WindowTicks => _windowTicks;

    /// <summary>Accounts currently tracked.</summary>
    public int TrackedAccounts => _accounts.Count;

    /// <summary>Total flags raised across all accounts.</summary>
    public long Violations => Interlocked.Read(ref _violations);

    /// <summary>
    /// Observations discarded because <see cref="DefaultMaxTrackedAccounts"/> was reached.
    /// Non-zero means the audit is no longer seeing the whole population.
    /// </summary>
    public long DroppedAccounts => Interlocked.Read(ref _droppedAccounts);

    /// <summary>
    /// Record one ACCEPTED attack. Returns true when this attack took the account over the
    /// permitted rate and it was not already over — so a sustained violation flags on the
    /// crossing, not once per attack.
    /// </summary>
    /// <param name="userId">The ACCOUNT, not the entity or the connection: that is the point.</param>
    /// <param name="currentTick">Base simulation tick the attack was accepted on.</param>
    public bool RecordAccepted(string userId, ulong currentTick)
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
                Ticks = new ulong[_permitted],
                LastSeenTicks = Stopwatch.GetTimestamp(),
            });
        }

        if (Interlocked.Increment(ref _recordsSinceSweep) >= SweepEveryRecords)
        {
            Interlocked.Exchange(ref _recordsSinceSweep, 0);
            EvictIdle(IdleBeforeEviction);
        }

        lock (state)
        {
            state.Accepted++;
            state.LastSeenTicks = Stopwatch.GetTimestamp();

            // The ring holds the last _permitted accepted ticks. The slot about to be
            // overwritten is therefore the _permitted-th attack back. If that one is still
            // inside the window, this attack is the (_permitted + 1)-th in the window.
            //
            // A true sliding window, not a tumbling one: a tumbling window lets a client
            // land 2x the permitted rate across a boundary and calls it compliant, which
            // is exactly the rate a rejoin-reset produces.
            ulong oldest = state.Ticks[state.Next];
            bool ringFull = state.Accepted > _permitted;

            state.Ticks[state.Next] = currentTick;
            state.Next = (state.Next + 1) % _permitted;

            // Ticks never go backwards within a run, but a server restart resets the
            // simulation clock while this audit's memory of an account does not survive it
            // either -- so the only way to see currentTick < oldest is a caller bug. Treat
            // it as "not in the window" rather than underflowing.
            bool insideWindow = ringFull
                && currentTick >= oldest
                && currentTick - oldest < (ulong)_windowTicks;

            if (!insideWindow)
            {
                state.Over = false;
                return false;
            }

            if (state.Over) return false;   // already flagged this crossing

            state.Over = true;
            state.Violations++;
            Interlocked.Increment(ref _violations);
            return true;
        }
    }

    /// <summary>Accepted attacks recorded for one account. Test and diagnostic use.</summary>
    public long AcceptedFor(string userId)
    {
        if (!_accounts.TryGetValue(userId, out AccountState? s)) return 0;
        lock (s) return s.Accepted;
    }

    /// <summary>Flags raised for one account. Test and diagnostic use.</summary>
    public long ViolationsFor(string userId)
    {
        if (!_accounts.TryGetValue(userId, out AccountState? s)) return 0;
        lock (s) return s.Violations;
    }

    private void EvictIdle(TimeSpan idle)
    {
        long cutoff = Stopwatch.GetTimestamp() - (long)(idle.TotalSeconds * Stopwatch.Frequency);
        foreach (var kv in _accounts)
        {
            long lastSeen;
            lock (kv.Value) lastSeen = kv.Value.LastSeenTicks;
            if (lastSeen < cutoff)
                _accounts.TryRemove(kv.Key, out _);
        }
    }
}
