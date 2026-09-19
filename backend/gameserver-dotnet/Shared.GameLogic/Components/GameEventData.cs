using System;

namespace Shared.GameLogic.Components
{
    /// <summary>
    /// The kind of an edge-triggered occurrence. Mirrors the <c>GameEventType</c> enum in
    /// <c>shared/proto/wire.proto</c>; the numeric values are on the wire and are FROZEN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an event channel exists beside the snapshot.</b> Everything else the server
    /// sends is level-triggered state — where an entity is, its HP, what it is doing. That
    /// is the right shape for state and the wrong shape for occurrences. "Took 12 damage"
    /// is not recoverable from two HP values a tick apart: a heal and a hit in the same
    /// tick net out, a delta snapshot may omit the entity entirely, and an entity leaving
    /// the AOI simply stops reporting. A client inferring damage numbers from HP deltas is
    /// wrong in exactly the cases a player notices, and wrong silently.
    /// </para>
    /// <para>
    /// <b>Zero is reserved for "not sent".</b> Same rule as <see cref="EntityAction"/> and
    /// the wire's <c>EntityType</c>. A consumer MUST ignore an event whose type it does not
    /// recognise rather than guess: events are presentation, so dropping an unknown one
    /// costs a missing damage number while guessing costs a wrong one.
    /// </para>
    /// </remarks>
    public enum GameEventType
    {
        /// <summary>Not sent / unknown. A consumer must ignore the event.</summary>
        Unspecified = 0,

        /// <summary>
        /// <see cref="GameEventData.Target"/> took <see cref="GameEventData.Amount"/>
        /// damage from <see cref="GameEventData.Source"/>. The amount is the damage
        /// APPLIED, after mitigation — the number a player expects to see, not the
        /// pre-defense roll.
        /// </summary>
        Damage = 1,

        /// <summary>Target was healed Amount by Source.</summary>
        Heal = 2,

        /// <summary>
        /// Target died. Source is the killer, or absent when nothing killed it.
        /// </summary>
        /// <remarks>
        /// NOT redundant with <see cref="EntityAction.Dead"/>, and the difference is why
        /// this channel exists: <c>Dead</c> is a state that persists as long as the corpse
        /// does, so a client that arrives afterwards sees Dead and cannot tell whether the
        /// death just happened. The event says it happened NOW, which is what a death
        /// animation, a sound and a kill feed each need.
        /// </remarks>
        Death = 3,

        /// <summary>
        /// Source successfully cast <see cref="GameEventData.AbilityId"/>. Emitted when the
        /// cast RESOLVES on the server, the only moment both sides agree on.
        /// </summary>
        AbilityCast = 4,

        /// <summary>Target gained Amount experience. Private to the subject.</summary>
        XpGain = 5,

        /// <summary>Target reached level Amount. Private to the subject.</summary>
        LevelUp = 6,
    }

    /// <summary>
    /// Presentation flags on a <see cref="GameEventData"/>.
    /// </summary>
    /// <remarks>
    /// A bitfield rather than more <see cref="GameEventType"/> values because these
    /// COMBINE — a critical periodic tick is one event, not two — and because a consumer
    /// that does not understand a bit ignores it and still shows a correct number, where an
    /// unrecognised enum value means the whole event is dropped.
    /// </remarks>
    [Flags]
    public enum GameEventFlags : uint
    {
        None = 0,

        /// <summary>A critical hit or critical heal.</summary>
        Critical = 1u << 0,

        /// <summary>The target was immune, or the effect was fully mitigated.</summary>
        Immune = 1u << 1,

        /// <summary>
        /// The effect came from a periodic source (a damage-over-time tick, a regeneration
        /// tick) rather than a direct action.
        /// </summary>
        Periodic = 1u << 2,
    }

    /// <summary>
    /// One edge-triggered occurrence produced by the simulation, in SIMULATION terms:
    /// entities are named by their string ids, not by wire handles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a simulation type, not a wire type.</b> Nothing serializes it. The wire
    /// carries <c>RpgMmo.Wire.V1.GameEvent</c>, whose <c>source</c>/<c>target</c> are
    /// per-connection interned handles — and interning is a property of a CONNECTION, not
    /// of the world. The simulation produces one event; the encoder turns it into as many
    /// wire events as there are connections entitled to see it, each with that
    /// connection's own handles. Putting handles in here would mean the simulation
    /// producing a different event per observer, which is not a thing the simulation can
    /// know.
    /// </para>
    /// <para>
    /// <b>Visibility is decided by the encoder, not here.</b> <see cref="IsPrivate"/> says
    /// whether an event is addressed to its subject alone; the AOI rule that decides
    /// everything else lives on the server beside the entity filter it mirrors. A
    /// simulation type that tried to answer "who may see this" would need the observer,
    /// and it does not have one.
    /// </para>
    /// <para>
    /// A readonly struct with no heap fields beyond the two ids: events are produced on the
    /// tick thread inside the world write lock, at a rate that scales with combat activity
    /// rather than with time, and a per-event allocation there is the kind of cost that
    /// only shows up under the load nobody tests at.
    /// </para>
    /// </remarks>
    public readonly struct GameEventData
    {
        public GameEventData(
            GameEventType type,
            string? sourceId,
            string? targetId,
            int amount,
            uint abilityId,
            GameEventFlags flags)
        {
            Type = type;
            SourceId = sourceId;
            TargetId = targetId;
            Amount = amount;
            AbilityId = abilityId;
            Flags = flags;
        }

        public GameEventType Type { get; }

        /// <summary>
        /// Id of the entity that caused this, or null for "none / the world".
        /// </summary>
        public string? SourceId { get; }

        /// <summary>Id of the entity this happened to, or null when the event has no subject.</summary>
        public string? TargetId { get; }

        /// <summary>
        /// Magnitude, meaning per <see cref="Type"/>: damage dealt, health restored,
        /// experience gained, level reached.
        /// </summary>
        /// <remarks>
        /// Signed, but damage and healing are both reported POSITIVE under their own event
        /// types rather than as one signed quantity — a consumer colouring a number by its
        /// sign would render mitigated-to-zero damage and a zero heal identically. The sign
        /// stays available for the cases that genuinely need it without overloading the
        /// common ones.
        /// </remarks>
        public int Amount { get; }

        /// <summary>Content id of the ability involved, 0 when none.</summary>
        public uint AbilityId { get; }

        public GameEventFlags Flags { get; }

        /// <summary>
        /// True when this event is addressed to <see cref="TargetId"/> alone and must not
        /// be sent to any other connection.
        /// </summary>
        /// <remarks>
        /// Progression is the private class: another player's experience gain is not
        /// theirs to see, and an event channel that leaked it would be an information
        /// disclosure shipped as a feature. Combat events are public to whoever can see a
        /// participant, which is the same AOI rule the entity set already obeys.
        /// </remarks>
        public bool IsPrivate => Type == GameEventType.XpGain || Type == GameEventType.LevelUp;

        /// <summary>Damage dealt by <paramref name="sourceId"/> to <paramref name="targetId"/>.</summary>
        public static GameEventData Damage(
            string? sourceId, string targetId, int amount, uint abilityId = 0,
            GameEventFlags flags = GameEventFlags.None) =>
            new GameEventData(GameEventType.Damage, sourceId, targetId, amount, abilityId, flags);

        /// <summary>Healing applied to <paramref name="targetId"/>.</summary>
        public static GameEventData Heal(
            string? sourceId, string targetId, int amount, uint abilityId = 0,
            GameEventFlags flags = GameEventFlags.None) =>
            new GameEventData(GameEventType.Heal, sourceId, targetId, amount, abilityId, flags);

        /// <summary>
        /// <paramref name="targetId"/> died. <paramref name="killerId"/> is null when
        /// nothing killed it.
        /// </summary>
        public static GameEventData Death(string? killerId, string targetId) =>
            new GameEventData(GameEventType.Death, killerId, targetId, 0, 0, GameEventFlags.None);

        /// <summary><paramref name="casterId"/> resolved a cast of <paramref name="abilityId"/>.</summary>
        public static GameEventData AbilityCast(string casterId, string? targetId, uint abilityId) =>
            new GameEventData(GameEventType.AbilityCast, casterId, targetId, 0, abilityId, GameEventFlags.None);

        public override string ToString() =>
            $"{Type}({SourceId ?? "-"} -> {TargetId ?? "-"}, amount={Amount}, ability={AbilityId}, flags={Flags})";
    }
}
