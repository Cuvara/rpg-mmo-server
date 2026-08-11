using System;
using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// Combined anti-cheat validation for player input.
    /// Delegates to <see cref="MovementSystem"/> and <see cref="CombatLogic"/>.
    /// </summary>
    public static class ValidationLogic
    {
        /// <summary>
        /// Full input validation. Returns null if valid, or an error string if invalid.
        /// </summary>
        /// <param name="entity">The entity performing the action.</param>
        /// <param name="input">The input to validate.</param>
        /// <param name="getEntity">Lookup function to resolve an entity by ID (for attack targets).</param>
        /// <param name="currentTick">Current simulation tick, used for cooldown checks.</param>
        public static string? ValidateInput(
            in EntityState entity,
            in InputData input,
            Func<string, EntityState?> getEntity,
            ulong currentTick)
        {
            if (entity.Dead)
                return "entity is dead";

            // Validate the movement direction. This is timestep-independent: the actual
            // displacement is produced by MovementSystem.Integrate, which cannot exceed
            // speed * dt by construction, so only the input vector needs auditing here.
            if (MovementSystem.ResolveDirection(input.MoveX, input.MoveY, out _) == MoveResult.Rejected)
                return $"invalid move direction ({input.MoveX}, {input.MoveY})";

            // Validate attack if a target is specified.
            if (!string.IsNullOrEmpty(input.AttackTargetId))
            {
                EntityState? target = getEntity(input.AttackTargetId);
                if (target == null)
                    return $"attack target '{input.AttackTargetId}' not found";

                string? attackError = CombatLogic.ValidateAttack(entity, target.Value, currentTick);
                if (attackError != null)
                    return attackError;
            }

            return null;
        }
    }
}
