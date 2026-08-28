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
                return "target out of range";

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
