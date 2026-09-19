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

        /// <summary>
        /// Simulation tick at which the ABILITY cooldown expires. Separate from
        /// <see cref="CooldownUntilTick"/>, which governs the basic attack only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One slot for all abilities, deliberately.</b> This is a global cooldown, not
        /// a per-ability one: casting anything blocks casting anything else until it
        /// expires. Per-ability cooldowns need a map from ability id to tick carried per
        /// entity, and <see cref="EntityState"/> is a flat struct composed on the tick
        /// thread inside the world write lock — adding a dictionary to it would put an
        /// allocation and a hash lookup on the hottest path in the simulation, per entity,
        /// per tick.
        /// </para>
        /// <para>
        /// That is a real gameplay limit and it is stated rather than hidden: a kit where
        /// two abilities must be usable in the same second cannot be expressed yet. Lifting
        /// it is a simulation-state change (a side table keyed by entity, walked only for
        /// entities that cast), not a wire change — <c>InputMessage.ability_id</c> and the
        /// cast event already carry everything a per-ability cooldown would need.
        /// </para>
        /// <para>
        /// A simulation tick, never wall-clock, for the same reason as
        /// <see cref="CooldownUntilTick"/>: replaying an input sequence must produce the
        /// same outcome on the server and on a client.
        /// </para>
        /// </remarks>
        public ulong AbilityCooldownUntilTick;

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

        /// <summary>
        /// Retrigger counter for <see cref="Action"/>, incremented every time the entity
        /// ENTERS an action — including re-entering the one it is already in. 0 means
        /// "not sent".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Action"/> is level-triggered: it reports the state an entity is in,
        /// not that a state was entered. Two attacks in a row therefore produce identical
        /// bytes, and a renderer driving an animator from <c>Action</c> alone plays the
        /// attack once and then holds — the second swing never fires. No amount of
        /// client-side edge detection fixes that, because the edge genuinely is not in the
        /// data. The server is the only party that knows an action was re-entered, so the
        /// edge has to be manufactured here or it does not exist anywhere.
        /// </para>
        /// <para>
        /// <b>A consumer retriggers when this CHANGES, not when it increases.</b> The
        /// counter wraps at 2^32 and resets on restart or respawn, so "greater than" is not
        /// a safe test — a consumer using one stops retriggering for four billion actions
        /// after a single wrap. Inequality has no such failure mode.
        /// </para>
        /// <para>
        /// Allocated from 1, so zero can mean "a sender that predates this field". A
        /// consumer seeing zero keeps its old behaviour rather than treating it as an edge,
        /// which would retrigger every animation on every snapshot from an old server.
        /// </para>
        /// </remarks>
        public uint ActionSeq;
    }
}
