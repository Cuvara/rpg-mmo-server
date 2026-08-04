using System.Text.Json.Serialization;

namespace Shared.GameLogic.Components;

/// <summary>
/// A single entity's visible state in a snapshot.
/// Ported from Go EntitySnapshot. JSON tags match the wire protocol.
/// </summary>
public readonly struct EntitySnapshotData
{
    [JsonPropertyName("id")]
    public readonly string Id;

    [JsonPropertyName("type")]
    public readonly string Type;

    [JsonPropertyName("x")]
    public readonly float X;

    [JsonPropertyName("y")]
    public readonly float Y;

    [JsonPropertyName("hp")]
    public readonly int Hp;

    [JsonPropertyName("max_hp")]
    public readonly int MaxHp;

    [JsonConstructor]
    public EntitySnapshotData(string id, string type, float x, float y, int hp, int maxHp)
    {
        Id = id;
        Type = type;
        X = x;
        Y = y;
        Hp = hp;
        MaxHp = maxHp;
    }
}

/// <summary>
/// World state snapshot sent to the client each tick.
/// Ported from Go SnapshotMessage. JSON tags match the wire protocol.
/// </summary>
public readonly struct SnapshotData
{
    [JsonPropertyName("tick")]
    public readonly ulong Tick;

    [JsonPropertyName("entities")]
    public readonly EntitySnapshotData[] Entities;

    [JsonConstructor]
    public SnapshotData(ulong tick, EntitySnapshotData[] entities)
    {
        Tick = tick;
        Entities = entities;
    }
}
