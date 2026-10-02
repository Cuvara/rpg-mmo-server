namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// Bit assignments for <c>EntitySnapshot.changed_fields</c> (wire.proto field 13,
    /// protocol version 2+).
    /// <para>
    /// When <c>changed_fields != 0</c> on a delta entity, only the bits that are set
    /// have their corresponding wire fields present; the receiver must keep its
    /// last-known value for every unset bit. When <c>changed_fields == 0</c>, all
    /// fields are present (same rule as a keyframe entity).
    /// </para>
    /// <para>
    /// Both the server encoder (<c>GameServer.Snapshot.SnapshotDeltaState</c>) and the
    /// client merger (<see cref="SnapshotMerger"/>) must agree on these values. One
    /// definition here is what makes that agreement structural rather than a convention.
    /// </para>
    /// </summary>
    public static class SnapshotFieldBits
    {
        /// <summary>EntitySnapshot.x (field 3)</summary>
        public const uint X          = 0x0001;

        /// <summary>EntitySnapshot.y (field 4)</summary>
        public const uint Y          = 0x0002;

        /// <summary>EntitySnapshot.hp (field 5)</summary>
        public const uint Hp         = 0x0004;

        /// <summary>EntitySnapshot.max_hp (field 6)</summary>
        public const uint MaxHp      = 0x0008;

        /// <summary>EntitySnapshot.type / type_name (fields 7 / 2 — treated as one logical field)</summary>
        public const uint Type       = 0x0010;

        /// <summary>EntitySnapshot.speed (field 9)</summary>
        public const uint Speed      = 0x0020;

        /// <summary>EntitySnapshot.facing_brad (field 10)</summary>
        public const uint FacingBrad = 0x0040;

        /// <summary>EntitySnapshot.action (field 11)</summary>
        public const uint Action     = 0x0080;

        /// <summary>EntitySnapshot.action_seq (field 12)</summary>
        public const uint ActionSeq  = 0x0100;

        // --- Protocol version 3. Sent only to peers that advertised version >= 3; a version 2
        // peer never sees these bits because it never receives the fields they describe. ---

        /// <summary>EntitySnapshot.z (field 14, protocol version 3). Height above the ground plane.</summary>
        public const uint Z          = 0x0200;

        /// <summary>
        /// EntitySnapshot.vel_x / vel_y / vel_z (fields 15-17, protocol version 3). One bit for
        /// all three axes: velocity is one vector and a receiver extrapolating with two fresh
        /// components and one stale one would curve a straight projectile.
        /// </summary>
        public const uint Velocity   = 0x0400;

        /// <summary>
        /// EntitySnapshot.owner / owner_id / spawn_seq (fields 18 / 24 / 19, protocol version 3),
        /// treated as one logical field: who owns the entity and which predicted spawn it
        /// replaces.
        /// </summary>
        public const uint Owner      = 0x0800;

        /// <summary>
        /// EntitySnapshot.stats / stats_removed (fields 20 / 21, protocol version 3). When set
        /// on a delta, <c>stats</c> lists only the changed entries (upserted by stat id) and
        /// <c>stats_removed</c> the ids to drop; every other stat keeps its last-known value.
        /// </summary>
        public const uint Stats      = 0x1000;

        /// <summary>
        /// EntitySnapshot.statuses / statuses_removed (fields 22 / 23, protocol version 3),
        /// merged by <c>effect_id</c> under the same rule as <see cref="Stats"/>.
        /// </summary>
        public const uint Statuses   = 0x2000;

        /// <summary>Every bit protocol version 2 defines.</summary>
        public const uint AllVersion2 = X | Y | Hp | MaxHp | Type | Speed | FacingBrad | Action | ActionSeq;

        /// <summary>Every bit protocol version 3 defines (a superset of <see cref="AllVersion2"/>).</summary>
        public const uint AllVersion3 = AllVersion2 | Z | Velocity | Owner | Stats | Statuses;
    }
}
