namespace Shared.GameLogic.Components;

/// <summary>
/// Shared game constants. Ported from Go const block.
/// Used by both server validation and client prediction.
/// </summary>
public static class GameConstants
{
    /// <summary>Maximum movement distance per tick (before speed multiplier).</summary>
    public const float MaxMovePerTick = 5.0f;

    /// <summary>Maximum attack range in world units.</summary>
    public const float AttackRange = 3.0f;

    /// <summary>Attack cooldown duration in milliseconds.</summary>
    public const int AttackCooldownMs = 500;

    /// <summary>Default Area of Interest radius for snapshot filtering.</summary>
    public const float DefaultAoiRadius = 50.0f;

    /// <summary>Default simulation tick rate (Hz).</summary>
    public const int DefaultTickRate = 15;

    /// <summary>Minimum damage dealt per attack (floor).</summary>
    public const int MinDamage = 1;
}
