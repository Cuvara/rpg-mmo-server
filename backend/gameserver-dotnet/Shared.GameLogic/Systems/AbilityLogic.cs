using Shared.GameLogic.Components;
using Shared.GameLogic.Content;

namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// Server-authoritative ability resolution. Shared with the client so both sides agree
    /// on what an ability does, even though the client does not predict one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is shared if abilities are not predicted.</b> Prediction is not the only
    /// reason to share logic. A client that shows a cooldown sweep, greys out an
    /// out-of-range target, or refuses to send an input it knows the server will reject is
    /// applying these rules — and if it applies its own copy of them, the two drift and the
    /// UI lies about what the server will do. Sharing the predicate is cheap; duplicating
    /// it is what costs.
    /// </para>
    /// <para>
    /// <b>What the client must not do with it.</b> A local answer from
    /// <see cref="ValidateCast"/> is a hint for presentation, never a substitute for the
    /// server's. The client sees stale positions by one interpolation delay and does not
    /// see cooldowns applied by effects it has no component for, so a cast it believes
    /// legal can still be refused. The correct client behaviour is to send the input and
    /// let the snapshot say what happened.
    /// </para>
    /// <para>
    /// Deterministic under ADR-10: integer arithmetic and squared-distance comparisons
    /// only. No <c>Sqrt</c>, no <c>Atan2</c>, no wall-clock.
    /// </para>
    /// </remarks>
    public static class AbilityLogic
    {
        /// <summary>The caster is dead.</summary>
        public const string CasterDeadRejection = "caster is dead";

        /// <summary>No ability with that id exists in the content set.</summary>
        public const string UnknownAbilityRejection = "unknown ability";

        /// <summary>The global ability cooldown had not expired.</summary>
        public const string CooldownRejection = "ability on cooldown";

        /// <summary>An entity-targeted ability arrived without a resolvable target.</summary>
        public const string MissingTargetRejection = "ability needs a target";

        /// <summary>The target was outside the ability's range.</summary>
        public const string OutOfRangeRejection = "ability target out of range";

        /// <summary>An entity-targeted damage ability aimed at something already dead.</summary>
        public const string TargetDeadRejection = "ability target is already dead";

        /// <summary>The caster is stunned or silenced (<see cref="StatusSet.CanCast"/> is false).</summary>
        public const string CannotCastRejection = "caster cannot cast";

        /// <summary>
        /// A ground or projectile ability arrived with an aim point on the caster, which gives
        /// a projectile no direction to travel in.
        /// </summary>
        public const string MissingAimRejection = "ability needs an aim point";

        /// <summary>
        /// Decides whether a cast may resolve. Returns null when it may, or one of the
        /// interned rejection constants above.
        /// </summary>
        /// <param name="caster">The casting entity, composed for THIS tick.</param>
        /// <param name="ability">The definition the input's ability id resolved to.</param>
        /// <param name="target">
        /// The target entity for <see cref="AbilityTargeting.Entity"/>, ignored otherwise.
        /// </param>
        /// <param name="hasTarget">
        /// Whether <paramref name="target"/> holds a resolved entity. Passed separately
        /// because <see cref="EntityState"/> is a struct and <c>default</c> is
        /// indistinguishable from a real entity at the origin with no id.
        /// </param>
        /// <param name="aim">Aim point for <see cref="AbilityTargeting.Ground"/>, ignored otherwise.</param>
        /// <param name="currentTick">Current SIMULATION tick.</param>
        /// <remarks>
        /// Rejection reasons are interned constants rather than interpolated strings for
        /// the reason <see cref="CombatLogic.OutOfRangeRejection"/> documents: a client
        /// holding a cast button generates rejections continuously while closing distance,
        /// on the tick thread inside the world write lock, so an allocating reason string
        /// would allocate per input per player per tick. A caller that wants the measured
        /// distance computes it behind its own debug guard.
        /// </remarks>
        public static string? ValidateCast(
            in EntityState caster,
            AbilityDefinition? ability,
            in EntityState target,
            bool hasTarget,
            in Vec2 aim,
            ulong currentTick)
        {
            // Checked before the null test so a dead entity gets the reason a player can
            // act on, rather than a content error that is not their problem.
            if (caster.Dead)
                return CasterDeadRejection;

            if (ability == null)
                return UnknownAbilityRejection;

            if (currentTick < caster.AbilityCooldownUntilTick)
                return CooldownRejection;

            switch (ability.Delivery)
            {
                case AbilityDelivery.Self:
                    // Nothing further to check: the caster is alive, off cooldown, and is
                    // its own target. Range and radius are not read for Self.
                    return null;

                case AbilityDelivery.Entity:
                    if (!hasTarget)
                        return MissingTargetRejection;

                    // Damage aimed at a corpse is refused; healing one is refused for the
                    // same reason, and both are refused HERE rather than resolving to zero,
                    // so the cooldown is not consumed by a cast that could do nothing.
                    if (target.Dead)
                        return TargetDeadRejection;

                    if (!CombatLogic.InRange(caster.Position, target.Position, ability.Range))
                        return OutOfRangeRejection;

                    return null;

                case AbilityDelivery.Ground:
                    // The AIM POINT is what must be in range, not whatever the area happens
                    // to cover. A caster with a 5-unit range and a 10-unit radius reaches 15
                    // units of effect, and that is the intended reading: radius is the size
                    // of the effect, range is how far the caster can place it.
                    if (!CombatLogic.InRange(caster.Position, aim, ability.Range))
                        return OutOfRangeRejection;

                    return null;

                case AbilityDelivery.Projectile:
                    // No range check on the aim point: it only gives the direction (ADR-29
                    // decision 5 -- derived from the caster's authoritative position), and how
                    // far the projectile flies is its own ProjectileSpec.Range. An aim point ON
                    // the caster has no direction at all, so it is refused rather than
                    // normalised into NaN.
                    if (aim.X == caster.Position.X && aim.Y == caster.Position.Y)
                        return MissingAimRejection;

                    return null;

                default:
                    // An unknown targeting mode means the content set and this build
                    // disagree. Refusing is the only safe answer: guessing a mode would
                    // apply an effect the author did not describe.
                    return UnknownAbilityRejection;
            }
        }

        /// <summary>
        /// <see cref="ValidateCast(in EntityState, AbilityDefinition?, in EntityState, bool, in Vec2, ulong)"/>
        /// plus the caster's crowd control (ADR-30): a stunned or silenced caster is refused
        /// with <see cref="CannotCastRejection"/>.
        /// </summary>
        /// <param name="casterStatuses">The caster's statuses, or null when it has none tracked.</param>
        /// <remarks>
        /// Crowd control is checked after "dead" and before everything else, so a silenced
        /// player pressing an unknown or cooling-down ability is told the reason that will
        /// still be true once the cooldown expires.
        /// </remarks>
        public static string? ValidateCast(
            in EntityState caster,
            AbilityDefinition? ability,
            in EntityState target,
            bool hasTarget,
            in Vec2 aim,
            ulong currentTick,
            StatusSet? casterStatuses)
        {
            if (caster.Dead)
                return CasterDeadRejection;

            if (casterStatuses != null && !casterStatuses.CanCast)
                return CannotCastRejection;

            return ValidateCast(in caster, ability, in target, hasTarget, in aim, currentTick);
        }

        /// <summary>
        /// Damage an ability deals from <paramref name="caster"/> to <paramref name="target"/>,
        /// after mitigation. At least <see cref="GameConstants.MinDamage"/>.
        /// </summary>
        /// <remarks>
        /// The ability's power is added to the caster's attack BEFORE the target's defense
        /// is subtracted, so an ability scales with the caster's gear rather than replacing
        /// it, and armour reduces the total once rather than once per source.
        /// </remarks>
        public static int CalculateAbilityDamage(
            in EntityState caster, AbilityDefinition ability, in EntityState target)
        {
            int dmg = caster.Attack + ability.Power - target.Defense;
            return dmg < GameConstants.MinDamage ? GameConstants.MinDamage : dmg;
        }

        /// <summary>
        /// Health an ability restores to <paramref name="target"/>, clamped to the health
        /// actually missing so a heal never reports more than it did.
        /// </summary>
        /// <remarks>
        /// Clamped here rather than at the call site because the clamped value is what the
        /// heal EVENT must carry: a full-health target healed for 500 shows "500" to every
        /// client and "0" in the HP bar, and a player reads that as the heal being eaten by
        /// something. The number shown and the number applied have to be the same number,
        /// which means one place computes both.
        /// </remarks>
        public static int CalculateHeal(AbilityDefinition ability, in EntityState target)
        {
            int missing = target.MaxHp - target.Hp;
            if (missing <= 0) return 0;
            return ability.Power < missing ? ability.Power : missing;
        }

        /// <summary>
        /// Damage one <see cref="EffectKind.Damage"/> entry deals, after mitigation: the
        /// effect-list form of <see cref="CalculateAbilityDamage"/>, with the same rule
        /// (power added to attack before defense is subtracted, floored at
        /// <see cref="GameConstants.MinDamage"/>).
        /// </summary>
        /// <remarks>
        /// <paramref name="caster"/> and <paramref name="target"/> must carry EFFECTIVE stats
        /// (after <see cref="StatusSet.EffectiveAttack"/> / <see cref="StatusSet.EffectiveDefense"/>)
        /// when statuses are in play; this function does not look them up.
        /// </remarks>
        public static int CalculateEffectDamage(in EntityState caster, in EffectSpec effect, in EntityState target)
        {
            int dmg = caster.Attack + effect.Power - target.Defense;
            return dmg < GameConstants.MinDamage ? GameConstants.MinDamage : dmg;
        }

        /// <summary>
        /// Health one <see cref="EffectKind.Heal"/> entry restores, clamped to the health
        /// actually missing — the effect-list form of <see cref="CalculateHeal"/>.
        /// </summary>
        public static int CalculateEffectHeal(in EffectSpec effect, in EntityState target) =>
            ClampHeal(effect.Power, in target);

        /// <summary>
        /// <paramref name="amount"/> clamped to the health <paramref name="target"/> is missing,
        /// and to 0 from below. Used for periodic heals, whose amount comes from a status rather
        /// than an ability.
        /// </summary>
        public static int ClampHeal(int amount, in EntityState target)
        {
            int missing = target.MaxHp - target.Hp;
            if (missing <= 0 || amount <= 0) return 0;
            return amount < missing ? amount : missing;
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> falls inside a ground ability's area.
        /// </summary>
        public static bool IsInArea(in Vec2 aim, in EntityState candidate, AbilityDefinition ability) =>
            CombatLogic.InRange(aim, candidate.Position, ability.Radius);

        /// <summary>
        /// The tick an ability's cooldown expires at, given the tick it resolved on.
        /// </summary>
        /// <remarks>
        /// A zero or negative <see cref="AbilityDefinition.CooldownTicks"/> yields
        /// <paramref name="currentTick"/>, which <see cref="ValidateCast"/> reads as "not on
        /// cooldown" — the comparison is strictly less-than. Content with no cooldown is a
        /// legitimate authoring choice and does not need a special case anywhere else.
        /// </remarks>
        public static ulong CooldownUntil(AbilityDefinition ability, ulong currentTick) =>
            ability.CooldownTicks <= 0 ? currentTick : currentTick + (ulong)ability.CooldownTicks;
    }
}
