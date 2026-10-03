using System;
using System.Collections.Generic;

namespace Shared.GameLogic.Content
{
    /// <summary>Whether a periodic status deals damage or heals on each interval.</summary>
    public enum PeriodicKind
    {
        /// <summary>Not periodic.</summary>
        None = 0,

        /// <summary>Damage over time.</summary>
        Damage = 1,

        /// <summary>Healing over time.</summary>
        Heal = 2,
    }

    /// <summary>
    /// The periodic part of a status: every <see cref="IntervalTicks"/> it deals or restores
    /// <see cref="Amount"/> per stack. <see cref="None"/> for a status that does not tick.
    /// </summary>
    public readonly struct PeriodicSpec
    {
        /// <summary>Builds a periodic spec.</summary>
        /// <param name="kind">Damage or heal.</param>
        /// <param name="intervalTicks">Simulation ticks between applications, 1 or more.</param>
        /// <param name="amount">Damage or healing per application, per stack, before mitigation.</param>
        public PeriodicSpec(PeriodicKind kind, int intervalTicks, int amount)
        {
            Kind = kind;
            IntervalTicks = intervalTicks;
            Amount = amount;
        }

        /// <summary>Damage, heal, or <see cref="PeriodicKind.None"/>.</summary>
        public PeriodicKind Kind { get; }

        /// <summary>
        /// Simulation ticks between applications. The first application happens this many
        /// ticks after the status is applied, never on the application tick itself.
        /// </summary>
        public int IntervalTicks { get; }

        /// <summary>Damage or healing per application, per stack, before mitigation.</summary>
        public int Amount { get; }

        /// <summary>True when the status ticks.</summary>
        public bool IsPeriodic => Kind != PeriodicKind.None;

        /// <summary>No periodic effect.</summary>
        public static PeriodicSpec None => default;

        /// <summary>A damage-over-time spec.</summary>
        public static PeriodicSpec DamageOverTime(int intervalTicks, int amount) =>
            new PeriodicSpec(PeriodicKind.Damage, intervalTicks, amount);

        /// <summary>A heal-over-time spec.</summary>
        public static PeriodicSpec HealOverTime(int intervalTicks, int amount) =>
            new PeriodicSpec(PeriodicKind.Heal, intervalTicks, amount);
    }

    /// <summary>What a <see cref="StatModifier"/> changes.</summary>
    /// <remarks>
    /// The first four are the first-class combat stats on <see cref="Components.EntityState"/>;
    /// <see cref="ContentStat"/> names a content stat (<see cref="StatDefinition"/>) by
    /// <see cref="StatModifier.StatId"/>.
    /// </remarks>
    public enum StatModifierTarget
    {
        /// <summary><see cref="Components.EntityState.Attack"/>.</summary>
        Attack = 0,

        /// <summary><see cref="Components.EntityState.Defense"/>.</summary>
        Defense = 1,

        /// <summary>
        /// <see cref="Components.EntityState.Speed"/>. Multiplier-only: speed is a float in
        /// world units per second and an integer additive has no unit to mean, so the
        /// validator refuses a non-zero <see cref="StatModifier.Add"/> here.
        /// </summary>
        Speed = 2,

        /// <summary><see cref="Components.EntityState.MaxHp"/>.</summary>
        MaxHp = 3,

        /// <summary>The content stat named by <see cref="StatModifier.StatId"/>.</summary>
        ContentStat = 4,
    }

    /// <summary>
    /// One stat change a status applies while active, per stack. Immutable value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fixed-point, deliberately.</b> The multiplier is in PERMILLE and is a DELTA:
    /// <c>+200</c> means x1.2, <c>-500</c> means x0.5, <c>0</c> means unchanged. A float
    /// multiplier would make the effective stat depend on the order modifiers are folded in
    /// and on the runtime's float contraction, and both sides compute these values. Integer
    /// permille sums are order-independent and exact.
    /// </para>
    /// <para>
    /// The effective value of a stat is
    /// <c>(base + sum(Add * stacks)) * max(0, 1000 + sum(MultiplierPermille * stacks)) / 1000</c>,
    /// computed in 64-bit integers by <see cref="Systems.StatusSet"/>. Additives apply before
    /// multipliers so a percentage buff scales flat bonuses too.
    /// </para>
    /// </remarks>
    public readonly struct StatModifier
    {
        /// <summary>Builds a modifier. Prefer the named factories.</summary>
        /// <param name="target">The stat changed.</param>
        /// <param name="statId">Content stat id when <paramref name="target"/> is <see cref="StatModifierTarget.ContentStat"/>; 0 otherwise.</param>
        /// <param name="add">Flat amount added per stack.</param>
        /// <param name="multiplierPermille">Multiplier delta in permille per stack (+200 = +20%).</param>
        public StatModifier(StatModifierTarget target, uint statId, int add, int multiplierPermille)
        {
            Target = target;
            StatId = statId;
            Add = add;
            MultiplierPermille = multiplierPermille;
        }

        /// <summary>The stat changed.</summary>
        public StatModifierTarget Target { get; }

        /// <summary>Content stat id for <see cref="StatModifierTarget.ContentStat"/>; 0 otherwise.</summary>
        public uint StatId { get; }

        /// <summary>Flat amount added per stack.</summary>
        public int Add { get; }

        /// <summary>Multiplier delta in permille per stack: +200 is +20%, -500 is -50%.</summary>
        public int MultiplierPermille { get; }

        /// <summary>A modifier on one of the first-class combat stats.</summary>
        public static StatModifier For(StatModifierTarget target, int add, int multiplierPermille) =>
            new StatModifier(target, 0u, add, multiplierPermille);

        /// <summary>A modifier on content stat <paramref name="statId"/>.</summary>
        public static StatModifier ForStat(uint statId, int add, int multiplierPermille) =>
            new StatModifier(StatModifierTarget.ContentStat, statId, add, multiplierPermille);

        /// <summary>True when this modifier changes the stat selected by the arguments.</summary>
        public bool Affects(StatModifierTarget target, uint statId) =>
            Target == target && (target != StatModifierTarget.ContentStat || StatId == statId);
    }

    /// <summary>
    /// Crowd-control effects a status imposes. Flags: one status may impose several.
    /// </summary>
    [Flags]
    public enum CrowdControl
    {
        /// <summary>No crowd control.</summary>
        None = 0,

        /// <summary>Cannot move, cast or act.</summary>
        Stun = 1 << 0,

        /// <summary>Cannot move; can still cast and act.</summary>
        Root = 1 << 1,

        /// <summary>Cannot cast abilities; can still move and basic-attack.</summary>
        Silence = 1 << 2,

        /// <summary>Moves slower by <see cref="StatusDefinition.SlowPermille"/>.</summary>
        Slow = 1 << 3,

        /// <summary>Every flag this build understands. Used by validation.</summary>
        All = Stun | Root | Silence | Slow,
    }

    /// <summary>
    /// One status effect (DoT/HoT, buff, debuff, crowd control, or a combination), exactly as
    /// content authoring defines it (ADR-30). Immutable once built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A status is data, not script, for the reason <see cref="AbilityEffect"/> gives: the
    /// client must be able to read what an active status does (a stun icon, a slowed
    /// predicted speed) from the definition alone. The wire tells a client only which status
    /// is active, with how many stacks and until when; everything else is looked up here.
    /// </para>
    /// <para>
    /// <b>Durations are simulation ticks</b>, never milliseconds, for the reason
    /// <see cref="AbilityDefinition.CooldownTicks"/> documents.
    /// </para>
    /// </remarks>
    public sealed class StatusDefinition
    {
        private readonly StatModifier[] _modifiers;

        /// <summary>Builds a status definition.</summary>
        /// <param name="id">Stable numeric id, 1 or greater.</param>
        /// <param name="key">Authoring key. Lowercase, digits and underscores; unique across statuses.</param>
        /// <param name="durationTicks">Lifetime in simulation ticks; 0 means "until removed".</param>
        /// <param name="maxStacks">Most stacks one entity can carry, 1 or more.</param>
        /// <param name="periodic">DoT/HoT part, or <see cref="PeriodicSpec.None"/>.</param>
        /// <param name="modifiers">Stat modifiers applied per stack. Copied. May be empty.</param>
        /// <param name="crowdControl">Crowd control imposed while active.</param>
        /// <param name="slowPermille">
        /// Speed reduction in permille when <paramref name="crowdControl"/> includes
        /// <see cref="CrowdControl.Slow"/> (300 = 30% slower); 0 otherwise.
        /// </param>
        public StatusDefinition(
            uint id,
            string key,
            int durationTicks,
            int maxStacks,
            PeriodicSpec periodic,
            IReadOnlyList<StatModifier> modifiers,
            CrowdControl crowdControl,
            int slowPermille)
        {
            if (modifiers == null) throw new ArgumentNullException(nameof(modifiers));

            Id = id;
            Key = key ?? throw new ArgumentNullException(nameof(key));
            DurationTicks = durationTicks;
            MaxStacks = maxStacks;
            Periodic = periodic;
            CrowdControl = crowdControl;
            SlowPermille = slowPermille;

            _modifiers = new StatModifier[modifiers.Count];
            for (int i = 0; i < _modifiers.Length; i++) _modifiers[i] = modifiers[i];
        }

        /// <summary>
        /// Stable numeric identifier, 1 or greater; the wire's <c>StatusEffect.effect_id</c>
        /// and <c>GameEvent.effect_id</c>. Zero is reserved for "no status".
        /// </summary>
        public uint Id { get; }

        /// <summary>Authoring key, unique across statuses.</summary>
        public string Key { get; }

        /// <summary>Lifetime in simulation ticks; 0 means "until removed".</summary>
        public int DurationTicks { get; }

        /// <summary>
        /// Most stacks one entity can carry. Re-applying at the cap refreshes the duration
        /// without adding a stack.
        /// </summary>
        public int MaxStacks { get; }

        /// <summary>DoT/HoT part, or <see cref="PeriodicSpec.None"/>.</summary>
        public PeriodicSpec Periodic { get; }

        /// <summary>Stat modifiers applied per stack, in authoring order.</summary>
        public IReadOnlyList<StatModifier> Modifiers => _modifiers;

        /// <summary>Number of entries in <see cref="Modifiers"/>.</summary>
        public int ModifierCount => _modifiers.Length;

        /// <summary>The modifier at <paramref name="index"/>, read without the interface.</summary>
        public StatModifier GetModifier(int index) => _modifiers[index];

        /// <summary>Crowd control imposed while active.</summary>
        public CrowdControl CrowdControl { get; }

        /// <summary>
        /// Speed reduction in permille while <see cref="CrowdControl"/> includes
        /// <see cref="Content.CrowdControl.Slow"/>. Not scaled by stacks; when several slows
        /// are active the strongest one applies.
        /// </summary>
        public int SlowPermille { get; }

        /// <summary>True when the status never expires on its own.</summary>
        public bool IsPermanent => DurationTicks == 0;

        /// <inheritdoc />
        public override string ToString() => $"{Id}:{Key}";
    }
}
