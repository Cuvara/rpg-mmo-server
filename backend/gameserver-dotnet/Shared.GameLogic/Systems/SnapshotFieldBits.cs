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
    }
}
