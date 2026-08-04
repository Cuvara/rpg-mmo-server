using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems;

/// <summary>
/// Combined anti-cheat validation for player input.
/// Delegates to <see cref="MovementLogic"/> and <see cref="CombatLogic"/>.
/// </summary>
public static class ValidationLogic
{
    /// <summary>
    /// Full input validation. Returns null if valid, or an error string if invalid.
    /// </summary>
    /// <param name="entity">The entity performing the action.</param>
    /// <param name="input">The input to validate.</param>
    /// <param name="getEntity">Lookup function to resolve an entity by ID (for attack targets).</param>
    /// <param name="nowTicks">Current time in DateTime.Ticks for cooldown checks.</param>
    public static string? ValidateInput(
        in EntityState entity,
        in InputData input,
        Func<string, EntityState?> getEntity,
        long nowTicks)
    {
        if (entity.Dead)
            return "entity is dead";

        // Validate movement if any movement input is present.
        if (input.MoveX != 0f || input.MoveY != 0f)
        {
            string? moveError = MovementLogic.ValidateMove(entity, input.MoveX, input.MoveY);
            if (moveError != null)
                return moveError;
        }

        // Validate attack if a target is specified.
        if (!string.IsNullOrEmpty(input.AttackTargetId))
        {
            EntityState? target = getEntity(input.AttackTargetId);
            if (target == null)
                return $"attack target '{input.AttackTargetId}' not found";

            string? attackError = CombatLogic.ValidateAttack(entity, target.Value, nowTicks);
            if (attackError != null)
                return attackError;
        }

        return null;
    }
}
