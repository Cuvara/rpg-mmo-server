using System.Runtime.InteropServices;
using Google.Protobuf;
using GameServer.Net;
using GameServer.World;
using Shared.GameLogic.Components;
// Both layers legitimately have an EntityAction: the generated wire enum and the shared
// simulation one, which mirror each other by design. Aliased rather than resolved by
// import order so every use below says which layer it means - this file is where the two
// meet, and that is exactly where an implicit choice would be a bug waiting to happen.
using SimAction = Shared.GameLogic.Components.EntityAction;
using WireAction = RpgMmo.Wire.V1.EntityAction;
using RpgMmo.Wire.V1;

namespace GameServer.Snapshot;

/// <summary>
/// Per-connection delta encoder state. Remembers the visible state last sent to one
/// client so the tick loop can transmit only what changed.
/// <para>
/// Keyframe policy: a full snapshot is sent on join, whenever the client asks for one
/// (<see cref="RequestFull"/>, wire message <c>MsgResync</c>), and every
/// <c>keyframeInterval</c> snapshots thereafter. Everything in between is a delta.
/// </para>
/// <para>
/// Correctness rests on the transport being ordered and reliable (TCP today): the
/// server treats "last sent" as "last received". The periodic keyframe is the recovery
/// path if that ever stops holding (KCP in unreliable mode, a client that joined late,
/// a client that lost its local state).
/// </para>
/// <para>
/// Threading: <see cref="RequestFull"/> is called from the connection read loop while
/// <see cref="Encode"/> runs on the tick thread — the request flag is interlocked. All
/// other state is touched only by the tick thread.
/// </para>
/// </summary>
public sealed class SnapshotDeltaState
{
    /// <summary>Visible fields of one entity as last sent to this client.</summary>
    private readonly struct SentView : IEquatable<SentView>
    {
        /// <summary>
        /// The entity's id string. NOT part of equality — the map key (the stable int)
        /// already fixes it. Carried so a delta can name the entity in its despawn
        /// list after the entity has left the AOI and there is no view to read it from.
        /// </summary>
        public readonly string Id;

        public readonly string Type;
        public readonly float X;
        public readonly float Y;
        public readonly int Hp;
        public readonly int MaxHp;
        public readonly float Speed;
        public readonly uint FacingBrad;
        public readonly SimAction Action;

        public SentView(in EntityView e)
        {
            Id = e.Id;
            Type = e.Type;
            X = e.Position.X;
            Y = e.Position.Y;
            Hp = e.Hp;
            MaxHp = e.MaxHp;
            Speed = e.Speed;
            FacingBrad = e.FacingBrad;
            Action = e.Action;
        }

        public bool Equals(SentView other) =>
            Hp == other.Hp &&
            MaxHp == other.MaxHp &&
            // Bit comparison, not tolerance: the client mirrors the server's floats
            // exactly, so any change at all must be transmitted. A tolerance here
            // would let slow drift accumulate silently.
            X.Equals(other.X) &&
            Y.Equals(other.Y) &&
            // Speed belongs here for a reason worth stating: this struct is the
            // ONLY thing deciding whether a delta resends an entity. An entity
            // that is buffed while standing still changes nothing else, so
            // omitting Speed would compare it equal, skip it, and leave the
            // client predicting at the old speed until the next keyframe — up to
            // 30 ticks of divergence that no test of the keyframe path can see.
            Speed.Equals(other.Speed) &&
            // Facing and action belong here for exactly the reason Speed does, and the
            // failure is more visible: an entity that turns on the spot changes nothing
            // else, and an entity that starts attacking without moving changes nothing
            // else either. Omitting them would compare such an entity equal, skip it,
            // and leave the client showing the old facing and the old animation until
            // the next keyframe - up to 30 ticks of a character looking the wrong way,
            // with no error on either side and no test of the keyframe path able to see
            // it. This struct is the ONLY thing deciding whether a delta resends an
            // entity.
            FacingBrad == other.FacingBrad &&
            Action == other.Action &&
            string.Equals(Type, other.Type, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SentView v && Equals(v);
        public override int GetHashCode() =>
            HashCode.Combine(Type, X, Y, Hp, MaxHp, Speed, FacingBrad, Action);
    }

    /// <summary>
    /// Entity key (the world-stable int standing in for the id string — see
    /// <see cref="EntityView.Key"/>) to the per-connection wire handle currently
    /// standing in for it. The wire handle space is unchanged; only this map's key
    /// type moved off strings (issue #237: ~1.2M string hashes/s at 200 players).
    /// </summary>
    /// <remarks>
    /// Reset at every keyframe, in lockstep with <see cref="_lastSent"/> — the
    /// two describe the same thing (what this client has been told) and must not
    /// be allowed to drift apart. Resetting at the keyframe is also what bounds
    /// a disagreement: whatever the client believes, a keyframe re-introduces
    /// every binding and repairs it within one interval.
    /// </remarks>
    private readonly Dictionary<int, uint> _handles = new();

    /// <summary>
    /// Next handle to hand out. Starts at 1 because 0 means "not interned" on
    /// the wire; reset at each keyframe, which keeps handles small enough to be
    /// a one- or two-byte varint.
    /// </summary>
    private uint _nextHandle = 1;

    /// <summary>Whether this connection's encoding supports handles. See Encode.</summary>
    private bool _intern;

    /// <summary>Observer position for the encode in progress. See the Encode parameter.</summary>
    private Vec2 _observer;

    /// <summary>Effective budget for the encode in progress; 0 means unbudgeted.</summary>
    private int _budgetBytes;

    /// <summary>
    /// The single <see cref="SnapshotMessage"/> this connection reuses, and the pool of
    /// <see cref="EntitySnapshot"/> objects filling it.
    /// <para>
    /// <b>Ownership contract, and it is the whole safety argument:</b> the message and
    /// its entities belong to this state object, not to the caller. They stay valid only
    /// until the next <see cref="Encode"/> call on the same instance. Callers must
    /// serialize (or otherwise finish with) the returned message before encoding again —
    /// which is what the write task does: claim, encode, serialize, write, loop.
    /// </para>
    /// <para>
    /// Safe without a lock because the instance is per-connection and single-threaded by
    /// design: the tick thread staged the AOI view, and everything here runs on that one
    /// connection's write task. Nothing is shared between connections, so a pool touched
    /// by two threads never arises — which is why this is a per-connection pool and not a
    /// shared one.
    /// </para>
    /// <para>
    /// Rented objects are returned by <i>resetting the high-water mark</i> at the start of
    /// each encode, never by an explicit release call. There is therefore no path on which
    /// an object is returned twice, and none on which a staged-but-never-encoded snapshot
    /// leaks one: an encode that does not happen rents nothing.
    /// </para>
    /// </summary>
    private readonly SnapshotMessage _message = new();
    private readonly List<EntitySnapshot> _pool = new();
    private int _poolUsed;

    private readonly Dictionary<int, SentView> _lastSent = new();
    private readonly HashSet<int> _seen = new();

    /// <summary>Scratch for the keys a delta despawns, reused so removal allocates nothing.</summary>
    private readonly List<int> _removedKeys = new();

    /// <summary>
    /// Per-connection key assignment for the legacy <see cref="EntityState"/> entry
    /// points, which carry no world-stable key. Same id string, same key, never
    /// reused — the exact property string keying had. Keys are negative so they can
    /// never collide with the world's positive stable keys if both entry points are
    /// ever used on one instance.
    /// </summary>
    private readonly Dictionary<string, int> _legacyKeys = new(StringComparer.Ordinal);
    private int _nextLegacyKey;

    /// <summary>Reused conversion buffer for the legacy entry points.</summary>
    private EntityView[] _legacyViews = Array.Empty<EntityView>();
    private int _sinceKeyframe;
    private int _forceFull = 1; // first snapshot on a connection is always a keyframe

    // ──────────────────────────────────────────────────────────────────────────────
    // Downlink budget (per connection, per snapshot).
    //
    // WHY THIS EXISTS. The AOI radius was the only thing bounding a snapshot. Radius
    // bounds *area*, not *population*: a town square, a world boss, a raid stack or a
    // load test all put an unbounded number of entities inside one observer's circle,
    // and the frame grew with them. The budget is the population bound the radius is
    // not, and it is per connection because that is the unit a mobile client's link
    // actually is.
    //
    // WHY SHEDDING CANNOT DESYNC THE DELTA STREAM — the invariant this whole file is
    // built around, stated once, here:
    //
    //   _lastSent and _handles are the encoder's model of "what this client has been
    //   told". An entity is written into either of them ONLY on the code path that has
    //   already committed its bytes to the outgoing message. A shed entity therefore
    //   leaves both untouched, which means:
    //     * its previous SentView stays in _lastSent, so the very next delta still sees
    //       it as changed and re-offers it — a shed update is deferred, never lost;
    //     * it is never given a handle, so no handle can appear on the wire whose
    //       binding the client was not sent in the same message. The "handle without a
    //       binding -> MsgResync" contract in wire.proto is preserved by construction,
    //       not by discipline.
    //   The same rule governs despawns: a key is erased from _lastSent/_handles only if
    //   its id actually made it into msg.Removed. A deferred despawn is re-offered next
    //   tick because the key is still in _lastSent and still absent from _seen.
    //
    //   The one thing that would break this is code that decides what to send in one
    //   place and records what was sent in another. It does not exist here: every
    //   "record" is on the line after its "add", inside the same branch.
    //
    // WHY IT IS BYTE-IDENTICAL WHEN IT DOES NOT BITE. With MaxSnapshotBytes <= 0 the
    // pre-budget code path runs untouched. With a budget set but not exceeded, entities
    // are emitted in AOI order exactly as before — the budgeted path re-orders only when
    // it actually has to shed. SnapshotByteIdentityTests keeps both honest.
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Default for <see cref="MaxSnapshotBytes"/> (<c>GAMESERVER_MAX_SNAPSHOT_BYTES</c>).
    /// </summary>
    /// <remarks>
    /// 8 KiB of snapshot payload. Derived from the only measured number available
    /// (<c>backend/docs/BENCHMARK.md</c>, 2026-08-07): 45.9 KB/s downstream per client at
    /// 200 players, and snapshots are broadcast on the world group at 15 Hz, so the mean
    /// snapshot at that load is ~3.06 KB. 8 KiB is ~2.7x that mean, which is the point:
    /// this is a <b>tail cap, not a traffic shaper</b>. It must not engage at a load the
    /// server is known to handle — a budget that sheds during normal play would trade a
    /// measured-good bandwidth profile for permanent visual staleness — and it must stop
    /// a crowd from turning one observer's frame into an unbounded one. At 15 Hz it bounds
    /// a client's downlink at ~123 KB/s, against ADR-7's &lt; 50 KB/s mobile target for the
    /// steady state.
    /// </remarks>
    public const int DefaultMaxSnapshotBytes = 8192;

    /// <summary>
    /// Ticks of deferral after which a visible entity is considered starved for the
    /// <c>gameserver.snapshots.max_shed_age</c> gauge. Reporting threshold only — it does
    /// not change scheduling, which is already strict oldest-first (see
    /// <see cref="EmitBudgeted"/>).
    /// </summary>
    public const int StarvationReportThreshold = 30;

    /// <summary>
    /// Maximum bytes of <see cref="SnapshotMessage"/> payload this connection may be sent
    /// per snapshot. &lt;= 0 disables the budget entirely and restores the pre-budget
    /// encoder exactly.
    /// </summary>
    /// <remarks>
    /// Payload, not frame: the envelope and the 4-byte length prefix add a small constant
    /// (currently 7-10 bytes) on top, and that constant is not something the encoder can
    /// trade against entities. The budget is a <b>soft</b> cap in exactly one place — the
    /// highest-priority entity is always emitted even if it alone exceeds the budget.
    /// Without that floor a single oversized entity would be deferred for ever, which is
    /// the starvation this design is required to rule out.
    /// </remarks>
    public int MaxSnapshotBytes { get; set; }

    /// <summary>
    /// Entity id of the player this connection belongs to, or null if unknown.
    /// </summary>
    /// <remarks>
    /// The observer's own entity is the one state the client cannot interpolate, dead-
    /// reckon or tolerate being stale in: it is the anchor for prediction reconciliation
    /// (<c>AckTick</c> is meaningless without it). It is therefore priority zero and is
    /// never shed. Everything else on screen degrades gracefully; this does not.
    /// </remarks>
    public string? SelfId { get; set; }

    /// <summary>
    /// Ticks each currently-deferred entity has been waiting, keyed like
    /// <see cref="_lastSent"/>. Absent means "nothing owed".
    /// </summary>
    private readonly Dictionary<int, int> _shedAge = new();

    /// <summary>Indices into the current <c>nearby</c> span that need sending this tick.</summary>
    private readonly List<int> _candidates = new();

    /// <summary>Keys whose despawn is owed to this client, in <see cref="_lastSent"/> order.</summary>
    private readonly List<int> _pendingRemovals = new();

    /// <summary>
    /// Scratch entity used only to measure a candidate's exact wire size before deciding
    /// whether it fits. Filled through the same <see cref="Fill"/> the emit path uses, so
    /// the measurement and the emission cannot drift apart.
    /// </summary>
    private readonly EntitySnapshot _sizingScratch = new();

    private CandidateSort[] _sortBuffer = Array.Empty<CandidateSort>();
    private static readonly CandidateComparer SortComparer = new();

    private long _entitiesShed;
    private long _removalsDeferred;
    private long _budgetedSnapshots;
    private int _maxShedAge;
    private long _reportedEntitiesShed;
    private long _reportedRemovalsDeferred;

    /// <summary>Total entity updates deferred by the budget on this connection.</summary>
    public long EntitiesShed => Interlocked.Read(ref _entitiesShed);

    /// <summary>Total despawn notifications deferred by the budget on this connection.</summary>
    public long RemovalsDeferred => Interlocked.Read(ref _removalsDeferred);

    /// <summary>Snapshots on which the budget actually bit (something was deferred).</summary>
    public long BudgetedSnapshots => Interlocked.Read(ref _budgetedSnapshots);

    /// <summary>
    /// Longest deferral, in snapshots, any entity on this connection has ever reached.
    /// High-water mark; never decreases.
    /// </summary>
    public int MaxShedAge => Volatile.Read(ref _maxShedAge);

    /// <summary>
    /// Entities with an outstanding deferral right now. Bounded by the visible set; a value
    /// that grows without bound is the prune pass having stopped working. Diagnostics/tests.
    /// </summary>
    public int DeferralRecords => _shedAge.Count;

    /// <summary>Payload bytes of the most recent encode. Diagnostics/tests.</summary>
    public int LastPayloadBytes { get; private set; }

    /// <summary>
    /// Shedding counters accumulated since the last call, plus the running high-water
    /// deferral. Same delta-per-connection discipline as
    /// <c>Connection.TakeSnapshotCounters</c>, and for the same reason: summing running
    /// totals across whichever connections are in the tick's scratch array is not a delta.
    /// </summary>
    internal void TakeBudgetCounters(out long shed, out long removalsDeferred, out int maxShedAge)
    {
        long s = EntitiesShed;
        long r = RemovalsDeferred;

        shed = s - _reportedEntitiesShed;
        removalsDeferred = r - _reportedRemovalsDeferred;
        maxShedAge = MaxShedAge;

        _reportedEntitiesShed = s;
        _reportedRemovalsDeferred = r;
    }

    /// <summary>One candidate's scheduling key. Struct, sorted in a reused array.</summary>
    private struct CandidateSort
    {
        public int Age;
        public float DistanceSq;
        public int Index;
        public bool Self;
    }

    /// <summary>
    /// The priority order, and the argument for it.
    ///
    /// <para><b>1. The observer's own entity.</b> Everything else on screen can be
    /// interpolated or dead-reckoned for a few ticks and the player will not name what
    /// changed. Their own character cannot: it is the reconciliation anchor, and a stale
    /// one reads as rubber-banding, the single most-noticed netcode artefact.</para>
    ///
    /// <para><b>2. Longest deferral first.</b> This is what makes the scheduler fair
    /// rather than merely reasonable. Because the comparison is strictly oldest-first, an
    /// entity deferred on tick N outranks every entity that became dirty on tick N+1, so
    /// the deferred set drains before the fresh set — a strict aging round-robin. With at
    /// least one entity admitted per snapshot (guaranteed by the soft-cap floor above), an
    /// entity that stays visible and dirty cannot be deferred more than once per other
    /// dirty entity ahead of it: <b>max deferral is bounded by the number of dirty
    /// entities in the observer's AOI</b>, and is not a function of how long the session
    /// runs. <c>Starvation_IsBoundedByTheDirtySetNotBySessionLength</c> asserts it.
    ///
    /// <para><b>That bound holds only while the budget is spent on entity updates alone.</b>
    /// The floor guarantees ONE candidate per snapshot, and priority 1 is the observer's own
    /// entity — so when a non-entity cost competes for the budget (in practice: a large
    /// despawn backlog), the guaranteed slot goes to self every tick and the other dirty
    /// entities wait for the backlog to stop eating the remainder. Measured at 63 ticks
    /// against a dirty set of 6 in
    /// <c>UnderDespawnBacklog_AnEntityUpdateStillLandsEveryTick</c>. The wait is then the
    /// dirty set PLUS the backlog's drain time — still finite, still independent of session
    /// length, because the backlog is finite and draining; but it is not the dirty-set
    /// figure, and an operator reading <c>max_shed_age</c> during heavy AOI churn should
    /// expect the larger one.</para></para>
    ///
    /// <para><b>3. Nearest first.</b> Among equally-deferred entities, error is most
    /// visible closest to the camera — a 200ms-stale entity two metres away is obvious, at
    /// forty metres it is a pixel. Squared distance; the square root would change nothing
    /// about the order.</para>
    ///
    /// <para><b>4. AOI index.</b> Pure tie-break, so the order is total and the output is
    /// deterministic — the same reason keyframe phase is derived from the user id rather
    /// than randomised.</para>
    /// </summary>
    private sealed class CandidateComparer : IComparer<CandidateSort>
    {
        public int Compare(CandidateSort a, CandidateSort b)
        {
            if (a.Self != b.Self) return a.Self ? -1 : 1;
            if (a.Age != b.Age) return b.Age.CompareTo(a.Age); // older first
            int d = a.DistanceSq.CompareTo(b.DistanceSq);      // nearer first
            if (d != 0) return d;
            return a.Index.CompareTo(b.Index);
        }
    }

    /// <summary>
    /// Per-connection keyframe phase, applied once after the join keyframe so that
    /// clients which joined on the same tick do not then keyframe in lockstep forever.
    /// See <see cref="PhaseFor"/>.
    /// </summary>
    private readonly int _phaseSeed;
    private bool _phaseApplied;

    /// <summary>Unstaggered state — every keyframe cycle is exactly the full interval.</summary>
    public SnapshotDeltaState() : this(0) { }

    /// <summary>
    /// State whose keyframe cycle is offset by <paramref name="phaseSeed"/> snapshots
    /// once, immediately after the join keyframe. Use <see cref="PhaseFor"/> to derive
    /// the seed from the connection's identity.
    /// </summary>
    public SnapshotDeltaState(int phaseSeed)
    {
        _phaseSeed = phaseSeed < 0 ? 0 : phaseSeed;
    }

    /// <summary>
    /// Deterministic keyframe phase for a connection, derived from its user id.
    ///
    /// <para>Deterministic on purpose. A random offset would spread the load equally
    /// well but make a replay of the same session produce different frames, which is
    /// the same reasoning that puts cooldowns on tick counts rather than wall clock
    /// (<c>docs/DESIGN.md</c>). Two runs with the same players produce the same
    /// keyframe schedule.</para>
    ///
    /// <para>FNV-1a rather than <see cref="string.GetHashCode()"/>: string hashing in
    /// .NET is randomized per process, so it would be stable within a run and different
    /// across runs — exactly the non-determinism this avoids.</para>
    /// </summary>
    public static int PhaseFor(string userId)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        uint hash = offsetBasis;
        foreach (char c in userId)
        {
            hash = (hash ^ (byte)c) * prime;
            hash = (hash ^ (byte)(c >> 8)) * prime;
        }
        // Mask the sign bit rather than casting: a negative seed would be clamped to 0
        // and silently unstagger a whole class of user ids.
        return (int)(hash & 0x7FFFFFFF);
    }

    /// <summary>Number of snapshots sent since the last keyframe. Diagnostics/tests.</summary>
    public int SinceKeyframe => _sinceKeyframe;

    /// <summary>Ask for the next snapshot to be a full keyframe. Thread-safe.</summary>
    public void RequestFull() => Interlocked.Exchange(ref _forceFull, 1);

    /// <summary>
    /// Build the snapshot to send to this client for the current tick.
    /// </summary>
    /// <param name="tick">Current simulation tick.</param>
    /// <param name="ackTick">Highest input tick accepted for this client's own entity.</param>
    /// <param name="nearby">Entities inside the client's AOI this tick.</param>
    /// <param name="keyframeInterval">
    /// Snapshots between keyframes. &lt;= 0 disables delta encoding entirely (every
    /// snapshot is a keyframe) — the escape hatch if a client cannot merge deltas.
    /// </param>
    /// <param name="intern">
    /// Whether to replace repeated entity ids with per-connection handles. TRUE
    /// only for Protobuf connections: the JSON encoding has no handle field, so
    /// interning there would emit entities with an empty id and silently break
    /// every pre-interning client. The encoding is a property of the connection,
    /// so the caller passes it rather than this class guessing.
    /// </param>
    /// <param name="observer">
    /// World position the snapshot is being built for — the observer's own entity. Used
    /// only to order entities when the downlink budget has to shed; irrelevant, and safely
    /// left at its default, when <see cref="MaxSnapshotBytes"/> is not set.
    /// </param>
    public SnapshotMessage Encode(ulong tick, ulong ackTick, List<EntityState> nearby, int keyframeInterval,
        bool intern = false, Vec2 observer = default)
        => Encode(tick, ackTick, CollectionsMarshal.AsSpan(nearby), keyframeInterval, intern, observer);

    /// <inheritdoc cref="Encode(ulong, ulong, List{EntityState}, int, bool)"/>
    /// <remarks>
    /// Legacy <see cref="EntityState"/> entry point, kept for callers (and tests)
    /// that still gather full states. It adapts each state to an
    /// <see cref="EntityView"/> — assigning per-connection negative keys, one string
    /// hash per entity, exactly what the string-keyed maps used to pay — and forwards
    /// to the view core below. One implementation, so the delta/keyframe bookkeeping
    /// cannot diverge between the entry points.
    /// </remarks>
    public SnapshotMessage Encode(ulong tick, ulong ackTick, ReadOnlySpan<EntityState> nearby, int keyframeInterval,
        bool intern = false, Vec2 observer = default)
    {
        if (_legacyViews.Length < nearby.Length) _legacyViews = new EntityView[nearby.Length];
        for (int i = 0; i < nearby.Length; i++)
        {
            ref readonly EntityState e = ref nearby[i];
            if (!_legacyKeys.TryGetValue(e.Id, out int key))
            {
                key = --_nextLegacyKey;
                _legacyKeys[e.Id] = key;
            }
            _legacyViews[i] = new EntityView(key, e.Id, e.Type, e.Position, e.Hp, e.MaxHp, e.Speed, e.FacingBrad, e.Action);
        }
        return Encode(tick, ackTick, _legacyViews.AsSpan(0, nearby.Length), keyframeInterval, intern, observer);
    }

    /// <inheritdoc cref="Encode(ulong, ulong, List{EntityState}, int, bool)"/>
    /// <remarks>
    /// The view form is what the write task calls (issue #237): the AOI walk fills a
    /// connection-owned <see cref="EntityView"/> buffer — the trimmed 7-field compose —
    /// and the delta maps key on <see cref="EntityView.Key"/> instead of hashing the
    /// id string 2-3 times per visible entity. Wire output is byte-identical to the
    /// string-keyed encoder (proved by <c>TrimmedGatherByteIdentityTests</c>): the
    /// key never reaches the wire and key↔id is a bijection, so every branch taken
    /// here is the branch the string-keyed code took.
    /// </remarks>
    public SnapshotMessage Encode(ulong tick, ulong ackTick, ReadOnlySpan<EntityView> nearby, int keyframeInterval,
        bool intern = false, Vec2 observer = default)
    {
        _intern = intern;
        _observer = observer;
        // Latched once per encode: MaxSnapshotBytes is a settable property and a change
        // landing between the sizing pass and the emit pass would let the two disagree
        // about the same snapshot.
        //
        // JSON connections are deliberately NOT budgeted. Every byte figure in this class
        // is a protobuf size, and a JSON frame for the same snapshot is several times
        // larger, so enforcing a protobuf-derived cap on a JSON stream would report bytes
        // that are not the bytes on that wire — a counter nobody can trust is worse than
        // no counter. JSON is the legacy encoding (ADR-9) and is expected to disappear;
        // until it does, a JSON client's downlink is bounded only by the AOI radius, and
        // that is a stated limitation, not an oversight.
        _budgetBytes = intern ? MaxSnapshotBytes : 0;
        bool full = Interlocked.Exchange(ref _forceFull, 0) != 0
                    || keyframeInterval <= 0
                    || _sinceKeyframe >= keyframeInterval;

        if (full)
        {
            // Stagger exactly once, after the join keyframe. A cohort that joins on the
            // same tick would otherwise share a counter phase and serialize full state
            // for every member on the same tick, every interval, for the life of the
            // server — a self-inflicted latency spike precisely while filling up.
            //
            // Applied once, not on every keyframe: a permanent offset would shorten this
            // client's cycle to (interval - phase) and hand it more keyframes than
            // everyone else. One shortened first cycle is enough to desynchronize.
            if (!_phaseApplied && keyframeInterval > 1)
            {
                _phaseApplied = true;
                _sinceKeyframe = _phaseSeed % keyframeInterval;
            }
            else
            {
                _sinceKeyframe = 0;
            }
            return EncodeFull(tick, ackTick, nearby);
        }

        _sinceKeyframe++;
        return EncodeDelta(tick, ackTick, nearby);
    }

    /// <summary>
    /// Reset the reused message and hand back every pooled entity in one step. Called at
    /// the top of each encode, which is what makes "return" a no-op rather than a call
    /// anyone can forget or repeat.
    /// </summary>
    private SnapshotMessage BeginMessage(ulong tick, ulong ackTick, bool full)
    {
        _message.Entities.Clear();
        _message.Removed.Clear();
        _message.Tick = tick;
        _message.AckTick = ackTick;
        _message.Full = full;
        _poolUsed = 0;
        return _message;
    }

    /// <summary>
    /// A cleared <see cref="EntitySnapshot"/> from the pool, growing it on first use at
    /// this depth. Every field is reset, not just the ones the caller is about to set: a
    /// stale <c>Id</c> or <c>Handle</c> carried over from a previous tick would be wrong
    /// state on the wire, which is the failure mode this whole subsystem exists to avoid.
    /// Clearing to the proto3 defaults also keeps the bytes identical, since default
    /// values are not emitted.
    /// </summary>
    private EntitySnapshot Rent()
    {
        EntitySnapshot e;
        if (_poolUsed < _pool.Count)
        {
            e = _pool[_poolUsed];
        }
        else
        {
            e = new EntitySnapshot();
            _pool.Add(e);
        }
        _poolUsed++;

        e.Id = "";
        e.TypeName = "";
        e.Type = EntityType.Unspecified;
        e.Handle = 0;
        e.X = 0f;
        e.Y = 0f;
        e.Hp = 0;
        e.MaxHp = 0;
        e.Speed = 0f;
        // Reset like every other field: a pooled message that kept a previous entity's
        // facing would attach it to whichever entity rents the object next, which is a
        // wrong value rather than a missing one - far harder to notice.
        e.FacingBrad = 0;
        e.Action = WireAction.Unspecified;
        return e;
    }

    private SnapshotMessage EncodeFull(ulong tick, ulong ackTick, ReadOnlySpan<EntityView> nearby)
    {
        var msg = BeginMessage(tick, ackTick, full: true);
        _lastSent.Clear();
        // A keyframe is the synchronisation point for the handle space: both
        // sides drop every binding and start again from 1.
        _handles.Clear();
        _nextHandle = 1;

        if (_budgetBytes <= 0)
        {
            // Pre-budget path, unchanged.
            _shedAge.Clear();

            // Indexed for-loop, no LINQ, no enumerator boxing: this runs once per client per tick.
            for (int i = 0; i < nearby.Length; i++)
            {
                ref readonly EntityView e = ref nearby[i];
                msg.Entities.Add(ToMsg(in e));
                _lastSent[e.Key] = new SentView(in e);
            }

            LastPayloadBytes = 0;
            return msg;
        }

        // A keyframe is budgeted like any other snapshot, and it has to be: the crowd
        // that makes a delta expensive makes the keyframe MORE expensive, so exempting
        // keyframes would leave the cap open on exactly the worst frame of the interval.
        //
        // What shedding on a keyframe means for the client is different from shedding on
        // a delta, and worth stating: SnapshotMerger clears its entity set on a full
        // snapshot, so an entity omitted from a keyframe disappears from the client's
        // world and is re-introduced (with a fresh handle and its id) by a following
        // delta, because it is absent from _lastSent. Correct — the client is never told
        // something false — but visible as a pop, which is the cost of a hard byte cap.
        // The nearest entities are the ones that survive, so the pop happens at the edge
        // of the observer's circle where it is least noticeable.
        _candidates.Clear();
        _seen.Clear();
        for (int i = 0; i < nearby.Length; i++)
        {
            _candidates.Add(i);
            // Filled on the keyframe path too, purely so the deferral bookkeeping can be
            // pruned against it below — a keyframe has no use for _seen otherwise.
            _seen.Add(nearby[i].Key);
        }
        _pendingRemovals.Clear(); // a keyframe carries no despawn list by definition

        return EncodeBudgeted(msg, nearby, HeaderBytes(tick, ackTick, full: true));
    }

    private SnapshotMessage EncodeDelta(ulong tick, ulong ackTick, ReadOnlySpan<EntityView> nearby)
    {
        if (_budgetBytes > 0) return EncodeDeltaBudgeted(tick, ackTick, nearby);

        _seen.Clear();
        var msg = BeginMessage(tick, ackTick, full: false);
        _shedAge.Clear();

        for (int i = 0; i < nearby.Length; i++)
        {
            ref readonly EntityView e = ref nearby[i];
            _seen.Add(e.Key);

            var view = new SentView(in e);
            if (_lastSent.TryGetValue(e.Key, out var prev) && prev.Equals(view))
                continue; // unchanged since last send -> omit

            msg.Entities.Add(ToMsg(in e));
            _lastSent[e.Key] = view;
        }

        // Anything previously sent but no longer in AOI is an explicit despawn.
        //
        // Despawn ORDER is part of the wire contract this class must not move: it is
        // _lastSent's enumeration order, and a Dictionary enumerates its entries array
        // in slot order, which is a function of the insert/remove sequence and not of
        // the key type — so switching the key from string to int leaves the order,
        // and therefore the bytes, exactly where they were.
        if (_lastSent.Count != _seen.Count)
        {
            foreach (var kv in _lastSent)
            {
                if (!_seen.Contains(kv.Key))
                {
                    msg.Removed.Add(kv.Value.Id);
                    _removedKeys.Add(kv.Key);
                }
            }
            for (int i = 0; i < _removedKeys.Count; i++)
            {
                _lastSent.Remove(_removedKeys[i]);
                // The handle is NOT returned to the pool: _nextHandle only ever
                // advances within an interval. Freeing it would allow reuse, and
                // reuse is the failure this design exists to avoid.
                _handles.Remove(_removedKeys[i]);
            }
            _removedKeys.Clear();
        }

        LastPayloadBytes = 0;
        return msg;
    }

    /// <summary>
    /// Delta encode under a byte budget. Splits into the same two questions the
    /// unbudgeted path answers — which entities changed, and which despawned — but
    /// answers them into <see cref="_candidates"/> and <see cref="_pendingRemovals"/>
    /// first, so <see cref="EncodeBudgeted"/> can decide what fits before anything is
    /// committed to <see cref="_lastSent"/> or <see cref="_handles"/>.
    /// </summary>
    private SnapshotMessage EncodeDeltaBudgeted(ulong tick, ulong ackTick, ReadOnlySpan<EntityView> nearby)
    {
        _seen.Clear();
        var msg = BeginMessage(tick, ackTick, full: false);
        _candidates.Clear();

        for (int i = 0; i < nearby.Length; i++)
        {
            ref readonly EntityView e = ref nearby[i];
            _seen.Add(e.Key);

            if (_lastSent.TryGetValue(e.Key, out var prev) && prev.Equals(new SentView(in e)))
            {
                // Nothing owed on this entity: the client's copy already matches. That
                // includes an entity which was deferred earlier and has since drifted
                // back to precisely the values last sent — rare, but it must clear the
                // deferral or the entity would keep a priority it no longer deserves.
                _shedAge.Remove(e.Key);
                continue;
            }

            _candidates.Add(i);
        }

        // Despawns owed to this client. Collected, NOT applied: a despawn is only erased
        // from _lastSent/_handles once its id has actually been written into msg.Removed
        // (see EncodeBudgeted). Erasing here and shedding later would tell this encoder
        // the client had forgotten an entity it was never asked to forget — a ghost that
        // survives until the next keyframe with nothing reporting it.
        _pendingRemovals.Clear();
        if (_lastSent.Count != _seen.Count)
        {
            // _lastSent enumeration order is the despawn order the wire contract fixes;
            // preserved here because this list is filled and drained in that same order.
            foreach (var kv in _lastSent)
            {
                if (!_seen.Contains(kv.Key)) _pendingRemovals.Add(kv.Key);
            }
        }

        return EncodeBudgeted(msg, nearby, HeaderBytes(tick, ackTick, full: false));
    }

    /// <summary>
    /// Fit <see cref="_candidates"/> and <see cref="_pendingRemovals"/> into
    /// <see cref="_budgetBytes"/>, committing only what is written.
    /// </summary>
    private SnapshotMessage EncodeBudgeted(
        SnapshotMessage msg, ReadOnlySpan<EntityView> nearby, int headerBytes)
    {
        // Sizing pass, in AOI order. Handles would be handed out in AOI order on the
        // emit-everything path below, so these sizes are exactly that path's sizes — the
        // measurement and the emission are the same function (Fill) fed the same inputs,
        // which is the only reason a "does it fit" answer computed here is safe to act on
        // there.
        int total = headerBytes;
        uint prospective = _nextHandle;
        for (int c = 0; c < _candidates.Count; c++)
        {
            ref readonly EntityView e = ref nearby[_candidates[c]];
            bool introduce = !_intern || !_handles.ContainsKey(e.Key);
            uint handle = 0;
            if (_intern)
            {
                if (introduce) handle = prospective++;
                else handle = _handles[e.Key];
            }
            Fill(_sizingScratch, in e, handle, introduce);
            total += EntryBytes(_sizingScratch);
        }
        for (int r = 0; r < _pendingRemovals.Count; r++)
        {
            total += RemovedBytes(_lastSent[_pendingRemovals[r]].Id);
        }

        if (total <= _budgetBytes)
        {
            // Fits. Emit in AOI order, which is byte-for-byte what the unbudgeted encoder
            // would have produced for this tick — the budget is invisible until it bites.
            for (int c = 0; c < _candidates.Count; c++)
            {
                ref readonly EntityView e = ref nearby[_candidates[c]];
                EmitEntity(msg, in e);
            }
            CommitRemovals(msg, _pendingRemovals.Count);
            PruneDeferrals();
            LastPayloadBytes = total;
            return msg;
        }

        SnapshotMessage shedded = EmitBudgeted(msg, nearby, headerBytes);
        PruneDeferrals();
        return shedded;
    }

    /// <summary>
    /// Drop deferral records for entities that are no longer visible.
    /// </summary>
    /// <remarks>
    /// Needed for one case that no other path covers: an entity which is <b>new</b> to this
    /// connection, is deferred before it is ever sent, and then leaves the AOI. It never
    /// entered <see cref="_lastSent"/>, so it never becomes a despawn and
    /// <see cref="CommitRemovals"/> never erases it — its <see cref="_shedAge"/> entry
    /// would simply stay. One entry is nothing; the accumulation over a session of a map
    /// whose spawns churn at the edge of an observer's circle is a slow leak in a
    /// per-connection dictionary, which is the kind of growth that is invisible until a
    /// long-lived server is the thing being debugged.
    /// </remarks>
    private void PruneDeferrals()
    {
        if (_shedAge.Count == 0) return;

        foreach (var kv in _shedAge)
        {
            if (!_seen.Contains(kv.Key)) _removedKeys.Add(kv.Key);
        }
        for (int i = 0; i < _removedKeys.Count; i++) _shedAge.Remove(_removedKeys[i]);
        _removedKeys.Clear();
    }

    /// <summary>
    /// The shedding path: order the candidates, then spend the budget on them in that
    /// order. See <see cref="CandidateComparer"/> for the priority order and the argument
    /// for it, and the class-level comment for why a shed entity cannot desynchronise the
    /// delta stream.
    /// </summary>
    private SnapshotMessage EmitBudgeted(
        SnapshotMessage msg, ReadOnlySpan<EntityView> nearby, int headerBytes)
    {
        Interlocked.Increment(ref _budgetedSnapshots);

        int n = _candidates.Count;
        if (_sortBuffer.Length < n) _sortBuffer = new CandidateSort[n + (n >> 2) + 8];

        for (int c = 0; c < n; c++)
        {
            int index = _candidates[c];
            ref readonly EntityView e = ref nearby[index];
            float dx = e.Position.X - _observer.X;
            float dy = e.Position.Y - _observer.Y;
            _shedAge.TryGetValue(e.Key, out int age);
            _sortBuffer[c] = new CandidateSort
            {
                Age = age,
                DistanceSq = (dx * dx) + (dy * dy),
                Index = index,
                Self = SelfId != null && string.Equals(e.Id, SelfId, StringComparison.Ordinal),
            };
        }

        Array.Sort(_sortBuffer, 0, n, SortComparer);

        int used = headerBytes;
        int next = 0;

        // The floor. The top-priority candidate is emitted whatever it costs, and before
        // the despawn list, for two reasons that are really one: it makes the budget a
        // soft cap rather than a trap (an entity larger than the whole budget would
        // otherwise be deferred for ever), and it guarantees at least one candidate is
        // admitted per snapshot, which is the premise the deferral bound in
        // CandidateComparer rests on. Without it a steady stream of despawns could
        // consume the budget every tick and starve updates indefinitely.
        if (n > 0)
        {
            ref readonly EntityView top = ref nearby[_sortBuffer[0].Index];
            used += EmitEntity(msg, in top);
            next = 1;
        }

        // Despawns next. A deferred despawn leaves the client rendering an entity that no
        // longer exists — wrong state, not stale state — so it outranks every remaining
        // update. Deferred ones stay in _lastSent and out of _seen, so the next delta
        // re-offers them; nothing is lost, only delayed.
        int removalsWritten = 0;
        for (int r = 0; r < _pendingRemovals.Count; r++)
        {
            int cost = RemovedBytes(_lastSent[_pendingRemovals[r]].Id);
            if (used + cost > _budgetBytes) break;
            used += cost;
            removalsWritten++;
        }
        if (removalsWritten < _pendingRemovals.Count)
        {
            Interlocked.Add(ref _removalsDeferred, _pendingRemovals.Count - removalsWritten);
        }
        CommitRemovals(msg, removalsWritten);

        // The rest, in priority order, until the next one does not fit. Stopping rather
        // than skipping ahead to a smaller candidate is deliberate: skipping would let a
        // cheap far-away entity overtake an expensive near one every tick, which is the
        // starvation the aging order exists to prevent.
        int shed = 0;
        for (int i = next; i < n; i++)
        {
            ref readonly EntityView e = ref nearby[_sortBuffer[i].Index];
            int size = MeasureEntity(in e);
            if (used + size > _budgetBytes)
            {
                for (int j = i; j < n; j++) shed += Defer(in nearby[_sortBuffer[j].Index]);
                break;
            }
            used += EmitEntity(msg, in e);
        }

        if (shed > 0) Interlocked.Add(ref _entitiesShed, shed);
        LastPayloadBytes = used;
        return msg;
    }

    /// <summary>
    /// Write the first <paramref name="count"/> owed despawns into the message and erase
    /// exactly those keys. The erase is on the line after the write, on purpose: it is
    /// the only place _lastSent and _handles may forget an entity, and it must not run
    /// for an id that stayed off the wire.
    /// </summary>
    private void CommitRemovals(SnapshotMessage msg, int count)
    {
        for (int r = 0; r < count; r++)
        {
            int key = _pendingRemovals[r];
            msg.Removed.Add(_lastSent[key].Id);
            _lastSent.Remove(key);
            // The handle is NOT returned to the pool: _nextHandle only ever advances
            // within an interval. Freeing it would allow reuse, and reuse is the failure
            // this design exists to avoid.
            _handles.Remove(key);
            _shedAge.Remove(key);
        }
    }

    /// <summary>
    /// Commit one entity to the wire and to the encoder's model of what the client has.
    /// Returns the bytes it added to the payload.
    /// </summary>
    private int EmitEntity(SnapshotMessage msg, in EntityView e)
    {
        var ent = ToMsg(in e);
        msg.Entities.Add(ent);
        _lastSent[e.Key] = new SentView(in e);
        _shedAge.Remove(e.Key);
        return EntryBytes(ent);
    }

    /// <summary>
    /// Record that an entity's update did not fit. Touches only the deferral bookkeeping —
    /// never _lastSent and never _handles, which is what makes the deferral invisible to
    /// the delta stream's correctness and visible only as latency.
    /// </summary>
    private int Defer(in EntityView e)
    {
        _shedAge.TryGetValue(e.Key, out int age);
        age++;
        _shedAge[e.Key] = age;
        if (age > _maxShedAge) Volatile.Write(ref _maxShedAge, age);
        return 1;
    }

    /// <summary>
    /// Exact wire size of one candidate as the emit path would write it, measured through
    /// the same <see cref="Fill"/>.
    /// </summary>
    private int MeasureEntity(in EntityView e)
    {
        uint handle = 0;
        bool introduce = true;
        if (_intern)
        {
            if (_handles.TryGetValue(e.Key, out uint existing))
            {
                handle = existing;
                introduce = false;
            }
            else
            {
                handle = _nextHandle;
            }
        }
        Fill(_sizingScratch, in e, handle, introduce);
        return EntryBytes(_sizingScratch);
    }

    /// <summary>Tag size of <c>SnapshotMessage.entities</c> (field 4, length-delimited).</summary>
    private const int EntitiesTagBytes = 1;

    /// <summary>Tag size of <c>SnapshotMessage.removed</c> (field 5, length-delimited).</summary>
    private const int RemovedTagBytes = 1;

    /// <summary>Bytes one entity contributes to the payload: tag + length prefix + body.</summary>
    private static int EntryBytes(EntitySnapshot e)
    {
        int size = e.CalculateSize();
        return EntitiesTagBytes + CodedOutputStream.ComputeLengthSize(size) + size;
    }

    /// <summary>Bytes one despawned id contributes to the payload.</summary>
    private static int RemovedBytes(string id)
        => RemovedTagBytes + CodedOutputStream.ComputeStringSize(id);

    /// <summary>
    /// Bytes the snapshot's own scalar fields contribute. proto3 omits default values, so
    /// tick 0 and ack 0 cost nothing and <c>full=false</c> costs nothing — the same rule
    /// the generated writer applies, which is why this is exact rather than an estimate.
    /// </summary>
    private static int HeaderBytes(ulong tick, ulong ackTick, bool full)
        => (tick != 0 ? 1 + CodedOutputStream.ComputeUInt64Size(tick) : 0)
         + (ackTick != 0 ? 1 + CodedOutputStream.ComputeUInt64Size(ackTick) : 0)
         + (full ? 2 : 0);

    /// <summary>
    /// Build the wire entity, interning its id against this connection's handle
    /// table.
    /// </summary>
    /// <remarks>
    /// The id string is written ONLY on the message that introduces the handle.
    /// Every later mention costs a varint instead of ~17 bytes. A handle is never
    /// reused within a keyframe interval: reuse would let a client that missed a
    /// despawn attribute an update to the wrong entity, which is wrong state
    /// rather than absent state and far harder to detect.
    /// </remarks>
    private EntitySnapshot ToMsg(in EntityView e)
    {
        var msg = Rent();

        if (!_intern)
        {
            // JSON: never intern. The id is the only identifier that encoding has.
            Fill(msg, in e, handle: 0, introduceId: true);
            return msg;
        }

        if (_handles.TryGetValue(e.Key, out uint handle))
        {
            // Already introduced this interval: handle alone.
            Fill(msg, in e, handle, introduceId: false);
        }
        else
        {
            // First mention: carry both, so the receiver learns the binding.
            handle = _nextHandle++;
            _handles[e.Key] = handle;
            Fill(msg, in e, handle, introduceId: true);
        }
        return msg;
    }

    /// <summary>
    /// Write one entity's wire fields. The single place an <see cref="EntitySnapshot"/> is
    /// populated, called both by <see cref="ToMsg"/> (which then commits it) and by
    /// <see cref="MeasureEntity"/> (which only measures it).
    /// </summary>
    /// <remarks>
    /// <b>Why this is one function and not two.</b> The budget decides what to send from a
    /// measured size and then sends it; if the measuring code and the writing code were
    /// separate, any field added to one and not the other would make the budget wrong by a
    /// few bytes per entity — small, silent, and growing with the crowd. Sharing the
    /// writer makes the two impossible to disagree: a field is measured because it is
    /// written, by the same statement.
    ///
    /// <para><paramref name="handle"/> is written even when it is being introduced, and
    /// the id is written only then — the interning contract in <c>wire.proto</c>. Speed is
    /// written on every mention, including handle-only ones: a client that resolves a
    /// handle expects complete state for that entity, and sending speed only with the id
    /// would leave it correct once per keyframe interval and stale in between.</para>
    ///
    /// <para>Every field is assigned unconditionally so the scratch instance carries no
    /// residue from the previous candidate it measured. <see cref="Rent"/> makes the same
    /// guarantee for pooled instances, for the same reason.</para>
    /// </remarks>
    private static void Fill(EntitySnapshot msg, in EntityView e, uint handle, bool introduceId)
    {
        msg.X = e.Position.X;
        msg.Y = e.Position.Y;
        msg.Hp = e.Hp;
        msg.MaxHp = e.MaxHp;
        msg.Speed = e.Speed;
        // Written on every mention, including handle-only ones, for the same reason as
        // Speed: a client that resolves a handle expects complete state, and sending
        // these only alongside the id would leave them correct once per keyframe
        // interval and stale in between. Both are already in the wire's biased/reserved
        // form, so there is no conversion on this path.
        //
        // They are written HERE and not in ToMsg deliberately. Fill is the single writer
        // of an EntitySnapshot precisely so the budget's dry sizing pass and the emit
        // path cannot disagree about how large an entity is; a field written beside Fill
        // instead of inside it makes the budget wrong by a few bytes per entity, which is
        // invisible at four entities and a whole entity's worth at eighty.
        msg.FacingBrad = e.FacingBrad;
        msg.Action = (WireAction)e.Action;
        msg.Handle = handle;
        msg.Id = introduceId ? e.Id : "";
        msg.TypeName = "";
        msg.Type = EntityType.Unspecified;
        EntityTypes.SetType(msg, e.Type);
    }
}
