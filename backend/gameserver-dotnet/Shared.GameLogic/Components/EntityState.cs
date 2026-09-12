namespace Shared.GameLogic.Components
{
    /// <summary>
    /// Pure data component representing the state of a game entity.
    /// Ported from Go Entity struct. No behavior — logic lives in Systems/.
    /// </summary>
    public struct EntityState
    {
        /// <summary>Unique entity identifier. Go: ID string.</summary>
        public string Id;

        /// <summary>Entity type: "player", "npc", "mob", "boss". Go: Type string.</summary>
        public string Type;

        /// <summary>World position. Go: X, Y float32.</summary>
        public Vec2 Position;

        /// <summary>Current hit points. Go: HP int.</summary>
        public int Hp;

        /// <summary>Maximum hit points. Go: MaxHP int.</summary>
        public int MaxHp;

        /// <summary>Attack power. Go: Attack int.</summary>
        public int Attack;

        /// <summary>Defense power. Go: Defense int.</summary>
        public int Defense;

        /// <summary>
        /// Movement speed in <b>world units per second</b>. Consumed by
        /// <c>MovementSystem.Integrate</c> as <c>position += direction * Speed * dt</c>.
        /// A non-positive value means the entity cannot move.
        /// </summary>
        public float Speed;

        /// <summary>Whether this entity is dead. Go: Dead bool.</summary>
        public bool Dead;

        /// <summary>
        /// Simulation tick at which the attack cooldown expires: an attack is allowed when
        /// <c>currentTick &gt;= CooldownUntilTick</c>.
        /// <para>
        /// This is a SIMULATION tick, not <c>DateTime.Ticks</c> — cooldowns must be
        /// deterministic and replayable, so no wall-clock is involved anywhere in combat.
        /// </para>
        /// </summary>
        public ulong CooldownUntilTick;

        /// <summary>Last processed input tick. Go: LastInputTick uint64.</summary>
        public ulong LastInputTick;

        /// <summary>
        /// Facing as 16-bit binary radians BIASED BY ONE: 0 means "not sent / unknown",
        /// and a real facing is <c>(FacingBrad - 1) * 2*PI / 65536</c> radians
        /// counter-clockwise from +X.
        /// </summary>
        /// <remarks>
        /// Stored in the wire's own biased integer form rather than as an angle. The
        /// bias is what lets zero mean "unknown" without colliding with due east (0.0
        /// radians), and keeping the stored form identical to the wire form means the
        /// snapshot encoder does no conversion at all on the hot path. The conversion
        /// itself is a wire concern and lives in each side's wire layer, not here:
        /// producing an angle from a direction needs <c>MathF.Atan2</c>, which ADR-10
        /// forbids in this library because it is implementation-defined across
        /// NativeAOT x64 and IL2CPP ARM64.
        /// </remarks>
        public uint FacingBrad;

        /// <summary>
        /// What the entity is doing, for animation selection on a client.
        /// <see cref="EntityAction.Unspecified"/> (0) means "not sent", never "idle".
        /// </summary>
        public EntityAction Action;
    }
}
