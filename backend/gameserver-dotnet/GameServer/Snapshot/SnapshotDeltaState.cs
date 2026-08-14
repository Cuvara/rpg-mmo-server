using System.Runtime.InteropServices;
using GameServer.Net;
using Shared.GameLogic.Components;
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
        public readonly string Type;
        public readonly float X;
        public readonly float Y;
        public readonly int Hp;
        public readonly int MaxHp;

        public SentView(in EntityState e)
        {
            Type = e.Type;
            X = e.Position.X;
            Y = e.Position.Y;
            Hp = e.Hp;
            MaxHp = e.MaxHp;
        }

        public bool Equals(SentView other) =>
            Hp == other.Hp &&
            MaxHp == other.MaxHp &&
            // Bit comparison, not tolerance: the client mirrors the server's floats
            // exactly, so any change at all must be transmitted. A tolerance here
            // would let slow drift accumulate silently.
            X.Equals(other.X) &&
            Y.Equals(other.Y) &&
            string.Equals(Type, other.Type, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SentView v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(Type, X, Y, Hp, MaxHp);
    }

    /// <summary>
    /// Entity id to the per-connection handle currently standing in for it.
    /// </summary>
    /// <remarks>
    /// Reset at every keyframe, in lockstep with <see cref="_lastSent"/> — the
    /// two describe the same thing (what this client has been told) and must not
    /// be allowed to drift apart. Resetting at the keyframe is also what bounds
    /// a disagreement: whatever the client believes, a keyframe re-introduces
    /// every binding and repairs it within one interval.
    /// </remarks>
    private readonly Dictionary<string, uint> _handles = new(StringComparer.Ordinal);

    /// <summary>
    /// Next handle to hand out. Starts at 1 because 0 means "not interned" on
    /// the wire; reset at each keyframe, which keeps handles small enough to be
    /// a one- or two-byte varint.
    /// </summary>
    private uint _nextHandle = 1;

    /// <summary>Whether this connection's encoding supports handles. See Encode.</summary>
    private bool _intern;

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

    private readonly Dictionary<string, SentView> _lastSent = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _sinceKeyframe;
    private int _forceFull = 1; // first snapshot on a connection is always a keyframe

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
    public SnapshotMessage Encode(ulong tick, ulong ackTick, List<EntityState> nearby, int keyframeInterval,
        bool intern = false)
        => Encode(tick, ackTick, CollectionsMarshal.AsSpan(nearby), keyframeInterval, intern);

    /// <inheritdoc cref="Encode(ulong, ulong, List{EntityState}, int, bool)"/>
    /// <remarks>
    /// The span form is what the tick loop calls: the AOI walk fills a buffer the
    /// connection owns and reuses, so nothing here needs a list to exist. The list
    /// overload above forwards to this one — one implementation, so the delta/keyframe
    /// bookkeeping cannot diverge between the two entry points.
    /// </remarks>
    public SnapshotMessage Encode(ulong tick, ulong ackTick, ReadOnlySpan<EntityState> nearby, int keyframeInterval,
        bool intern = false)
    {
        _intern = intern;
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
        return e;
    }

    private SnapshotMessage EncodeFull(ulong tick, ulong ackTick, ReadOnlySpan<EntityState> nearby)
    {
        var msg = BeginMessage(tick, ackTick, full: true);
        _lastSent.Clear();
        // A keyframe is the synchronisation point for the handle space: both
        // sides drop every binding and start again from 1.
        _handles.Clear();
        _nextHandle = 1;

        // Indexed for-loop, no LINQ, no enumerator boxing: this runs once per client per tick.
        for (int i = 0; i < nearby.Length; i++)
        {
            var e = nearby[i];
            msg.Entities.Add(ToMsg(in e));
            _lastSent[e.Id] = new SentView(in e);
        }

        return msg;
    }

    private SnapshotMessage EncodeDelta(ulong tick, ulong ackTick, ReadOnlySpan<EntityState> nearby)
    {
        _seen.Clear();
        var msg = BeginMessage(tick, ackTick, full: false);

        for (int i = 0; i < nearby.Length; i++)
        {
            var e = nearby[i];
            _seen.Add(e.Id);

            var view = new SentView(in e);
            if (_lastSent.TryGetValue(e.Id, out var prev) && prev.Equals(view))
                continue; // unchanged since last send -> omit

            msg.Entities.Add(ToMsg(in e));
            _lastSent[e.Id] = view;
        }

        // Anything previously sent but no longer in AOI is an explicit despawn.
        if (_lastSent.Count != _seen.Count)
        {
            foreach (var id in _lastSent.Keys)
            {
                if (!_seen.Contains(id)) msg.Removed.Add(id);
            }
            for (int i = 0; i < msg.Removed.Count; i++)
            {
                _lastSent.Remove(msg.Removed[i]);
                // The handle is NOT returned to the pool: _nextHandle only ever
                // advances within an interval. Freeing it would allow reuse, and
                // reuse is the failure this design exists to avoid.
                _handles.Remove(msg.Removed[i]);
            }
        }

        return msg;
    }

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
    private EntitySnapshot ToMsg(in EntityState e)
    {
        var msg = Rent();
        msg.X = e.Position.X;
        msg.Y = e.Position.Y;
        msg.Hp = e.Hp;
        msg.MaxHp = e.MaxHp;
        EntityTypes.SetType(msg, e.Type);

        if (!_intern)
        {
            // JSON: never intern. The id is the only identifier that encoding has.
            msg.Id = e.Id;
            return msg;
        }

        if (_handles.TryGetValue(e.Id, out uint handle))
        {
            // Already introduced this interval: handle alone.
            msg.Handle = handle;
        }
        else
        {
            // First mention: carry both, so the receiver learns the binding.
            handle = _nextHandle++;
            _handles[e.Id] = handle;
            msg.Handle = handle;
            msg.Id = e.Id;
        }
        return msg;
    }
}
