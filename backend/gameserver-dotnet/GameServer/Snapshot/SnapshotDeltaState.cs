using System.Runtime.InteropServices;
using GameServer.Net;
using GameServer.World;
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

        public SentView(in EntityView e)
        {
            Id = e.Id;
            Type = e.Type;
            X = e.Position.X;
            Y = e.Position.Y;
            Hp = e.Hp;
            MaxHp = e.MaxHp;
            Speed = e.Speed;
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
            string.Equals(Type, other.Type, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SentView v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(Type, X, Y, Hp, MaxHp, Speed);
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
    /// Legacy <see cref="EntityState"/> entry point, kept for callers (and tests)
    /// that still gather full states. It adapts each state to an
    /// <see cref="EntityView"/> — assigning per-connection negative keys, one string
    /// hash per entity, exactly what the string-keyed maps used to pay — and forwards
    /// to the view core below. One implementation, so the delta/keyframe bookkeeping
    /// cannot diverge between the entry points.
    /// </remarks>
    public SnapshotMessage Encode(ulong tick, ulong ackTick, ReadOnlySpan<EntityState> nearby, int keyframeInterval,
        bool intern = false)
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
            _legacyViews[i] = new EntityView(key, e.Id, e.Type, e.Position, e.Hp, e.MaxHp, e.Speed);
        }
        return Encode(tick, ackTick, _legacyViews.AsSpan(0, nearby.Length), keyframeInterval, intern);
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
        e.Speed = 0f;
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

        // Indexed for-loop, no LINQ, no enumerator boxing: this runs once per client per tick.
        for (int i = 0; i < nearby.Length; i++)
        {
            ref readonly EntityView e = ref nearby[i];
            msg.Entities.Add(ToMsg(in e));
            _lastSent[e.Key] = new SentView(in e);
        }

        return msg;
    }

    private SnapshotMessage EncodeDelta(ulong tick, ulong ackTick, ReadOnlySpan<EntityView> nearby)
    {
        _seen.Clear();
        var msg = BeginMessage(tick, ackTick, full: false);

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
    private EntitySnapshot ToMsg(in EntityView e)
    {
        var msg = Rent();
        msg.X = e.Position.X;
        msg.Y = e.Position.Y;
        msg.Hp = e.Hp;
        msg.MaxHp = e.MaxHp;
        // Written on every mention, including handle-only ones. A client that
        // resolves a handle expects a complete state for that entity; sending
        // speed only when the id is introduced would leave it correct once per
        // keyframe interval and stale in between.
        msg.Speed = e.Speed;
        EntityTypes.SetType(msg, e.Type);

        if (!_intern)
        {
            // JSON: never intern. The id is the only identifier that encoding has.
            msg.Id = e.Id;
            return msg;
        }

        if (_handles.TryGetValue(e.Key, out uint handle))
        {
            // Already introduced this interval: handle alone.
            msg.Handle = handle;
        }
        else
        {
            // First mention: carry both, so the receiver learns the binding.
            handle = _nextHandle++;
            _handles[e.Key] = handle;
            msg.Handle = handle;
            msg.Id = e.Id;
        }
        return msg;
    }
}
