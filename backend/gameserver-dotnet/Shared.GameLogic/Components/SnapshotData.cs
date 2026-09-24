namespace Shared.GameLogic.Components
{
    /// <summary>
    /// A single entity's visible state in a snapshot.
    /// Ported from Go EntitySnapshot.
    /// <para>
    /// A <b>simulation</b> type, not a wire type: nothing serializes it. The wire
    /// carries <c>RpgMmo.Wire.V1.EntitySnapshot</c> (Protobuf, ADR-9).
    /// </para>
    /// </summary>
    public readonly struct EntitySnapshotData
    {
        public readonly string Id;

        public readonly string Type;

        public readonly float X;

        public readonly float Y;

        public readonly int Hp;

        public readonly int MaxHp;

        /// <summary>
        /// Movement speed in world units per second, as the server is currently
        /// integrating it for this entity.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Exists for client-side prediction, which replays local input through
        /// <see cref="Systems.MovementSystem"/> and therefore needs the same speed the
        /// server used. Without it a client can only assume the spawn default, and any
        /// buff, mount or slow desyncs the two with no error on either side.
        /// </para>
        /// <para>
        /// <b>Zero means "not sent", not "immobile."</b> proto3 elides a zero float, so a
        /// sender predating the wire field is indistinguishable from a stationary entity.
        /// Consumers must treat a non-positive value as absent and fall back to a
        /// configured default — trusting it outright means an old server pins a client's
        /// predicted speed to zero and the local player stops moving.
        /// </para>
        /// </remarks>
        public readonly float Speed;

        /// <summary>
        /// Facing as 16-bit binary radians BIASED BY ONE: 0 means "not sent", and a real
        /// facing is <c>(FacingBrad - 1) * 2*PI / 65536</c> radians counter-clockwise
        /// from +X.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Carried in its RAW WIRE FORM rather than as an angle, for the same reason
        /// <see cref="Speed"/> is carried raw: this struct is a state carrier, and the
        /// conversion is a wire concern. The codec lives in each side's wire layer
        /// (<c>GameServer/Net/FacingCodec.cs</c> on the server,
        /// <c>Runtime/Protocol/FacingCodec.cs</c> in the Unity client) and NOT in this
        /// library, because encoding a direction vector needs <c>MathF.Atan2</c>, which
        /// ADR-10 forbids here — it is implementation-defined across NativeAOT x64 and
        /// IL2CPP ARM64. Entity-id interning is kept out of this library for the same
        /// kind of reason.
        /// </para>
        /// <para>
        /// <b>Zero means "not sent", not "facing east".</b> The +1 bias exists precisely
        /// so those two are distinguishable: 0.0 radians is a perfectly ordinary facing,
        /// so a plain float would make them identical bytes under proto3 elision.
        /// Consumers must keep the entity's last known facing, or derive one from its
        /// movement, rather than snapping to east — otherwise every entity from an old
        /// server points the same way, which reads as a content bug.
        /// </para>
        /// </remarks>
        public readonly uint FacingBrad;

        /// <summary>
        /// What the entity is doing, for animation selection.
        /// </summary>
        /// <remarks>
        /// <see cref="EntityAction.Unspecified"/> (0) means "not sent", never "idle".
        /// A consumer must keep whatever it was showing rather than falling back to
        /// idle, or a sender predating the field freezes every entity in the world into
        /// an idle pose — which looks like a broken animator, not a missing field.
        /// </remarks>
        public readonly EntityAction Action;

        /// <summary>
        /// Retrigger counter for <see cref="Action"/>. 0 means "not sent".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A consumer retriggers when this CHANGES, never when it increases.</b> The
        /// counter wraps at 2^32 and resets when the server restarts or the entity respawns,
        /// so a greater-than test stops retriggering for four billion actions after a single
        /// wrap — with nothing reporting an error.
        /// </para>
        /// <para>
        /// It is carried through the merge for the reason <see cref="FacingBrad"/> is: a
        /// field that decodes correctly and is then dropped here reaches no view at all, and
        /// the symptom is an entity that renders perfectly and never animates a second
        /// swing.
        /// </para>
        /// </remarks>
        public readonly uint ActionSeq;

        /// <summary>
        /// Field-level delta mask from <c>EntitySnapshot.changed_fields</c> (wire.proto field 13,
        /// protocol version 2+). Zero means "all fields present" (full entity state). Non-zero
        /// means this is a partial update: only the bits that are set have valid data; the
        /// <see cref="SnapshotMerger"/> keeps its last-known value for every unset bit.
        /// </summary>
        /// <remarks>
        /// Bit assignments are in <see cref="Systems.SnapshotFieldBits"/>.
        /// </remarks>
        public readonly uint ChangedFields;

        /// <summary>
        /// Constructs entity state without a speed, leaving <see cref="Speed"/> zero —
        /// which consumers read as "not sent".
        /// </summary>
        /// <remarks>
        /// <b>Kept for source compatibility, deliberately.</b> This library is compiled
        /// as source by the Unity client against a pinned tag (ADR-10), so removing this
        /// overload would break that build the moment the tag moved, for every call site
        /// at once. Adding an overload costs nothing; changing a signature costs a
        /// coordinated release.
        /// </remarks>
        public EntitySnapshotData(string id, string type, float x, float y, int hp, int maxHp)
            : this(id, type, x, y, hp, maxHp, 0f)
        {
        }

        /// <summary>
        /// Constructs entity state without a facing or action, leaving both at their
        /// "not sent" values.
        /// </summary>
        /// <remarks>
        /// Kept for source compatibility for the same reason as the overload above: the
        /// Unity client compiles this library from a pinned tag, so a signature change
        /// breaks every call site at once the moment the tag moves, whereas an extra
        /// overload costs nothing.
        /// </remarks>
        public EntitySnapshotData(string id, string type, float x, float y, int hp, int maxHp, float speed)
            : this(id, type, x, y, hp, maxHp, speed, 0u, EntityAction.Unspecified)
        {
        }

        public EntitySnapshotData(
            string id, string type, float x, float y, int hp, int maxHp, float speed,
            uint facingBrad, EntityAction action)
            : this(id, type, x, y, hp, maxHp, speed, facingBrad, action, actionSeq: 0u, changedFields: 0u)
        {
        }

        // REMOVED in 0.6.0: the ten-argument overload taking `changedFields` positionally.
        //
        // Its doc comment claimed source compatibility "with callers that predated
        // ActionSeq" -- but those callers passed NINE arguments, and that overload is still
        // here. The ten-argument form was new API introduced in the same release as the
        // field it served, so it had no caller to be compatible with. What it actually did
        // was capture every pre-existing ten-argument call, whose tenth argument was
        // ActionSeq.
        //
        // WorldState.Apply in the Unity client was one such call. It compiled clean, and
        // the counter landed in `changedFields`: a counter of 12 is a mask asserting
        // Hp|MaxHp are the ONLY fields present, so the next merge reconstructed the entity
        // from stale X, Y, Speed, Facing and Action while believing it was correct. Nothing
        // validates a mask for plausibility, and nothing should -- a mask comes from an
        // encoder, not from a mis-bound argument (Cuvara/Netcode#159, fixed at the call
        // site in #156).
        //
        // Removing it makes that mistake a compile error. Callers that want a mask without
        // a counter pass `actionSeq: 0u, changedFields: mask` to the full constructor, or
        // name the argument.

        /// <summary>
        /// Full constructor including both the retrigger counter and the field-level delta mask.
        /// </summary>
        /// <param name="actionSeq">
        /// Retrigger counter — 0 means "not sent". See <see cref="ActionSeq"/>.
        /// </param>
        /// <param name="changedFields">
        /// Zero for a complete entity snapshot; non-zero for a partial update — see
        /// <see cref="ChangedFields"/> and <see cref="Systems.SnapshotFieldBits"/>.
        /// </param>
        public EntitySnapshotData(
            string id, string type, float x, float y, int hp, int maxHp, float speed,
            uint facingBrad, EntityAction action, uint actionSeq, uint changedFields)
        {
            Id = id;
            Type = type;
            X = x;
            Y = y;
            Hp = hp;
            MaxHp = maxHp;
            Speed = speed;
            FacingBrad = facingBrad;
            Action = action;
            ActionSeq = actionSeq;
            ChangedFields = changedFields;
        }
    }

    /// <summary>
    /// World state snapshot sent to the client each tick.
    /// Ported from Go SnapshotMessage. Simulation type — see
    /// <see cref="EntitySnapshotData"/>; the wire type is
    /// <c>RpgMmo.Wire.V1.SnapshotMessage</c>.
    /// <para>
    /// A snapshot is either a KEYFRAME (<see cref="Full"/> = true: <see cref="Entities"/> is the
    /// complete AOI set and anything absent must be dropped) or a DELTA (<see cref="Entities"/>
    /// holds only entities whose visible state changed since the previous snapshot, and
    /// <see cref="Removed"/> lists entities that left the AOI or the world).
    /// </para>
    /// </summary>
    public readonly struct SnapshotData
    {
        public readonly ulong Tick;

        /// <summary>
        /// Highest client input tick the server has accepted for this player.
        /// The client rewinds to this tick and replays newer inputs to reconcile.
        /// 0 means no input has been accepted yet.
        /// </summary>
        public readonly ulong AckTick;

        /// <summary>True when this snapshot is a keyframe carrying complete AOI state.</summary>
        public readonly bool Full;

        public readonly EntitySnapshotData[] Entities;

        /// <summary>Entity IDs that left the AOI/world. Empty on keyframes.</summary>
        public readonly string[]? Removed;

        public SnapshotData(ulong tick, ulong ackTick, bool full, EntitySnapshotData[] entities, string[]? removed)
        {
            Tick = tick;
            AckTick = ackTick;
            Full = full;
            Entities = entities;
            Removed = removed;
        }

        /// <summary>Convenience constructor for a keyframe with no ack and no removals.</summary>
        public SnapshotData(ulong tick, EntitySnapshotData[] entities)
            : this(tick, 0, true, entities, null)
        {
        }
    }
}
