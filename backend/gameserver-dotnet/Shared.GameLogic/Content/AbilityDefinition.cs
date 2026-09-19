using System;

namespace Shared.GameLogic.Content
{
    /// <summary>How an ability chooses what it affects.</summary>
    public enum AbilityTargeting
    {
        /// <summary>Affects the caster. Needs no target and no aim point.</summary>
        Self = 0,

        /// <summary>Affects one named entity, which must be in range.</summary>
        Entity = 1,

        /// <summary>
        /// Affects everything within <see cref="AbilityDefinition.Radius"/> of an aim
        /// point chosen by the caster.
        /// </summary>
        Ground = 2,
    }

    /// <summary>What an ability does when it resolves.</summary>
    /// <remarks>
    /// Deliberately a small closed set rather than a scripting hook. An ability whose
    /// effect is data is one both the server and a predicting client can reason about; an
    /// ability whose effect is a script is one only the server can run, and the client
    /// would be back to guessing. Widening this enum is a protocol-level decision, not a
    /// content one.
    /// </remarks>
    public enum AbilityEffect
    {
        /// <summary>Deals damage, scaled from the caster's attack by <see cref="AbilityDefinition.Power"/>.</summary>
        Damage = 0,

        /// <summary>Restores health.</summary>
        Heal = 1,
    }

    /// <summary>
    /// One ability, exactly as content authoring defines it. Immutable once built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shares <see cref="ItemDefinition"/>'s constraints and for the same reasons: the
    /// <b>schema</b> is shared, the <b>parsing</b> is not. The server runs NativeAOT and
    /// cannot use reflection; Unity compiles these files as source and has no
    /// <c>System.Text.Json</c>. Constructor-assigned rather than <c>init</c>-set, because
    /// <c>init</c> needs <c>IsExternalInit</c>, which netstandard2.1 does not carry.
    /// </para>
    /// <para>
    /// <b><see cref="Id"/> is numeric, unlike an item's.</b> Ability ids travel on the wire
    /// on every cast event and every ability input, where an item id travels only in
    /// inventory payloads that are not per-tick. A string id would cost ~10 bytes on the
    /// hottest gameplay path to say something a varint says in one. The content set carries
    /// a separate <see cref="Name"/> for authoring, and the validator enforces that ids are
    /// unique and start at 1 — zero is reserved for "no ability" on the wire.
    /// </para>
    /// </remarks>
    public sealed class AbilityDefinition
    {
        public AbilityDefinition(
            uint id,
            string name,
            AbilityTargeting targeting,
            AbilityEffect effect,
            float range,
            float radius,
            int power,
            int cooldownTicks)
        {
            Id = id;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Targeting = targeting;
            Effect = effect;
            Range = range;
            Radius = radius;
            Power = power;
            CooldownTicks = cooldownTicks;
        }

        /// <summary>
        /// Stable numeric identifier, 1 or greater. Zero is reserved for "no ability".
        /// </summary>
        /// <remarks>
        /// Once an id has been persisted against a player — on a hotbar, in a talent
        /// choice — it can never be reused for a different ability, for the same reason an
        /// item id cannot: the saved row is only a reference.
        /// </remarks>
        public uint Id { get; }

        /// <summary>Display name. Free to change — nothing references it.</summary>
        public string Name { get; }

        public AbilityTargeting Targeting { get; }

        public AbilityEffect Effect { get; }

        /// <summary>
        /// Maximum distance in world units from the caster to the target entity or aim
        /// point. Ignored for <see cref="AbilityTargeting.Self"/>.
        /// </summary>
        public float Range { get; }

        /// <summary>
        /// Radius in world units around the aim point. Only meaningful for
        /// <see cref="AbilityTargeting.Ground"/>.
        /// </summary>
        public float Radius { get; }

        /// <summary>
        /// Effect magnitude before the caster's stats are applied. For
        /// <see cref="AbilityEffect.Damage"/> it is added to the caster's attack before
        /// the target's defense is subtracted; for <see cref="AbilityEffect.Heal"/> it is
        /// the health restored.
        /// </summary>
        public int Power { get; }

        /// <summary>
        /// Cooldown in SIMULATION TICKS, never milliseconds.
        /// </summary>
        /// <remarks>
        /// The tick loop is the only clock the simulation has. A cooldown in wall-clock
        /// time would resolve differently on a server under load than on a client
        /// replaying the same input sequence, which is the whole class of divergence
        /// tick-based cooldowns exist to prevent. Authoring converts seconds to ticks
        /// against the server's advertised tick rate, once, at content build time.
        /// </remarks>
        public int CooldownTicks { get; }

        /// <summary>True when this ability needs a named target entity.</summary>
        public bool NeedsTarget => Targeting == AbilityTargeting.Entity;

        /// <summary>True when this ability needs an aim point.</summary>
        public bool NeedsAim => Targeting == AbilityTargeting.Ground;

        public override string ToString() => $"{Id}:{Name}";
    }
}
