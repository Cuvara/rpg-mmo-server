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

        public EntitySnapshotData(string id, string type, float x, float y, int hp, int maxHp)
        {
            Id = id;
            Type = type;
            X = x;
            Y = y;
            Hp = hp;
            MaxHp = maxHp;
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
