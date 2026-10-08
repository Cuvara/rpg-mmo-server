using System.Runtime.InteropServices;
using Google.Protobuf;
using GameServer.Net;
using GameServer.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
// Both layers legitimately have an EntityAction: the generated wire enum and the shared
// simulation one, which mirror each other by design. Aliased rather than resolved by
// import order so every use below says which layer it means - this file is where the two
// meet, and that is exactly where an implicit choice would be a bug waiting to happen.
using SimAction = Shared.GameLogic.Components.EntityAction;
using WireEventType = RpgMmo.Wire.V1.GameEventType;
using SimEventType = Shared.GameLogic.Components.GameEventType;
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

        /// <summary>
        /// Retrigger counter for <see cref="Action"/>. In equality for a reason the other
        /// fields do not have: it is the ONLY field that changes when an entity repeats an
        /// action it is already in. An attacker swinging twice from a standstill has the
        /// same position, the same HP, the same facing and the same action on both ticks,
        /// so a SentView without this compares the second swing EQUAL to the first, the
        /// delta suppresses it, and the retrigger the counter exists to deliver never
        /// reaches the client. The feature would be dead on arrival and every test of the
        /// keyframe path would still pass.
        /// </summary>
        public readonly uint ActionSeq;

        // ── Protocol 3 scalars (ADR-28/29). ALWAYS zero on a connection below protocol 3, so
        // they cannot make a version 2 entity compare unequal and the version 2 delta stream
        // is exactly what it was. Stats and statuses are not here: they are variable-length
        // and tracked per key in _v3Sent, with the world's change counters as the fast path.

        /// <summary>Height as sent.</summary>
        public readonly float Z;

        /// <summary>Replicated velocity as sent (see <see cref="ReplicatedVelocity"/>).</summary>
        public readonly float VelX;

        /// <inheritdoc cref="VelX"/>
        public readonly float VelY;

        /// <inheritdoc cref="VelX"/>
        public readonly float VelZ;

        /// <summary>
        /// Owner as sent: the owner's handle on an interning connection, 1/0 for "owner_id
        /// written / not written" on JSON. Per connection, because handles are.
        /// </summary>
        public readonly uint Owner;

        /// <summary>spawn_seq as sent to THIS connection (non-zero only for the owner's).</summary>
        public readonly uint SpawnSeq;

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
            ActionSeq = e.ActionSeq;
            Z = 0f;
            VelX = 0f;
            VelY = 0f;
            VelZ = 0f;
            Owner = 0;
            SpawnSeq = 0;
        }

        public SentView(in EntityView e, float velX, float velY, float velZ, uint owner, uint spawnSeq)
            : this(in e)
        {
            Z = e.Z;
            VelX = velX;
            VelY = velY;
            VelZ = velZ;
            Owner = owner;
            SpawnSeq = spawnSeq;
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
            // The retrigger counter is part of visible state, not metadata: two attacks
            // in a row differ ONLY here, so omitting it would make the second identical
            // to the first and the delta encoder would drop it -- reintroducing the exact
            // missed animation this field exists to fix.
            ActionSeq == other.ActionSeq &&
            // Protocol 3 scalars: zero on both sides for a version 2 connection.
            Z.Equals(other.Z) &&
            VelX.Equals(other.VelX) &&
            VelY.Equals(other.VelY) &&
            VelZ.Equals(other.VelZ) &&
            Owner == other.Owner &&
            SpawnSeq == other.SpawnSeq &&
            string.Equals(Type, other.Type, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SentView v && Equals(v);
        public override int GetHashCode() =>
            HashCode.Combine(Type, X, Y, Hp, MaxHp, Speed, FacingBrad, (Action, ActionSeq));
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
    /// Latched from <see cref="FieldDelta"/> at the start of each Encode. True when the
    /// connection has negotiated protocol version 2+ AND uses Protobuf (interning is a
    /// prerequisite, because <c>changed_fields</c> is a Protobuf-only field).
    /// </summary>
    private bool _fieldDelta;

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

    /// <summary>
    /// Reused <see cref="GameEvent"/> instances, mirroring the entity pool. Events are rare
    /// per tick but the pool is what keeps a burst of them — a wave of AoE deaths — from
    /// allocating per event per observer.
    /// </summary>
    private readonly List<GameEvent> _eventPool = new();

    private int _eventPoolUsed;
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
    /// Per-factor importance weights for this connection
    /// (<c>GAMESERVER_IMPORTANCE_*</c>). Default is
    /// <see cref="ReplicationImportance.Weights.Legacy"/> — every factor zero, every score
    /// zero, every comparison on that key a tie, and therefore the pre-importance ordering
    /// byte for byte.
    /// </summary>
    public ReplicationImportance.Weights ImportanceWeights { get; set; } =
        ReplicationImportance.Weights.Legacy;

    /// <summary>
    /// The AOI radius this connection is gathered with
    /// (<see cref="Server.AoiSettings"/>), so the distance factor can be normalised by it.
    /// </summary>
    /// <remarks>
    /// Carried here rather than passed per encode because it is a property of the
    /// deployment, not of a snapshot, and threading it through <c>Encode</c> would put a
    /// constant in the hot signature. Defaults to the compiled-in radius so a caller that
    /// never sets it still normalises against something sane rather than dividing by zero.
    /// </remarks>
    public float AoiRadius { get; set; } = GameConstants.DefaultAoiRadius;

    /// <summary>
    /// Per-importance send intervals (<c>GAMESERVER_REPLICATION_SCHEDULE</c>). Default is
    /// <see cref="Server.ReplicationSchedule.Off"/>: every dirty entity is due every world
    /// tick, which is the pre-schedule behaviour.
    /// </summary>
    public Server.ReplicationSchedule Schedule { get; set; } = Server.ReplicationSchedule.Off;

    /// <summary>
    /// Whether this connection has negotiated field-level delta encoding (protocol version
    /// 2+). When true, delta snapshots carry only the fields that changed since the last
    /// send, and the wire's <c>EntitySnapshot.changed_fields</c> mask names which ones.
    /// <para>
    /// Set once after the join handshake, before any Encode call, alongside the other
    /// per-connection delta-encoder flags (<see cref="MaxSnapshotBytes"/>,
    /// <see cref="ImportanceWeights"/>, etc.). Defaults to false so a caller that never
    /// sets it stays on the pre-version-2 encoding, exactly as <see cref="Schedule"/> and
    /// <see cref="ImportanceWeights"/> default to their no-op values.
    /// </para>
    /// <para>
    /// Field-level delta requires interning: <c>changed_fields</c> is a Protobuf-only field,
    /// and an interned handle proves the receiver already holds the entity's last state to
    /// merge against. Both conditions are checked in <see cref="Encode"/>; setting this flag
    /// on a non-Protobuf connection has no effect.
    /// </para>
    /// </summary>
    public bool FieldDelta { get; set; }

    /// <summary>
    /// Wire protocol version the peer advertised in its join (0 = unversioned). Set once after
    /// the handshake, like <see cref="FieldDelta"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Version 3 or higher</b> (ADR-28..30) turns on, for this connection only: z, velocity,
    /// owner (+ spawn_seq for the owner's own connection), the stat block and status effects,
    /// <c>GameEvent.effect_id</c> and event types 7-9, and projectile/item ENTITIES, which are
    /// otherwise never sent. On a delta they use mask bits <c>0x0200</c>-<c>0x2000</c> when
    /// <see cref="FieldDelta"/> is also on.
    /// </para>
    /// <para>
    /// <b>Below 3</b> the encoder produces exactly the protocol 2 bytes: none of the above is
    /// written, compared or sent (<c>V2WireIdentityTests</c> pins it against the pre-v3 encoder).
    /// </para>
    /// </remarks>
    public uint PeerProtocolVersion { get; set; }

    /// <summary>Latched per encode: <see cref="PeerProtocolVersion"/> &gt;= 3.</summary>
    private bool _v3;

    /// <summary>
    /// <c>ack_applied_tick</c> of the encode in progress: the base tick that applied the input
    /// <c>ack_tick</c> acknowledges. Always zero below protocol 3.
    /// </summary>
    private ulong _ackAppliedTick;

    /// <summary>Protocol 3 variable-length data of the encode in progress; null = none gathered.</summary>
    private SnapshotV3Gather? _gather;

    /// <summary>Observer's stable key for the encode in progress (spawn_seq addressing).</summary>
    private int _observerKey = PendingGameEvent.NoKey;

    /// <summary>
    /// What this connection was last sent of an entity's stat block and statuses, keyed like
    /// <see cref="_lastSent"/> and kept in lockstep with it (written only where an entity is
    /// committed, erased wherever <see cref="_lastSent"/> forgets a key). Protocol 3 only.
    /// </summary>
    private readonly Dictionary<int, V3Sent> _v3Sent = new();

    /// <summary>Recycled <see cref="V3Sent"/> records, so a keyframe or despawn allocates nothing.</summary>
    private readonly List<V3Sent> _v3Free = new();

    /// <summary>
    /// Handles the budget's sizing pass WOULD assign, so an owner or status source introduced
    /// earlier in the same message is measured exactly as the emit pass will write it.
    /// </summary>
    private readonly Dictionary<int, uint> _prospective = new();

    /// <summary>True only during the budget's all-fits sizing pass: lookups consult <see cref="_prospective"/>.</summary>
    private bool _sizingProspective;

    // Pools for the repeated wire sub-messages, mirroring the entity pool. The "scratch" pair
    // serves the sizing instance and is reset on every measurement.
    private readonly List<StatValue> _statPool = new();
    private int _statPoolUsed;
    private readonly List<StatusEffect> _statusPool = new();
    private int _statusPoolUsed;
    private readonly List<StatValue> _scratchStatPool = new();
    private int _scratchStatPoolUsed;
    private readonly List<StatusEffect> _scratchStatusPool = new();
    private int _scratchStatusPoolUsed;

    /// <summary>One status as sent: the effect with its source already in this connection's terms.</summary>
    private readonly struct SentStatus
    {
        public readonly uint EffectId;
        public readonly uint Stacks;
        public readonly ulong ExpiresTick;
        public readonly uint Source;

        public SentStatus(uint effectId, uint stacks, ulong expiresTick, uint source)
        {
            EffectId = effectId;
            Stacks = stacks;
            ExpiresTick = expiresTick;
            Source = source;
        }
    }

    /// <summary>Last-sent stat block and statuses of one entity (protocol 3).</summary>
    private sealed class V3Sent
    {
        public StatValueData[] Stats = new StatValueData[4];
        public int StatCount;
        public SentStatus[] Statuses = new SentStatus[4];
        public int StatusCount;
        public uint StatsVersion;
        public uint StatusesVersion;
    }

    /// <summary>
    /// The rate of the tick counter this encoder is HANDED, for converting configured
    /// milliseconds into that same unit. This is the BASE (critical) rate.
    /// </summary>
    /// <remarks>
    /// <b>Base, not world, and the difference was a live defect.</b> Snapshots are built on
    /// the world group, so "every 2 snapshots" is the natural way to think about an
    /// interval — but the <c>tick</c> the encoder receives is <c>TickLoop.CurrentTick</c>,
    /// the authoritative simulation tick, which advances at the CRITICAL rate. At the 60/15
    /// default it therefore jumps by 4 between consecutive snapshots. Converting 133ms into
    /// 2 world ticks and then comparing against a counter that moves 4 per snapshot makes
    /// every entity due every time: the schedule reported zero deferrals on a live server
    /// while every unit test passed, because the tests fed it a counter that advanced by 1.
    ///
    /// <para>Converting into base ticks instead makes the arithmetic come out right for the
    /// same configured milliseconds: 133ms is 8 base ticks, which is exactly 2 snapshots at
    /// 60/15, and 266ms is 16, which is 4.</para>
    /// </remarks>
    public int TickHz { get; set; } = Server.SimulationRates.DefaultCriticalHz;

    /// <summary>
    /// Base ticks per emission, i.e. <c>SimulationRates.WorldEvery</c>. Snapshots go out on
    /// world ticks, so this is what turns an interval in base ticks into the wait an entity
    /// actually takes — see <see cref="Server.ReplicationSchedule.IntervalTicksFor(float,int,int)"/>.
    /// <para>Defaulted to the shipped rates rather than to 1, because 1 means "no
    /// quantisation" and would let a caller that forgot to set it silently get the
    /// unquantised behaviour, which is the failure this field exists to remove.</para>
    /// </summary>
    public int WorldEvery { get; set; } = Server.SimulationRates.Default.WorldEvery;

    /// <summary>
    /// World tick each entity was last actually emitted on, keyed like
    /// <see cref="_lastSent"/>. Absent means "never sent to this connection".
    /// </summary>
    /// <remarks>
    /// Pruned everywhere <see cref="_lastSent"/> is: on despawn commit, on keyframe, and in
    /// <see cref="PruneDeferrals"/>. Miss one and the map grows for the life of the
    /// connection with entries for entities that left long ago -- a leak whose only symptom
    /// is memory, on a per-connection object.
    /// </remarks>
    private readonly Dictionary<int, ulong> _lastSentTick = new();

    /// <summary>Scores for the current candidate set, parallel to <see cref="_candidates"/>.</summary>
    private readonly List<float> _candidateScores = new();

    private long _entitiesDeferredByInterval;
    private int _maxStateAge;
    private long _reportedDeferredByInterval;

    /// <summary>Entity updates withheld because their tier was not due.</summary>
    public long EntitiesDeferredByInterval => Interlocked.Read(ref _entitiesDeferredByInterval);

    /// <summary>
    /// Longest gap, in world ticks, between an entity's state going stale for this client
    /// and being re-sent. High-water mark.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="MaxShedAge"/>, which counts BUDGET deferrals. A
    /// schedule deferral never touches <c>_shedAge</c> -- a not-due entity is not a shed
    /// entity -- so the existing gauge is blind to it, and reading one for the other would
    /// report a healthy zero while entities went seconds without an update.
    /// </remarks>
    public int MaxStateAge => Volatile.Read(ref _maxStateAge);

    /// <summary>
    /// For each entity currently owed an update, the world tick from which the client's copy
    /// counts as stale: the later of its last send and the tick before it was first withheld.
    /// Set by whichever deferral source withholds it first — the schedule or the budget — and
    /// consumed when it is finally sent. Absent means "nothing owed".
    /// </summary>
    /// <remarks>
    /// The later of its last send and the previous encode, not simply the last send: an
    /// entity that stood still for two seconds and then moved was not stale during the two
    /// seconds — the client's copy was correct as of the previous snapshot — so its wait
    /// starts there, not when it was last sent.
    /// </remarks>
    private readonly Dictionary<int, ulong> _owedFrom = new();

    private int _maxUpdateGap;

    /// <summary>
    /// Longest wait, in <b>base</b> ticks (the encoder is handed TickLoop's tick), between an
    /// entity's last send and the send that delivered an update it was owed — <b>whatever
    /// withheld it</b>. High-water mark. Published in milliseconds as
    /// <c>max_update_gap_ms</c>, converted where the base rate is known.
    /// </summary>
    /// <remarks>
    /// <para>This is the number the client's interpolation cover is spent on, and neither
    /// <see cref="MaxStateAge"/> nor <see cref="MaxShedAge"/> is it (#421). Each counts only
    /// its own source: the schedule's age stops at the tick the entity comes due, the
    /// budget's starts counting there, and an entity that is scheduled out and then shed
    /// waits for the two together (BENCHMARK.md Part XX).</para>
    ///
    /// <para>An arrival gap at the client is this plus whatever the link spreads it by. Its
    /// budget is <c>ReplicationSchedule.ClientInterpolationBudgetMs</c> minus the link's
    /// share.</para>
    ///
    /// <para>A keyframe does not break a wait: an entity owed across it keeps its start and is
    /// recorded when the keyframe (or the delta after a truncated keyframe) carries it.</para>
    /// </remarks>
    public int MaxUpdateGap => Volatile.Read(ref _maxUpdateGap);

    /// <summary>Entities currently owed an update. Bounded by the visible set. Diagnostics/tests.</summary>
    public int OwedRecords => _owedFrom.Count;

    /// <summary>Start (or keep) the wait of an entity that is being withheld this tick.</summary>
    private void NoteOwed(int key, ulong tick)
    {
        if (_owedFrom.ContainsKey(key)) return;
        ulong from = _prevEncodingTick < tick ? _prevEncodingTick : 0;
        if (_lastSentTick.TryGetValue(key, out ulong last) && last > from) from = last;
        _owedFrom[key] = from;
    }

    /// <summary>End the wait of an entity being sent this tick, if it had one.</summary>
    private void NoteSent(int key, ulong tick)
    {
        if (!_owedFrom.Remove(key, out ulong from) || tick <= from) return;
        ulong gap = tick - from;
        if (gap > int.MaxValue) gap = int.MaxValue;
        if ((int)gap > _maxUpdateGap) Volatile.Write(ref _maxUpdateGap, (int)gap);
    }

    /// <summary>Drop waits of entities not in this encode's visible set.</summary>
    private void PruneOwed()
    {
        if (_owedFrom.Count == 0) return;
        foreach (var kv in _owedFrom)
        {
            if (!_seen.Contains(kv.Key)) _removedKeys.Add(kv.Key);
        }
        for (int i = 0; i < _removedKeys.Count; i++) _owedFrom.Remove(_removedKeys[i]);
        _removedKeys.Clear();
    }

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

    /// <summary>Schedule counters since the last call, same delta discipline as above.</summary>
    internal void TakeScheduleCounters(out long deferredByInterval, out int maxStateAge, out int maxUpdateGap)
    {
        long d = EntitiesDeferredByInterval;
        deferredByInterval = d - _reportedDeferredByInterval;
        _reportedDeferredByInterval = d;
        maxStateAge = MaxStateAge;
        maxUpdateGap = MaxUpdateGap;
    }

    /// <summary>One candidate's scheduling key. Struct, sorted in a reused array.</summary>
    private struct CandidateSort
    {
        public int Age;
        public float Score;
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
            int g = b.Score.CompareTo(a.Score);                // higher score first
            if (g != 0) return g;
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

    /// <summary>Tick of the encode in flight, so the emit path can stamp send times.</summary>
    private ulong _encodingTick;

    /// <summary>
    /// Tick of the encode before this one. Snapshots are encoded on WORLD ticks while the tick
    /// counter is the BASE tick, so "the previous snapshot" is not <c>tick - 1</c> — at 60/20
    /// it is <c>tick - 3</c>. Using <c>tick - 1</c> made the first live run of
    /// <see cref="MaxUpdateGap"/> under-read every gap by two base ticks.
    /// </summary>
    private ulong _prevEncodingTick;

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
            _legacyViews[i] = new EntityView(
                key, e.Id, e.Type, e.Position, e.Hp, e.MaxHp, e.Speed, e.FacingBrad, e.Action, e.ActionSeq);
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
        bool intern = false, Vec2 observer = default,
        ReadOnlySpan<PendingGameEvent> events = default, int observerKey = PendingGameEvent.NoKey,
        SnapshotV3Gather? v3 = null, ulong ackAppliedTick = 0)
    {
        _intern = intern;
        _v3 = PeerProtocolVersion >= 3;
        // ack_applied_tick is protocol 3 only. It is additive and a protocol 2 receiver would
        // skip it, but V2WireIdentityTests pins the protocol 2 bytes against the pre-v3
        // encoder, and a field nobody below 3 reads is not worth breaking that pin for. Latched
        // per encode and written by BeginMessage, so every path (keyframe, delta, budgeted)
        // carries it and the pooled message never keeps a previous encode's value.
        _ackAppliedTick = _v3 ? ackAppliedTick : 0;
        // Only meaningful when it describes THIS span; a gather of another size is ignored
        // rather than indexed out of step with the entities.
        _gather = _v3 && v3 != null && v3.EntityCount == nearby.Length ? v3 : null;
        _observerKey = observerKey;
        // Field-level delta requires interning: changed_fields is a Protobuf-only field,
        // and an interned handle proves the receiver already has the entity's last state.
        // Gating it on intern rather than FieldDelta alone means a caller that sets the
        // flag on a JSON connection still gets the safe (all-fields) path.
        _fieldDelta = intern && FieldDelta;
        _observer = observer;
        _prevEncodingTick = _encodingTick;
        _encodingTick = tick;
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
            // Events are appended AFTER the entity pass, never before, and that ordering is
            // load-bearing: an event is addressed by handle, and a handle only exists once
            // the entity pass has introduced it. Appending first would emit events naming
            // handles the receiver has no binding for, which is the one failure the
            // interning contract says a receiver must answer with a resync.
            var fullMsg = EncodeFull(tick, ackTick, nearby);
            AppendEvents(fullMsg, events, observerKey);
            return fullMsg;
        }

        _sinceKeyframe++;
        var deltaMsg = EncodeDelta(tick, ackTick, nearby);
        AppendEvents(deltaMsg, events, observerKey);
        return deltaMsg;
    }

    /// <summary>
    /// Filters this tick's events down to the ones this connection may see and writes them
    /// into <paramref name="msg"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Visibility is decided against what this connection has been told, not against
    /// distance.</b> An entity is visible to this connection exactly when it is in
    /// <see cref="_lastSent"/> — which is the encoder's model of what the client knows, and
    /// is maintained by the AOI pass that just ran. Re-deriving visibility from positions
    /// here would be a second, independently wrong answer to a question the entity pass has
    /// already answered; the two would disagree at the edge of the circle, and the symptom
    /// would be damage numbers for entities that are not on screen.
    /// </para>
    /// <para>
    /// <b>Either participant is enough.</b> A player who sees the victim but not the
    /// attacker still needs the damage number: the alternative is a health bar that drops
    /// with no explanation. The attacker's handle is simply left at zero in that case, which
    /// a receiver reads as "no source".
    /// </para>
    /// <para>
    /// <b>Private events are addressed, not broadcast.</b> Experience and levels reach only
    /// the connection whose own entity is the subject. An event channel that leaked another
    /// player's progression would be an information disclosure shipped as a feature.
    /// </para>
    /// </remarks>
    private void AppendEvents(SnapshotMessage msg, ReadOnlySpan<PendingGameEvent> events, int observerKey)
    {
        // The overwhelmingly common case: a tick in which nothing happened. Costs one
        // length check per connection per tick.
        if (events.Length == 0) return;

        for (int i = 0; i < events.Length; i++)
        {
            ref readonly PendingGameEvent pending = ref events[i];

            // Event types 7-9 (status applied/removed, projectile hit) are protocol 3: a
            // version 2 peer was never promised them and receives exactly the pre-v3 stream.
            if (!_v3 && IsV3EventType(pending.Data.Type)) continue;

            uint sourceHandle = 0;
            uint targetHandle = 0;
            bool sourceKnown = pending.HasSource && IsKnown(pending.SourceKey, out sourceHandle);
            bool targetKnown = pending.HasTarget && IsKnown(pending.TargetKey, out targetHandle);

            if (pending.Data.IsPrivate)
            {
                // Addressed to its subject alone. Compared by key rather than by id string
                // because the observer is identified to this encoder by key everywhere else,
                // and mixing the two would be one more place they could disagree.
                if (observerKey == PendingGameEvent.NoKey || pending.TargetKey != observerKey) continue;
            }
            else if (!sourceKnown && !targetKnown)
            {
                continue;
            }

            var wire = RentEvent();
            wire.Type = (WireEventType)pending.Data.Type;
            wire.Amount = pending.Data.Amount;
            wire.AbilityId = pending.Data.AbilityId;
            wire.Flags = (uint)pending.Data.Flags;
            if (_v3) wire.EffectId = pending.Data.EffectId;

            if (_intern)
            {
                // A participant this connection does not know is reported as absent rather
                // than by id: sending the id would name an entity the client has never been
                // told about, which is a disclosure the AOI exists to prevent.
                wire.Source = sourceKnown ? sourceHandle : 0u;
                wire.Target = targetKnown ? targetHandle : 0u;
            }
            else
            {
                // JSON has no handle table, so the ids are the only names available. Same
                // visibility rule: an unknown participant is omitted, not named.
                wire.SourceId = sourceKnown ? (pending.Data.SourceId ?? "") : "";
                wire.TargetId = targetKnown ? (pending.Data.TargetId ?? "") : "";
            }

            msg.Events.Add(wire);
        }
    }

    /// <summary>
    /// Whether this connection has been told about <paramref name="key"/>, and its handle
    /// if it is interning.
    /// </summary>
    private bool IsKnown(int key, out uint handle)
    {
        handle = 0;
        if (!_lastSent.ContainsKey(key)) return false;
        if (_intern) _handles.TryGetValue(key, out handle);
        return true;
    }

    /// <summary>
    /// A cleared <see cref="GameEvent"/> from the pool. Every field is reset for the reason
    /// <see cref="Rent"/> gives: a pooled message that kept a previous event's source would
    /// attach it to whichever event rents the object next, which is a wrong value rather
    /// than a missing one.
    /// </summary>
    private GameEvent RentEvent()
    {
        GameEvent e;
        if (_eventPoolUsed < _eventPool.Count)
        {
            e = _eventPool[_eventPoolUsed];
        }
        else
        {
            e = new GameEvent();
            _eventPool.Add(e);
        }
        _eventPoolUsed++;

        e.Type = WireEventType.Unspecified;
        e.Source = 0;
        e.Target = 0;
        e.Amount = 0;
        e.AbilityId = 0;
        e.Flags = 0;
        e.SourceId = "";
        e.TargetId = "";
        e.EffectId = 0;
        return e;
    }

    /// <summary>Game event types a protocol 2 peer is never sent.</summary>
    private static bool IsV3EventType(SimEventType type) =>
        type is SimEventType.StatusApplied or SimEventType.StatusRemoved or SimEventType.ProjectileHit;

    /// <summary>
    /// Reset the reused message and hand back every pooled entity in one step. Called at
    /// the top of each encode, which is what makes "return" a no-op rather than a call
    /// anyone can forget or repeat.
    /// </summary>
    private SnapshotMessage BeginMessage(ulong tick, ulong ackTick, bool full)
    {
        _message.Entities.Clear();
        _message.Removed.Clear();
        _message.Events.Clear();
        _eventPoolUsed = 0;
        _statPoolUsed = 0;
        _statusPoolUsed = 0;
        _message.Tick = tick;
        _message.AckTick = ackTick;
        _message.AckAppliedTick = _ackAppliedTick;
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
        e.ActionSeq = 0;
        e.ChangedFields = 0;
        ResetV3(e);
        return e;
    }

    /// <summary>
    /// Clear every protocol 3 field of a wire entity. Called on every rented and every
    /// measured instance: on a version 2 connection these stay at their proto3 defaults and
    /// cost zero bytes, which is what keeps the version 2 bytes unchanged.
    /// </summary>
    private static void ResetV3(EntitySnapshot e)
    {
        e.Z = 0f;
        e.VelX = 0f;
        e.VelY = 0f;
        e.VelZ = 0f;
        e.Owner = 0;
        e.OwnerId = "";
        e.SpawnSeq = 0;
        if (e.Stats.Count > 0) e.Stats.Clear();
        if (e.StatsRemoved.Count > 0) e.StatsRemoved.Clear();
        if (e.Statuses.Count > 0) e.Statuses.Clear();
        if (e.StatusesRemoved.Count > 0) e.StatusesRemoved.Clear();
    }

    private SnapshotMessage EncodeFull(ulong tick, ulong ackTick, ReadOnlySpan<EntityView> nearby)
    {
        var msg = BeginMessage(tick, ackTick, full: true);

        // Send intervals are NOT consulted on this path, and that is a correctness rule
        // rather than an optimisation. A keyframe is the complete visible set by definition
        // and the client DISCARDS anything it does not list (wire.proto), so withholding an
        // entity here would not make it a beat late -- it would make it vanish until some
        // later delta happened to carry it.
        _lastSent.Clear();
        ReleaseAllV3();
        // A keyframe is the synchronisation point for the handle space: both
        // sides drop every binding and start again from 1.
        _handles.Clear();
        _lastSentTick.Clear();
        _nextHandle = 1;

        if (_budgetBytes <= 0)
        {
            // Pre-budget path, unchanged.
            _shedAge.Clear();

            // Indexed for-loop, no LINQ, no enumerator boxing: this runs once per client per tick.
            for (int i = 0; i < nearby.Length; i++)
            {
                ref readonly EntityView e = ref nearby[i];
                if (!_v3 && IsV3OnlyEntity(in e)) continue;
                msg.Entities.Add(ToMsg(in e, i));
                _lastSent[e.Key] = ViewOf(in e);
                CommitV3(in e, i);
                // Stamped here as well as in EmitEntity. This path does not go through it,
                // and an entity introduced on an unbudgeted keyframe with no send tick looks
                // to the schedule like one that was never sent -- so it would be due on the
                // very next delta and the schedule would silently do nothing for any
                // deployment running GAMESERVER_MAX_SNAPSHOT_BYTES=0.
                NoteSent(e.Key, tick);
                _lastSentTick[e.Key] = tick;
            }
            // Everything visible was just sent; what is left owed is out of sight.
            _owedFrom.Clear();

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
        _candidateScores.Clear();
        _seen.Clear();
        for (int i = 0; i < nearby.Length; i++)
        {
            if (!_v3 && IsV3OnlyEntity(in nearby[i])) continue;
            _candidates.Add(i);
            // Scored here too, so _candidateScores is always parallel to _candidates
            // whichever path built them. The keyframe path does not USE the score to decide
            // anything -- intervals never apply to a keyframe -- but the budget sort runs on
            // both paths, and a sort reading a list that is only populated on one of them is
            // an index-out-of-range waiting for the first budgeted keyframe.
            _candidateScores.Add(ScoreOf(in nearby[i], DistanceSqTo(in nearby[i])));
            // Filled on the keyframe path too, purely so the deferral bookkeeping can be
            // pruned against it below — a keyframe has no use for _seen otherwise.
            _seen.Add(nearby[i].Key);
        }
        _pendingRemovals.Clear(); // a keyframe carries no despawn list by definition

        return EncodeBudgeted(msg, nearby, HeaderBytes(tick, ackTick, _ackAppliedTick, full: true));
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
            if (!_v3 && IsV3OnlyEntity(in e)) continue;
            _seen.Add(e.Key);

            var view = ViewOf(in e);
            bool known = _lastSent.TryGetValue(e.Key, out var prev);
            if (known && prev.Equals(view) && !V3Dirty(in e, i))
                continue; // unchanged since last send -> omit

            // The schedule applies on this path too. It is a SEPARATE concern from the byte
            // budget -- that one answers "how much of this snapshot may I spend", this one
            // answers "is this entity due at all" -- and gating it on the budget being
            // enabled would mean GAMESERVER_MAX_SNAPSHOT_BYTES=0, documented as disabling
            // only the budget, silently disabled the schedule as well. With the schedule off
            // (the default) this branch is unreachable and the path is byte-for-byte what it
            // was.
            if (known && Schedule.Enabled && !DueNow(in e, in prev, tick, ScoreOf(in e, DistanceSqTo(in e))))
            {
                Interlocked.Increment(ref _entitiesDeferredByInterval);
                NoteStateAge(e.Key, tick);
                continue;
            }

            msg.Entities.Add(ToMsg(in e, i));
            // Recomputed after the write, not reused: the owner handle is per connection and
            // the record must say exactly what the bytes said.
            _lastSent[e.Key] = _v3 ? ViewOf(in e) : view;
            CommitV3(in e, i);
            NoteSent(e.Key, tick);
            _lastSentTick[e.Key] = tick;
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
                ReleaseV3(_removedKeys[i]);
                // The handle is NOT returned to the pool: _nextHandle only ever
                // advances within an interval. Freeing it would allow reuse, and
                // reuse is the failure this design exists to avoid.
                _handles.Remove(_removedKeys[i]);
            }
            _removedKeys.Clear();
        }
        PruneOwed();

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
        _candidateScores.Clear();

        for (int i = 0; i < nearby.Length; i++)
        {
            ref readonly EntityView e = ref nearby[i];
            if (!_v3 && IsV3OnlyEntity(in e)) continue;
            _seen.Add(e.Key);

            bool known = _lastSent.TryGetValue(e.Key, out var prev);
            if (known && prev.Equals(ViewOf(in e)) && !V3Dirty(in e, i))
            {
                // Nothing owed on this entity: the client's copy already matches. That
                // includes an entity which was deferred earlier and has since drifted
                // back to precisely the values last sent — rare, but it must clear the
                // deferral or the entity would keep a priority it no longer deserves.
                _shedAge.Remove(e.Key);
                continue;
            }

            float score = ScoreOf(in e, DistanceSqTo(in e));

            // An entity this connection has NEVER been sent bypasses the schedule entirely:
            // its "last sent" is never, and a client that receives a handle it has no
            // binding for must ask for a keyframe (wire.proto) -- which costs far more than
            // the update being withheld.
            if (known && Schedule.Enabled && !DueNow(in e, in prev, tick, score))
            {
                // Withheld, NOT removed from the span and NOT recorded as sent. Leaving
                // _lastSent untouched is what makes this safe: when the entity is next due
                // it is compared against what the client actually holds, so the snapshot
                // carries its CURRENT state rather than a queued intermediate one.
                Interlocked.Increment(ref _entitiesDeferredByInterval);
                NoteStateAge(e.Key, tick);
                continue;
            }

            _candidates.Add(i);
            _candidateScores.Add(score);
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

        return EncodeBudgeted(msg, nearby, HeaderBytes(tick, ackTick, _ackAppliedTick, full: false));
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
        // Protocol 3 only: owner and status-source handles introduced earlier in this same
        // message are measured as the emit pass below will write them.
        _prospective.Clear();
        _sizingProspective = _v3;
        for (int c = 0; c < _candidates.Count; c++)
        {
            int index = _candidates[c];
            ref readonly EntityView e = ref nearby[index];
            bool introduce = !_intern || !_handles.ContainsKey(e.Key);
            uint handle = 0;
            if (_intern)
            {
                if (introduce) handle = prospective++;
                else handle = _handles[e.Key];
            }
            if (_fieldDelta && !introduce && _lastSent.TryGetValue(e.Key, out SentView prev))
                Fill(_sizingScratch, in e, index, handle, introduce, fieldDelta: true, hasPrev: true, prev, sizing: true);
            else
                Fill(_sizingScratch, in e, index, handle, introduce, sizing: true);
            total += EntryBytes(_sizingScratch);
            if (_v3 && _intern && introduce) _prospective[e.Key] = handle;
            else if (_v3 && !_intern) _prospective[e.Key] = 1;
        }
        _sizingProspective = false;
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
                int index = _candidates[c];
                EmitEntity(msg, in nearby[index], index);
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
        PruneOwed();
        if (_shedAge.Count > 0)
        {
            foreach (var kv in _shedAge)
            {
                if (!_seen.Contains(kv.Key)) _removedKeys.Add(kv.Key);
            }
            for (int i = 0; i < _removedKeys.Count; i++) _shedAge.Remove(_removedKeys[i]);
            _removedKeys.Clear();
        }

        // Send-tick records for entities that are no longer visible. Kept in step with
        // _shedAge deliberately: both are per-entity bookkeeping bounded by the visible set,
        // and one growing without bound while the other does not would be a leak whose only
        // symptom is memory, on a per-connection object that lives as long as the session.
        if (_lastSentTick.Count > _lastSent.Count)
        {
            foreach (var kv in _lastSentTick)
            {
                if (!_lastSent.ContainsKey(kv.Key)) _removedKeys.Add(kv.Key);
            }
            for (int i = 0; i < _removedKeys.Count; i++) _lastSentTick.Remove(_removedKeys[i]);
            _removedKeys.Clear();
        }
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
            float distanceSq = (dx * dx) + (dy * dy);
            _sortBuffer[c] = new CandidateSort
            {
                Age = age,
                // Reused, never recomputed. The candidate pass already scored this entity to
                // decide whether it was due; scoring it again would be waste and, worse,
                // could disagree with itself the day the two call sites drift apart -- which
                // would defer an entity on one score and order it on another.
                Score = _candidateScores[c],
                DistanceSq = distanceSq,
                Index = index,
                Self = IsSelf(in e),
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
            int topIndex = _sortBuffer[0].Index;
            used += EmitEntity(msg, in nearby[topIndex], topIndex);
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
            int index = _sortBuffer[i].Index;
            ref readonly EntityView e = ref nearby[index];
            int size = MeasureEntity(in e, index);
            if (used + size > _budgetBytes)
            {
                for (int j = i; j < n; j++) shed += Defer(in nearby[_sortBuffer[j].Index]);
                break;
            }
            used += EmitEntity(msg, in e, index);
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
    /// <summary>
    /// Gameplay importance for one candidate, from state already in hand at the sort.
    /// </summary>
    /// <remarks>
    /// Returns 0 without touching anything when every weight is zero, which is the shipped
    /// default: the whole term then costs one struct field compare per candidate and the
    /// comparison it feeds always ties.
    ///
    /// <para><b>The "changed" inputs are computed against <see cref="_lastSent"/>, not
    /// against the previous tick.</b> What matters is whether THIS connection has been told,
    /// and two connections seeing the same entity legitimately disagree about that — one may
    /// have been shed last snapshot. Reading a world-level "changed this tick" flag would
    /// score an entity as fresh for a client that never received the change.</para>
    /// </remarks>
    private float ScoreOf(in EntityView e, float distanceSq)
    {
        ReplicationImportance.Weights w = ImportanceWeights;
        if (w.AllZero) return 0f;

        bool hpChanged = true, actionChanged = true;
        if (_lastSent.TryGetValue(e.Key, out SentView prev))
        {
            hpChanged = prev.Hp != e.Hp || prev.MaxHp != e.MaxHp;
            actionChanged = prev.Action != e.Action || prev.ActionSeq != e.ActionSeq;
        }

        var inputs = new ReplicationImportance.Inputs(
            distanceSq,
            AoiRadius,
            isPlayer: EntityTypes.IsPlayer(e.Type),
            action: e.Action,
            hpChanged: hpChanged,
            actionChanged: actionChanged);

        return ReplicationImportance.Score(in inputs, in w);
    }

    /// <summary>Squared distance from the observer to an entity.</summary>
    private float DistanceSqTo(in EntityView e)
    {
        float dx = e.Position.X - _observer.X;
        float dy = e.Position.Y - _observer.Y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// Whether a known, changed entity is due this world tick.
    /// </summary>
    /// <remarks>
    /// <b>An edge is never deferred.</b> Health and the action retrigger counter are the two
    /// things a client can neither interpolate nor dead-reckon, and the counter exists
    /// precisely because an action is an occurrence rather than a state. Withholding either
    /// is not a late update, it is a dropped event -- the client would never learn the hit
    /// or the swing happened at all, because the next snapshot carries only the state
    /// afterwards. Position and facing are safe to defer; these are not.
    ///
    /// <b>Nor is the observer's own entity, ever.</b> "Position is safe to defer" holds
    /// because the client interpolates or dead-reckons it -- true of every entity except
    /// the one the client is PREDICTING. For self the position IS the reconciliation
    /// anchor: withholding it does not delay a remote body by an interval, it lets the
    /// local prediction diverge for that interval and then corrects it in one step. The
    /// priority sort has said so since it was written (rule 1 of
    /// <see cref="CandidateComparer"/>: "a stale one reads as rubber-banding, the single
    /// most-noticed netcode artefact") -- the schedule shipped without the same rule, and a
    /// three-client play session measured the result as a 0.0041 -> 0.3333 jump in the
    /// client's reported lastCorrection. Self scores 5 under the "balanced" profile
    /// (distance 2 + type 3, with no HP or action edge to add), which lands in the 133ms
    /// tier: predicting at 60Hz against an anchor arriving at 7.5Hz.
    ///
    /// This is a rule, not a weight. <c>GAMESERVER_IMPORTANCE_W_TYPE=7</c> also stops the
    /// stutter, by lifting EVERY player over the top tier -- which protects a player 49
    /// units away exactly as much as the one being predicted, and gives back a third of
    /// the saving to do it.
    /// </remarks>
    private bool DueNow(in EntityView e, in SentView prev, ulong tick, float score)
    {
        if (IsSelf(in e)) return true;
        // ADR-29 decision 1: a projectile is due every world tick. It lives for a fraction of
        // a second, and a receiver dead-reckoning one from a stale snapshot draws it through
        // whatever it already hit.
        if (ReplicationImportance.IsAlwaysDue(e.Type)) return true;
        if (prev.Hp != e.Hp || prev.MaxHp != e.MaxHp) return true;
        if (prev.Action != e.Action || prev.ActionSeq != e.ActionSeq) return true;

        int interval = Schedule.IntervalTicksFor(score, TickHz, WorldEvery);
        if (interval <= 1) return true;

        if (!_lastSentTick.TryGetValue(e.Key, out ulong last)) return true;
        return tick - last >= (ulong)interval;
    }

    /// <summary>
    /// Whether <paramref name="e"/> is the entity belonging to the connection this state
    /// encodes for. One definition, read by both the priority sort and the schedule: they
    /// disagreeing about what "self" means is the failure this replaces.
    /// </summary>
    private bool IsSelf(in EntityView e) =>
        SelfId != null && string.Equals(e.Id, SelfId, StringComparison.Ordinal);

    /// <summary>Track how long this entity has gone without the update it is owed.</summary>
    private void NoteStateAge(int key, ulong tick)
    {
        NoteOwed(key, tick);
        if (!_lastSentTick.TryGetValue(key, out ulong last)) return;
        ulong age = tick - last;
        if (age > int.MaxValue) age = int.MaxValue;
        if ((int)age > _maxStateAge) Volatile.Write(ref _maxStateAge, (int)age);
    }

    private void CommitRemovals(SnapshotMessage msg, int count)
    {
        for (int r = 0; r < count; r++)
        {
            int key = _pendingRemovals[r];
            msg.Removed.Add(_lastSent[key].Id);
            _lastSent.Remove(key);
            ReleaseV3(key);
            // The handle is NOT returned to the pool: _nextHandle only ever advances
            // within an interval. Freeing it would allow reuse, and reuse is the failure
            // this design exists to avoid.
            _handles.Remove(key);
            _shedAge.Remove(key);
            _lastSentTick.Remove(key);
            _owedFrom.Remove(key);
        }
    }

    /// <summary>
    /// Commit one entity to the wire and to the encoder's model of what the client has.
    /// Returns the bytes it added to the payload.
    /// </summary>
    private int EmitEntity(SnapshotMessage msg, in EntityView e, int index)
    {
        var ent = ToMsg(in e, index);
        msg.Entities.Add(ent);
        _lastSent[e.Key] = ViewOf(in e);
        CommitV3(in e, index);
        NoteSent(e.Key, _encodingTick);
        _lastSentTick[e.Key] = _encodingTick;
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
        NoteOwed(e.Key, _encodingTick);
        _shedAge.TryGetValue(e.Key, out int age);
        age++;
        _shedAge[e.Key] = age;
        if (age > _maxShedAge) Volatile.Write(ref _maxShedAge, age);
        return 1;
    }

    /// <summary>
    /// Exact wire size of one candidate as the emit path would write it, measured through
    /// the same <see cref="Fill"/>. Must mirror <see cref="ToMsg"/> exactly, because the
    /// budget spends the budget on this measurement and then emits at that size; any
    /// divergence makes the byte-cap wrong by that many bytes per entity.
    /// </summary>
    private int MeasureEntity(in EntityView e, int index)
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

        if (_fieldDelta && !introduce && _lastSent.TryGetValue(e.Key, out SentView prev))
            Fill(_sizingScratch, in e, index, handle, introduce, fieldDelta: true, hasPrev: true, prev, sizing: true);
        else
            Fill(_sizingScratch, in e, index, handle, introduce, sizing: true);
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
    private static int HeaderBytes(ulong tick, ulong ackTick, ulong ackAppliedTick, bool full)
        => (tick != 0 ? 1 + CodedOutputStream.ComputeUInt64Size(tick) : 0)
         + (ackTick != 0 ? 1 + CodedOutputStream.ComputeUInt64Size(ackTick) : 0)
         + (ackAppliedTick != 0 ? 1 + CodedOutputStream.ComputeUInt64Size(ackAppliedTick) : 0)
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
    private EntitySnapshot ToMsg(in EntityView e, int index)
    {
        var msg = Rent();

        if (!_intern)
        {
            // JSON: never intern. The id is the only identifier that encoding has.
            Fill(msg, in e, index, handle: 0, introduceId: true);
            return msg;
        }

        if (_handles.TryGetValue(e.Key, out uint handle))
        {
            // Already introduced this interval: handle alone.
            // With field-level delta, look up the previous state to compute the mask.
            // The lookup is always valid: _handles and _lastSent are updated in lockstep
            // (EmitEntity writes both; keyframes clear both), so a present handle
            // guarantees a present SentView.
            if (_fieldDelta && _lastSent.TryGetValue(e.Key, out SentView prev))
                Fill(msg, in e, index, handle, introduceId: false, fieldDelta: true, hasPrev: true, prev);
            else
                Fill(msg, in e, index, handle, introduceId: false);
        }
        else
        {
            // First mention: carry both id and all fields, so the receiver can construct
            // a complete initial state. Field-level delta does NOT apply here: the client
            // has no previous state to merge against.
            handle = _nextHandle++;
            _handles[e.Key] = handle;
            Fill(msg, in e, index, handle, introduceId: true);
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
    /// written on every mention, including handle-only ones on the full-field path: a
    /// client that resolves a handle expects complete state for that entity, and sending
    /// speed only with the id would leave it correct once per keyframe interval and stale
    /// in between.</para>
    ///
    /// <para>Every field is assigned unconditionally so the scratch instance carries no
    /// residue from the previous candidate it measured. <see cref="Rent"/> makes the same
    /// guarantee for pooled instances, for the same reason.</para>
    ///
    /// <para><b>Field-level delta path</b> (<paramref name="hasPrev"/> = true, not
    /// introducing). Only the fields that changed since <paramref name="prev"/> are
    /// written; <c>changed_fields</c> is set to the corresponding mask. Unset fields stay
    /// at their <see cref="Rent"/>-reset proto3 defaults and are elided by the serialiser.
    /// The receiver keeps its last-known values for those fields. This path is unreachable
    /// on a first introduction: a new entity always travels the full-field path so the
    /// client can construct a complete initial state.</para>
    /// </remarks>
    /// <param name="hasPrev">
    /// True when <paramref name="prev"/> contains the previously sent state for this
    /// entity (i.e. the entity is already known to the client). Only meaningful when
    /// <paramref name="fieldDelta"/> is also true.
    /// </param>
    private void Fill(EntitySnapshot msg, in EntityView e, int index, uint handle, bool introduceId,
        bool fieldDelta = false, bool hasPrev = false, in SentView prev = default, bool sizing = false)
    {
        if (sizing)
        {
            // The measuring instance is refilled for every candidate: its sub-message pools
            // restart with it, so a measurement never holds more than one entity's worth.
            _scratchStatPoolUsed = 0;
            _scratchStatusPoolUsed = 0;
        }

        // Protocol 3 fields are cleared on every fill, so neither a pooled nor the measuring
        // instance can carry a previous entity's z, owner or stat block. On a version 2
        // connection they then stay at their proto3 defaults and cost zero bytes.
        ResetV3(msg);

        if (fieldDelta && hasPrev && !introduceId)
        {
            // Field-level delta: write only the fields that differ from what the client
            // was last told, and record which ones via changed_fields. The fields that do
            // NOT change are left at the Rent()-reset proto3 defaults (zero / empty /
            // Unspecified) so the serialiser elides them — they cost zero bytes.
            //
            // "Rent()-reset" holds for a pooled instance but NOT for the measuring instance,
            // which is refilled candidate after candidate: without this reset it kept the
            // previous candidate's unchanged fields and the budget over-measured every partial
            // entity by them. Clearing here is a no-op for a pooled instance.
            //
            // Protocol 3 connections only. The over-measurement is conservative (it can only
            // shed early, never exceed the budget), and fixing it for protocol 2 moves which
            // entity a tight budget sheds - i.e. moves protocol 2 bytes, which this change
            // promises not to do (V2WireIdentityTests, tight-budget case, fails if applied).
            if (_v3)
            {
                msg.X = 0f;
                msg.Y = 0f;
                msg.Hp = 0;
                msg.MaxHp = 0;
                msg.Speed = 0f;
                msg.FacingBrad = 0;
                msg.Action = WireAction.Unspecified;
                msg.ActionSeq = 0;
                msg.TypeName = "";
                msg.Type = EntityType.Unspecified;
            }
            uint mask = 0;

            if (e.Position.X != prev.X)
            {
                mask |= SnapshotFieldBits.X;
                msg.X = e.Position.X;
            }
            if (e.Position.Y != prev.Y)
            {
                mask |= SnapshotFieldBits.Y;
                msg.Y = e.Position.Y;
            }
            if (e.Hp != prev.Hp)
            {
                mask |= SnapshotFieldBits.Hp;
                msg.Hp = e.Hp;
            }
            if (e.MaxHp != prev.MaxHp)
            {
                mask |= SnapshotFieldBits.MaxHp;
                msg.MaxHp = e.MaxHp;
            }
            if (!string.Equals(e.Type, prev.Type, StringComparison.Ordinal))
            {
                mask |= SnapshotFieldBits.Type;
                EntityTypes.SetType(msg, e.Type);
            }
            if (e.Speed != prev.Speed)
            {
                mask |= SnapshotFieldBits.Speed;
                msg.Speed = e.Speed;
            }
            if (e.FacingBrad != prev.FacingBrad)
            {
                mask |= SnapshotFieldBits.FacingBrad;
                msg.FacingBrad = e.FacingBrad;
            }
            if (e.Action != prev.Action)
            {
                mask |= SnapshotFieldBits.Action;
                msg.Action = (WireAction)e.Action;
            }
            if (e.ActionSeq != prev.ActionSeq)
            {
                mask |= SnapshotFieldBits.ActionSeq;
                msg.ActionSeq = e.ActionSeq;
            }

            if (_v3)
            {
                mask |= FillV3Delta(msg, in e, index, in prev, sizing);

                // A mask of 0 means "every field present" (wire.proto), so a partial entity
                // must never carry one. Version 2 cannot get here with 0 (SentView covers every
                // field it writes); version 3 could only in a corner where a status source's
                // handle moved and moved back within one message. Writing x is always valid.
                if (mask == 0)
                {
                    mask = SnapshotFieldBits.X;
                    msg.X = e.Position.X;
                }
            }

            msg.Handle = handle;
            msg.Id = ""; // never introducing — handle already known to the client
            msg.ChangedFields = mask;
            // TypeName and Type are at their Rent()-reset defaults (empty/"Unspecified")
            // if the type bit is absent. msg.TypeName = ""; msg.Type = Unspecified; are
            // already guaranteed by Rent().
            return;
        }

        // Full entity snapshot: keyframe entity, first introduction, or non-field-delta
        // connection. Every field is written; changed_fields stays 0 (proto3 default).
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
        msg.ActionSeq = e.ActionSeq;
        msg.Handle = handle;
        msg.Id = introduceId ? e.Id : "";
        msg.TypeName = "";
        msg.Type = EntityType.Unspecified;
        EntityTypes.SetType(msg, e.Type);
        msg.ChangedFields = 0;

        if (_v3) FillV3Full(msg, in e, index, sizing);
    }

    // ───────────────────────────── protocol 3 (ADR-28..30) ─────────────────────────────

    /// <summary>Projectile and item entities exist only for protocol 3 peers.</summary>
    private static bool IsV3OnlyEntity(in EntityView e) =>
        string.Equals(e.Type, Gameplay.CombatResolver.ProjectileType, StringComparison.Ordinal)
        || string.Equals(e.Type, Gameplay.CombatResolver.ItemType, StringComparison.Ordinal);

    /// <summary>
    /// The velocity this encoder replicates: a projectile's always, a character's only while
    /// it moves vertically (airborne), zero otherwise.
    /// </summary>
    /// <remarks>
    /// wire.proto: velocity is "sent for entities whose motion is ballistic or extrapolated".
    /// A walking character is interpolated between snapshots, and its ground velocity changes on
    /// every start, stop and turn, so replicating it would add a field to most deltas for nothing
    /// a receiver uses. Zero on all three axes is the documented "stationary or not sent".
    /// </remarks>
    private static void ReplicatedVelocity(in EntityView e, out float vx, out float vy, out float vz)
    {
        if (e.VelZ != 0f || string.Equals(e.Type, Gameplay.CombatResolver.ProjectileType, StringComparison.Ordinal))
        {
            vx = e.VelX;
            vy = e.VelY;
            vz = e.VelZ;
            return;
        }

        vx = 0f;
        vy = 0f;
        vz = 0f;
    }

    /// <summary>
    /// The owner (or a status source) in this connection's terms: its handle when interning,
    /// 1 when JSON may name it. 0 when there is none or this connection has not been told
    /// about it - an unknown entity is never named, the same disclosure rule events follow.
    /// </summary>
    private uint EntityToken(int key)
    {
        if (key == 0 || key == PendingGameEvent.NoKey) return 0;
        if (_intern)
        {
            if (_handles.TryGetValue(key, out uint h)) return h;
            if (_sizingProspective && _prospective.TryGetValue(key, out h)) return h;
            return 0;
        }

        if (_lastSent.ContainsKey(key)) return 1;
        return _sizingProspective && _prospective.ContainsKey(key) ? 1u : 0u;
    }

    /// <summary>
    /// Status source handle. JSON has no source-id field on <c>StatusEffect</c>, so a JSON
    /// connection always gets 0 ("unknown").
    /// </summary>
    private uint SourceToken(int key) => _intern ? EntityToken(key) : 0u;

    /// <summary>spawn_seq for this connection: only the owner's own connection is told (ADR-29.2).</summary>
    private uint SpawnSeqFor(in EntityView e)
    {
        if (e.SpawnSeq == 0 || e.OwnerKey == 0) return 0;
        bool own = _observerKey != PendingGameEvent.NoKey
            ? e.OwnerKey == _observerKey
            : SelfId != null && string.Equals(e.OwnerId, SelfId, StringComparison.Ordinal);
        return own ? e.SpawnSeq : 0u;
    }

    /// <summary>The record of what this connection holds for <paramref name="e"/>, in its own terms.</summary>
    private SentView ViewOf(in EntityView e)
    {
        if (!_v3) return new SentView(in e);
        ReplicatedVelocity(in e, out float vx, out float vy, out float vz);
        return new SentView(in e, vx, vy, vz, EntityToken(e.OwnerKey), SpawnSeqFor(in e));
    }

    private void WriteOwner(EntitySnapshot msg, in EntityView e, uint owner)
    {
        if (_intern) msg.Owner = owner;
        else msg.OwnerId = owner != 0 ? e.OwnerId ?? "" : "";
    }

    private ReadOnlySpan<StatValueData> StatsAt(int index) =>
        _gather != null ? _gather.StatsOf(index) : default;

    private ReadOnlySpan<GatheredStatus> StatusesAt(int index) =>
        _gather != null ? _gather.StatusesOf(index) : default;

    private void FillV3Full(EntitySnapshot msg, in EntityView e, int index, bool sizing)
    {
        msg.Z = e.Z;
        ReplicatedVelocity(in e, out float vx, out float vy, out float vz);
        msg.VelX = vx;
        msg.VelY = vy;
        msg.VelZ = vz;
        WriteOwner(msg, in e, EntityToken(e.OwnerKey));
        msg.SpawnSeq = SpawnSeqFor(in e);

        // Complete sets: a keyframe, an introduction and a full-replace entity all REPLACE the
        // receiver's copy, so anything not listed here is gone on the receiving side.
        ReadOnlySpan<StatValueData> stats = StatsAt(index);
        for (int i = 0; i < stats.Length; i++) msg.Stats.Add(RentStat(sizing, stats[i].StatId, stats[i].Value));

        ReadOnlySpan<GatheredStatus> statuses = StatusesAt(index);
        for (int i = 0; i < statuses.Length; i++)
        {
            ref readonly GatheredStatus s = ref statuses[i];
            msg.Statuses.Add(RentStatus(sizing, s.EffectId, s.Stacks, s.ExpiresTick, SourceToken(s.SourceKey)));
        }
    }

    private uint FillV3Delta(EntitySnapshot msg, in EntityView e, int index, in SentView prev, bool sizing)
    {
        uint mask = 0;

        if (e.Z != prev.Z)
        {
            mask |= SnapshotFieldBits.Z;
            msg.Z = e.Z;
        }

        ReplicatedVelocity(in e, out float vx, out float vy, out float vz);
        if (vx != prev.VelX || vy != prev.VelY || vz != prev.VelZ)
        {
            // One bit for the vector: a receiver extrapolating two fresh axes and one stale
            // one would curve a straight projectile.
            mask |= SnapshotFieldBits.Velocity;
            msg.VelX = vx;
            msg.VelY = vy;
            msg.VelZ = vz;
        }

        uint owner = EntityToken(e.OwnerKey);
        uint seq = SpawnSeqFor(in e);
        if (owner != prev.Owner || seq != prev.SpawnSeq)
        {
            mask |= SnapshotFieldBits.Owner;
            WriteOwner(msg, in e, owner);
            msg.SpawnSeq = seq;
        }

        _v3Sent.TryGetValue(e.Key, out V3Sent? sent);

        // Stats: changed entries upserted by id, vanished ids in stats_removed.
        bool statsChanged = false;
        ReadOnlySpan<StatValueData> stats = StatsAt(index);
        for (int i = 0; i < stats.Length; i++)
        {
            ref readonly StatValueData s = ref stats[i];
            if (sent != null && TryFindStat(sent, s.StatId, out int old) && old == s.Value) continue;
            msg.Stats.Add(RentStat(sizing, s.StatId, s.Value));
            statsChanged = true;
        }
        if (sent != null)
        {
            for (int i = 0; i < sent.StatCount; i++)
            {
                uint id = sent.Stats[i].StatId;
                if (!ContainsStat(stats, id))
                {
                    msg.StatsRemoved.Add(id);
                    statsChanged = true;
                }
            }
        }
        if (statsChanged) mask |= SnapshotFieldBits.Stats;

        // Statuses: same rule, keyed by effect_id; an entry changed when its stacks, expiry or
        // source (in this connection's terms) changed.
        bool statusesChanged = false;
        ReadOnlySpan<GatheredStatus> statuses = StatusesAt(index);
        for (int i = 0; i < statuses.Length; i++)
        {
            ref readonly GatheredStatus s = ref statuses[i];
            uint source = SourceToken(s.SourceKey);
            if (sent != null && TryFindStatus(sent, s.EffectId, out SentStatus old)
                && old.Stacks == s.Stacks && old.ExpiresTick == s.ExpiresTick && old.Source == source)
                continue;
            msg.Statuses.Add(RentStatus(sizing, s.EffectId, s.Stacks, s.ExpiresTick, source));
            statusesChanged = true;
        }
        if (sent != null)
        {
            for (int i = 0; i < sent.StatusCount; i++)
            {
                uint id = sent.Statuses[i].EffectId;
                if (!ContainsStatus(statuses, id))
                {
                    msg.StatusesRemoved.Add(id);
                    statusesChanged = true;
                }
            }
        }
        if (statusesChanged) mask |= SnapshotFieldBits.Statuses;

        return mask;
    }

    /// <summary>
    /// Whether the stat block or statuses of a KNOWN entity differ from what this connection
    /// was last sent. The world's change counters are the fast path; content is compared only
    /// when a counter moved, and a counter that moved without a visible change is absorbed so
    /// the comparison is not repeated every tick.
    /// </summary>
    private bool V3Dirty(in EntityView e, int index)
    {
        if (!_v3) return false;
        if (!_v3Sent.TryGetValue(e.Key, out V3Sent? sent))
            return StatsAt(index).Length > 0 || StatusesAt(index).Length > 0;

        if (sent.StatsVersion == e.StatsVersion && sent.StatusesVersion == e.StatusesVersion) return false;

        if (StatsDiffer(sent, StatsAt(index)) || StatusesDiffer(sent, StatusesAt(index))) return true;

        // Same content under a new counter: what the client holds is still exact.
        sent.StatsVersion = e.StatsVersion;
        sent.StatusesVersion = e.StatusesVersion;
        return false;
    }

    private static bool StatsDiffer(V3Sent sent, ReadOnlySpan<StatValueData> current)
    {
        if (sent.StatCount != current.Length) return true;
        for (int i = 0; i < current.Length; i++)
        {
            if (!TryFindStat(sent, current[i].StatId, out int v) || v != current[i].Value) return true;
        }
        return false;
    }

    private bool StatusesDiffer(V3Sent sent, ReadOnlySpan<GatheredStatus> current)
    {
        if (sent.StatusCount != current.Length) return true;
        for (int i = 0; i < current.Length; i++)
        {
            ref readonly GatheredStatus s = ref current[i];
            if (!TryFindStatus(sent, s.EffectId, out SentStatus old)) return true;
            if (old.Stacks != s.Stacks || old.ExpiresTick != s.ExpiresTick || old.Source != SourceToken(s.SourceKey))
                return true;
        }
        return false;
    }

    private static bool TryFindStat(V3Sent sent, uint id, out int value)
    {
        for (int i = 0; i < sent.StatCount; i++)
        {
            if (sent.Stats[i].StatId == id)
            {
                value = sent.Stats[i].Value;
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static bool TryFindStatus(V3Sent sent, uint effectId, out SentStatus status)
    {
        for (int i = 0; i < sent.StatusCount; i++)
        {
            if (sent.Statuses[i].EffectId == effectId)
            {
                status = sent.Statuses[i];
                return true;
            }
        }
        status = default;
        return false;
    }

    private static bool ContainsStat(ReadOnlySpan<StatValueData> stats, uint id)
    {
        for (int i = 0; i < stats.Length; i++) if (stats[i].StatId == id) return true;
        return false;
    }

    private static bool ContainsStatus(ReadOnlySpan<GatheredStatus> statuses, uint id)
    {
        for (int i = 0; i < statuses.Length; i++) if (statuses[i].EffectId == id) return true;
        return false;
    }

    /// <summary>
    /// Record what was just written for <paramref name="e"/>: the complete current stat block
    /// and statuses (in this connection's terms), and the counters they correspond to. Called
    /// on exactly the lines that record <see cref="_lastSent"/>, after the write.
    /// </summary>
    private void CommitV3(in EntityView e, int index)
    {
        if (!_v3) return;
        if (!_v3Sent.TryGetValue(e.Key, out V3Sent? sent))
        {
            if (_v3Free.Count > 0)
            {
                sent = _v3Free[^1];
                _v3Free.RemoveAt(_v3Free.Count - 1);
            }
            else
            {
                sent = new V3Sent();
            }
            _v3Sent[e.Key] = sent;
        }

        ReadOnlySpan<StatValueData> stats = StatsAt(index);
        if (sent.Stats.Length < stats.Length) sent.Stats = new StatValueData[stats.Length + 2];
        stats.CopyTo(sent.Stats);
        sent.StatCount = stats.Length;

        ReadOnlySpan<GatheredStatus> statuses = StatusesAt(index);
        if (sent.Statuses.Length < statuses.Length) sent.Statuses = new SentStatus[statuses.Length + 2];
        for (int i = 0; i < statuses.Length; i++)
        {
            ref readonly GatheredStatus s = ref statuses[i];
            sent.Statuses[i] = new SentStatus(s.EffectId, s.Stacks, s.ExpiresTick, SourceToken(s.SourceKey));
        }
        sent.StatusCount = statuses.Length;
        sent.StatsVersion = e.StatsVersion;
        sent.StatusesVersion = e.StatusesVersion;
    }

    /// <summary>Forget one entity's protocol 3 record (it left this connection's view).</summary>
    private void ReleaseV3(int key)
    {
        if (_v3Sent.Remove(key, out V3Sent? sent)) _v3Free.Add(sent);
    }

    /// <summary>Forget every protocol 3 record (keyframe).</summary>
    private void ReleaseAllV3()
    {
        if (_v3Sent.Count == 0) return;
        foreach (var kv in _v3Sent) _v3Free.Add(kv.Value);
        _v3Sent.Clear();
    }

    /// <summary>Entities this connection holds a protocol 3 record for. Diagnostics/tests.</summary>
    internal int V3Records => _v3Sent.Count;

    private StatValue RentStat(bool sizing, uint id, int value)
    {
        List<StatValue> pool = sizing ? _scratchStatPool : _statPool;
        ref int used = ref sizing ? ref _scratchStatPoolUsed : ref _statPoolUsed;
        StatValue v;
        if (used < pool.Count) v = pool[used];
        else
        {
            v = new StatValue();
            pool.Add(v);
        }
        used++;
        v.StatId = id;
        v.Value = value;
        return v;
    }

    private StatusEffect RentStatus(bool sizing, uint effectId, uint stacks, ulong expiresTick, uint source)
    {
        List<StatusEffect> pool = sizing ? _scratchStatusPool : _statusPool;
        ref int used = ref sizing ? ref _scratchStatusPoolUsed : ref _statusPoolUsed;
        StatusEffect s;
        if (used < pool.Count) s = pool[used];
        else
        {
            s = new StatusEffect();
            pool.Add(s);
        }
        used++;
        s.EffectId = effectId;
        s.Stacks = stacks;
        s.ExpiresTick = expiresTick;
        s.Source = source;
        return s;
    }
}
