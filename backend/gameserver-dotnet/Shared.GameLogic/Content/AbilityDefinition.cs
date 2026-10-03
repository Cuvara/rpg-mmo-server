using System;
using System.Collections.Generic;

namespace Shared.GameLogic.Content
{
    /// <summary>How an ability chooses what it affects.</summary>
    /// <remarks>
    /// The protocol 2 form. <see cref="AbilityDelivery"/> supersedes it and adds
    /// projectiles; the two share numeric values for every mode this enum can express.
    /// </remarks>
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
    /// <para>
    /// The protocol 2 form. Protocol 3 abilities carry an ordered list of
    /// <see cref="EffectSpec"/> instead; this enum survives as a legacy view of it
    /// (<see cref="AbilityDefinition.Effect"/>).
    /// </para>
    /// </remarks>
    public enum AbilityEffect
    {
        /// <summary>Deals damage, scaled from the caster's attack by <see cref="AbilityDefinition.Power"/>.</summary>
        Damage = 0,

        /// <summary>Restores health.</summary>
        Heal = 1,
    }

    /// <summary>
    /// How an ability reaches what it affects (ADR-30). The successor of
    /// <see cref="AbilityTargeting"/>, which it extends with <see cref="Projectile"/>.
    /// </summary>
    /// <remarks>
    /// The first three values are numerically identical to <see cref="AbilityTargeting"/>,
    /// so a cast between the two is exact for every delivery the older enum can express.
    /// </remarks>
    public enum AbilityDelivery
    {
        /// <summary>Affects the caster. Needs no target and no aim point.</summary>
        Self = 0,

        /// <summary>Affects one named entity, which must be in range.</summary>
        Entity = 1,

        /// <summary>
        /// Affects everything within <see cref="AbilityDefinition.Radius"/> of an aim point
        /// chosen by the caster, which must itself be within <see cref="AbilityDefinition.Range"/>.
        /// </summary>
        Ground = 2,

        /// <summary>
        /// Spawns a projectile entity (ADR-29) that travels from the caster toward the aim
        /// point with the parameters in <see cref="AbilityDefinition.Projectile"/>. The
        /// effect list applies to whatever the projectile hits.
        /// </summary>
        Projectile = 3,
    }

    /// <summary>The kind of one entry in an ability's effect list (ADR-30).</summary>
    /// <remarks>
    /// A small closed set for the reason <see cref="AbilityEffect"/> gives: an effect that is
    /// data is one both sides can reason about. New <b>combinations</b> are content; a new
    /// <b>kind</b> is a library release.
    /// </remarks>
    public enum EffectKind
    {
        /// <summary>
        /// Deals damage: <see cref="EffectSpec.Power"/> is added to the caster's attack before
        /// the target's defense is subtracted, exactly as <see cref="AbilityEffect.Damage"/> did.
        /// </summary>
        Damage = 0,

        /// <summary>Restores <see cref="EffectSpec.Power"/> health, clamped to what is missing.</summary>
        Heal = 1,

        /// <summary>
        /// Applies one stack of the status named by <see cref="EffectSpec.StatusId"/>.
        /// <see cref="EffectSpec.Power"/> is not read.
        /// </summary>
        ApplyStatus = 2,
    }

    /// <summary>
    /// One entry of an ability's ordered effect list. Immutable value.
    /// </summary>
    /// <remarks>
    /// Effects resolve in list order against each affected entity, so "damage, then apply a
    /// burn" and "apply a vulnerability, then damage" are different abilities. The order is
    /// part of the content and both sides must honour it.
    /// </remarks>
    public readonly struct EffectSpec : IEquatable<EffectSpec>
    {
        /// <summary>Builds an effect. Prefer the named factories, which cannot mis-bind.</summary>
        public EffectSpec(EffectKind kind, int power, uint statusId)
        {
            Kind = kind;
            Power = power;
            StatusId = statusId;
        }

        /// <summary>What this effect does.</summary>
        public EffectKind Kind { get; }

        /// <summary>
        /// Magnitude for <see cref="EffectKind.Damage"/> and <see cref="EffectKind.Heal"/>.
        /// Never negative in valid content; not read for <see cref="EffectKind.ApplyStatus"/>.
        /// </summary>
        public int Power { get; }

        /// <summary>
        /// Content status id for <see cref="EffectKind.ApplyStatus"/>; 0 for every other kind.
        /// </summary>
        public uint StatusId { get; }

        /// <summary>A damage effect of <paramref name="power"/>.</summary>
        public static EffectSpec Damage(int power) => new EffectSpec(EffectKind.Damage, power, 0u);

        /// <summary>A heal effect of <paramref name="power"/>.</summary>
        public static EffectSpec Heal(int power) => new EffectSpec(EffectKind.Heal, power, 0u);

        /// <summary>An effect applying one stack of status <paramref name="statusId"/>.</summary>
        public static EffectSpec ApplyStatus(uint statusId) => new EffectSpec(EffectKind.ApplyStatus, 0, statusId);

        /// <inheritdoc />
        public bool Equals(EffectSpec other) =>
            Kind == other.Kind && Power == other.Power && StatusId == other.StatusId;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is EffectSpec other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => (((int)Kind * 397) ^ Power) * 397 ^ (int)StatusId;

        /// <inheritdoc />
        public override string ToString() =>
            Kind == EffectKind.ApplyStatus ? $"ApplyStatus({StatusId})" : $"{Kind}({Power})";
    }

    /// <summary>
    /// Flight parameters of a <see cref="AbilityDelivery.Projectile"/> ability (ADR-29).
    /// All zero (<see cref="None"/>) for every other delivery.
    /// </summary>
    public readonly struct ProjectileSpec
    {
        /// <summary>Builds projectile parameters.</summary>
        /// <param name="speed">Travel speed in world units per second.</param>
        /// <param name="radius">Collision radius of the swept sphere, in world units.</param>
        /// <param name="range">Maximum travel distance in world units before it despawns.</param>
        public ProjectileSpec(float speed, float radius, float range)
        {
            Speed = speed;
            Radius = radius;
            Range = range;
        }

        /// <summary>Travel speed in world units per second.</summary>
        public float Speed { get; }

        /// <summary>Collision radius of the swept sphere, in world units.</summary>
        public float Radius { get; }

        /// <summary>Maximum travel distance in world units before the projectile despawns.</summary>
        public float Range { get; }

        /// <summary>No projectile: the value every non-projectile ability carries.</summary>
        public static ProjectileSpec None => default;
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
    /// <para>
    /// <b>Two shapes, one type (ADR-30).</b> The protocol 2 shape is one targeting mode and
    /// one effect; the protocol 3 shape is a <see cref="Delivery"/> and an ordered
    /// <see cref="Effects"/> list. Both constructors exist because the Unity client compiles
    /// this library from a pinned tag, and a removed signature breaks every call site at
    /// once. The original constructor is mapped onto the new model — its targeting becomes
    /// the delivery and its single effect a one-entry list — so every consumer can read the
    /// new properties regardless of which constructor built the definition. The original
    /// properties (<see cref="Targeting"/>, <see cref="Effect"/>, <see cref="Power"/>) stay
    /// as a derived legacy view.
    /// </para>
    /// </remarks>
    public sealed class AbilityDefinition
    {
        private readonly EffectSpec[] _effects;

        // Set only by the legacy constructor when it is handed an AbilityEffect value the
        // enum does not define. Carried through so the validator still reports it against
        // this ability instead of the value silently becoming a damage ability.
        private readonly AbilityEffect? _undefinedLegacyEffect;

        /// <summary>
        /// Builds a single-effect ability in the original (protocol 2) shape.
        /// </summary>
        /// <remarks>
        /// Kept for source compatibility. Equivalent to the effect-list constructor with
        /// <c>delivery = (AbilityDelivery)targeting</c>, a one-entry effect list holding
        /// <paramref name="effect"/> at <paramref name="power"/>, and no projectile.
        /// </remarks>
        public AbilityDefinition(
            uint id,
            string name,
            AbilityTargeting targeting,
            AbilityEffect effect,
            float range,
            float radius,
            int power,
            int cooldownTicks)
            : this(
                id,
                name,
                (AbilityDelivery)targeting,
                new[] { new EffectSpec(effect == AbilityEffect.Heal ? EffectKind.Heal : EffectKind.Damage, power, 0u) },
                range,
                radius,
                cooldownTicks,
                ProjectileSpec.None)
        {
            if (!Enum.IsDefined(typeof(AbilityEffect), effect))
            {
                _undefinedLegacyEffect = effect;
            }
        }

        /// <summary>
        /// Builds an ability in the effect-list (protocol 3, ADR-30) shape.
        /// </summary>
        /// <param name="id">Stable numeric id, 1 or greater.</param>
        /// <param name="name">Display name.</param>
        /// <param name="delivery">How the ability reaches what it affects.</param>
        /// <param name="effects">
        /// Ordered effects applied to each affected entity. Copied, so later changes to the
        /// caller's collection never reach this definition.
        /// </param>
        /// <param name="range">
        /// Maximum caster-to-target (or caster-to-aim-point) distance for
        /// <see cref="AbilityDelivery.Entity"/> and <see cref="AbilityDelivery.Ground"/>.
        /// Not read for <see cref="AbilityDelivery.Projectile"/>, whose reach is
        /// <see cref="ProjectileSpec.Range"/>.
        /// </param>
        /// <param name="radius">Area radius for <see cref="AbilityDelivery.Ground"/>.</param>
        /// <param name="cooldownTicks">Cooldown in simulation ticks.</param>
        /// <param name="projectile">
        /// Flight parameters for <see cref="AbilityDelivery.Projectile"/>;
        /// <see cref="ProjectileSpec.None"/> otherwise.
        /// </param>
        public AbilityDefinition(
            uint id,
            string name,
            AbilityDelivery delivery,
            IReadOnlyList<EffectSpec> effects,
            float range,
            float radius,
            int cooldownTicks,
            ProjectileSpec projectile)
        {
            if (effects == null) throw new ArgumentNullException(nameof(effects));

            Id = id;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Delivery = delivery;
            Range = range;
            Radius = radius;
            CooldownTicks = cooldownTicks;
            Projectile = projectile;

            _effects = new EffectSpec[effects.Count];
            for (int i = 0; i < _effects.Length; i++) _effects[i] = effects[i];
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

        /// <summary>How the ability reaches what it affects.</summary>
        public AbilityDelivery Delivery { get; }

        /// <summary>
        /// Ordered effects applied to each affected entity. Backed by an array: on the tick
        /// path index it (<see cref="EffectCount"/> / <see cref="GetEffect"/>) rather than
        /// <c>foreach</c> over the interface, which boxes an enumerator.
        /// </summary>
        public IReadOnlyList<EffectSpec> Effects => _effects;

        /// <summary>Number of entries in <see cref="Effects"/>.</summary>
        public int EffectCount => _effects.Length;

        /// <summary>The effect at <paramref name="index"/>, read without the interface.</summary>
        public EffectSpec GetEffect(int index) => _effects[index];

        /// <summary>
        /// Flight parameters; <see cref="ProjectileSpec.None"/> unless <see cref="Delivery"/>
        /// is <see cref="AbilityDelivery.Projectile"/>.
        /// </summary>
        public ProjectileSpec Projectile { get; }

        /// <summary>
        /// Legacy view of <see cref="Delivery"/> for callers written before projectiles.
        /// </summary>
        /// <remarks>
        /// <see cref="AbilityDelivery.Projectile"/> reads as <see cref="AbilityTargeting.Ground"/>:
        /// both need an aim point and neither needs a target, which is what an older caller
        /// branching on this property uses it for. New code switches on <see cref="Delivery"/>.
        /// </remarks>
        public AbilityTargeting Targeting =>
            Delivery == AbilityDelivery.Projectile ? AbilityTargeting.Ground : (AbilityTargeting)Delivery;

        /// <summary>
        /// Legacy view: the kind of the first damage or heal entry in <see cref="Effects"/>,
        /// or <see cref="AbilityEffect.Damage"/> when there is none.
        /// </summary>
        public AbilityEffect Effect
        {
            get
            {
                if (_undefinedLegacyEffect.HasValue) return _undefinedLegacyEffect.Value;
                int i = FirstDirectEffect();
                return i >= 0 && _effects[i].Kind == EffectKind.Heal ? AbilityEffect.Heal : AbilityEffect.Damage;
            }
        }

        /// <summary>
        /// Maximum distance in world units from the caster to the target entity or aim
        /// point. Ignored for <see cref="AbilityDelivery.Self"/> and
        /// <see cref="AbilityDelivery.Projectile"/>.
        /// </summary>
        public float Range { get; }

        /// <summary>
        /// Radius in world units around the aim point. Only meaningful for
        /// <see cref="AbilityDelivery.Ground"/>.
        /// </summary>
        public float Radius { get; }

        /// <summary>
        /// Legacy view: the power of the first damage or heal entry in <see cref="Effects"/>,
        /// or 0 when there is none.
        /// </summary>
        /// <remarks>
        /// For <see cref="AbilityEffect.Damage"/> it is added to the caster's attack before the
        /// target's defense is subtracted; for <see cref="AbilityEffect.Heal"/> it is the
        /// health restored.
        /// </remarks>
        public int Power
        {
            get
            {
                int i = FirstDirectEffect();
                return i >= 0 ? _effects[i].Power : 0;
            }
        }

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
        public bool NeedsTarget => Delivery == AbilityDelivery.Entity;

        /// <summary>True when this ability needs an aim point (ground area or projectile).</summary>
        public bool NeedsAim => Delivery == AbilityDelivery.Ground || Delivery == AbilityDelivery.Projectile;

        private int FirstDirectEffect()
        {
            for (int i = 0; i < _effects.Length; i++)
            {
                EffectKind k = _effects[i].Kind;
                if (k == EffectKind.Damage || k == EffectKind.Heal) return i;
            }

            return -1;
        }

        /// <inheritdoc />
        public override string ToString() => $"{Id}:{Name}";
    }
}
