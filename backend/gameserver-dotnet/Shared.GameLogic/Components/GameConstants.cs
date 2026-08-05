namespace Shared.GameLogic.Components;

/// <summary>
/// Shared game constants. Used by both server validation and client prediction —
/// the Unity client must compile against the exact same values or prediction
/// diverges from the authoritative simulation.
/// </summary>
public static class GameConstants
{
    // ── Movement ──

    /// <summary>
    /// Maximum accepted magnitude of a raw client input vector before it is treated
    /// as garbage and dropped. Anything in (1, this] is normalized to unit length —
    /// a raw diagonal key input of (1,1) has magnitude ~1.414 and is clamped, not dropped.
    /// </summary>
    public const float MaxInputMagnitude = 1.5f;

    /// <summary>
    /// Squared magnitude below which an input vector counts as "no movement".
    /// </summary>
    public const float InputDeadzoneSq = 1e-8f;

    /// <summary>
    /// Upper bound for a single integration step in seconds. Guards against a
    /// pathological dt (paused process, debugger break) teleporting an entity.
    /// </summary>
    public const float MaxDeltaTime = 0.5f;

    /// <summary>
    /// Tolerance factor applied when auditing an observed displacement against the
    /// theoretical maximum (<c>speed * dt</c>). Absorbs float rounding and one frame
    /// of jitter without opening a speed-hack window.
    /// </summary>
    public const float DisplacementTolerance = 1.05f;

    /// <summary>Default map width in world units.</summary>
    public const float DefaultMapWidth = 1000f;

    /// <summary>Default map height in world units.</summary>
    public const float DefaultMapHeight = 1000f;

    // ── Combat ──

    /// <summary>Maximum attack range in world units.</summary>
    public const float AttackRange = 3.0f;

    /// <summary>Attack cooldown duration in milliseconds.</summary>
    public const int AttackCooldownMs = 500;

    /// <summary>Minimum damage dealt per attack (floor).</summary>
    public const int MinDamage = 1;

    // ── Simulation ──

    /// <summary>Default Area of Interest radius for snapshot filtering.</summary>
    public const float DefaultAoiRadius = 50.0f;

    /// <summary>Default simulation tick rate (Hz).</summary>
    public const int DefaultTickRate = 15;
}
