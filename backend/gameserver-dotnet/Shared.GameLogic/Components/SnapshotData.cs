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

        public EntitySnapshotData(string id, string type, float x, float y, int hp, int maxHp, float speed)
        {
            Id = id;
            Type = type;
            X = x;
            Y = y;
            Hp = hp;
            MaxHp = maxHp;
            Speed = speed;
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
