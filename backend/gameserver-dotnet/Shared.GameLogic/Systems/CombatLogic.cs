using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// Server-authoritative combat logic. Ported from Go combat package.
    /// Shared between server (validation/resolution) and client (prediction).
    /// </summary>
    public static class CombatLogic
    {
        /// <summary>
        /// Rejection reason for an out-of-range attack, as one interned constant.
        /// </summary>
        /// <remarks>
        /// This used to be an interpolated string carrying the measured distance —
        /// which cost a <c>MathF.Sqrt</c>, two <c>float.ToString</c>s and a string
        /// allocation per rejection, on the server's tick thread inside its world
        /// write lock. Out-of-range is not an error path: it is what an
        /// auto-attacking client does continuously while closing distance, so the
        /// allocation ran per attack input per player per tick. Consumers that want
        /// the distance compute it at the call site behind their own debug guard;
        /// golden vectors on both repos assert only this prefix, so the constant is
        /// the whole contract (rpg-mmo-server#249).
        /// </remarks>
        public const string OutOfRangeRejection = "target out of range";

        /// <summary>
        /// Calculate damage dealt by attacker to defender.
        /// Formula: attacker.Attack - defender.Defense, minimum <see cref="GameConstants.MinDamage"/>.
        /// </summary>
        public static int CalculateDamage(in EntityState attacker, in EntityState defender)
        {
            int dmg = attacker.Attack - defender.Defense;
            return dmg < GameConstants.MinDamage ? GameConstants.MinDamage : dmg;
        }

        /// <summary>
        /// Check if an entity should die (HP &lt;= 0 and not already dead).
        /// Returns true if the entity died THIS call. Mutates <paramref name="entity"/>.Dead and HP.
        /// </summary>
        public static bool HandleDeath(ref EntityState entity)
        {
            if (entity.Dead || entity.Hp > 0)
                return false;

            entity.Dead = true;
            entity.Hp = 0;
            return true;
        }

        /// <summary>
        /// Validate an attack: check target alive, range, and cooldown.
        /// Returns null if valid, or an error string describing why the attack is invalid.
        /// </summary>
        /// <param name="currentTick">
        /// Current SIMULATION tick (not wall-clock). Cooldowns are tick-based so the same
        /// input sequence always resolves the same way on server and predicting client.
        /// </param>
        public static string? ValidateAttack(in EntityState attacker, in EntityState target, ulong currentTick)
        {
            if (target.Dead)
                return "target is already dead";

            if (!InRange(attacker.Position, target.Position, GameConstants.AttackRange))
                return OutOfRangeRejection;

            if (currentTick < attacker.CooldownUntilTick)
                return "attack on cooldown";

            return null;
        }

        /// <summary>
        /// Check if two positions are within the specified range. Uses squared distance (no sqrt).
        /// </summary>
        public static bool InRange(in Vec2 a, in Vec2 b, float range)
        {
            return Vec2.DistanceSq(a, b) <= range * range;
        }
    }
}
