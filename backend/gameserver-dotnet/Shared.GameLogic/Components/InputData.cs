using System.Text.Json.Serialization;

namespace Shared.GameLogic.Components;

/// <summary>
/// Player input for one simulation tick. Ported from Go InputMessage.
/// JSON field names match the Go wire protocol exactly.
///
/// <para>
/// <b>MoveX/MoveY are a direction, not a displacement.</b> The server integrates
/// <c>direction * speed * dt</c> once per tick; a vector with magnitude &gt; 1 is
/// normalized (so diagonals are not faster than cardinals) and a grossly invalid
/// vector is dropped. Sending more input packets does not move the player further.
/// </para>
/// </summary>
public readonly struct InputData
{
    [JsonPropertyName("tick")]
    public readonly ulong Tick;

    [JsonPropertyName("move_x")]
    public readonly float MoveX;

    [JsonPropertyName("move_y")]
    public readonly float MoveY;

    [JsonPropertyName("attack_target_id")]
    public readonly string? AttackTargetId;

    [JsonConstructor]
    public InputData(ulong tick, float moveX, float moveY, string? attackTargetId)
    {
        Tick = tick;
        MoveX = moveX;
        MoveY = moveY;
        AttackTargetId = attackTargetId;
    }
}
