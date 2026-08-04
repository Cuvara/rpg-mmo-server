using System.Text.Json.Serialization;

namespace Shared.GameLogic.Components;

/// <summary>
/// Player input for one simulation tick. Ported from Go InputMessage.
/// JSON field names match the Go wire protocol exactly.
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
