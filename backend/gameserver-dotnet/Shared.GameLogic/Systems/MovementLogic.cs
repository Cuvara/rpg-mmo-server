using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems;

/// <summary>
/// Server-authoritative movement logic. Ported from Go movement + validator.
/// Shared between server (validation) and client (prediction).
/// </summary>
public static class MovementLogic
{
    /// <summary>
    /// Apply movement delta to a position. Returns the new position.
    /// </summary>
    public static Vec2 ApplyMove(in Vec2 position, float moveX, float moveY)
    {
        return new Vec2(position.X + moveX, position.Y + moveY);
    }

    /// <summary>
    /// Validate movement against speed limits (anti-cheat).
    /// Returns null if valid, or an error string if the move exceeds the speed limit.
    /// </summary>
    public static string? ValidateMove(in EntityState entity, float moveX, float moveY)
    {
        float distSq = moveX * moveX + moveY * moveY;

        float speed = entity.Speed > 0f ? entity.Speed : 1f;
        float limit = GameConstants.MaxMovePerTick * speed;
        float limitSq = limit * limit;

        if (distSq > limitSq)
        {
            float dist = MathF.Sqrt(distSq);
            return $"move too fast: distance {dist:F2} exceeds limit {limit:F2} (speed hack)";
        }

        return null;
    }
}
