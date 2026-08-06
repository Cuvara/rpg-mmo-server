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
/// <para>
/// A snapshot is either a KEYFRAME (<see cref="Full"/> = true: <see cref="Entities"/> is the
/// complete AOI set and anything absent must be dropped) or a DELTA (<see cref="Entities"/>
/// holds only entities whose visible state changed since the previous snapshot, and
/// <see cref="Removed"/> lists entities that left the AOI or the world).
/// </para>
/// </summary>
public readonly struct SnapshotData
{
    [JsonPropertyName("tick")]
    public readonly ulong Tick;

    /// <summary>
    /// Highest client input tick the server has accepted for this player.
    /// The client rewinds to this tick and replays newer inputs to reconcile.
    /// 0 means no input has been accepted yet.
    /// </summary>
    [JsonPropertyName("ack_tick")]
    public readonly ulong AckTick;

    /// <summary>True when this snapshot is a keyframe carrying complete AOI state.</summary>
    [JsonPropertyName("full")]
    public readonly bool Full;

    [JsonPropertyName("entities")]
    public readonly EntitySnapshotData[] Entities;

    /// <summary>Entity IDs that left the AOI/world. Empty on keyframes.</summary>
    [JsonPropertyName("removed")]
    public readonly string[]? Removed;

    [JsonConstructor]
    public SnapshotData(ulong tick, ulong ackTick, bool full, EntitySnapshotData[] entities, string[]? removed)
    {
        Tick = tick;
        AckTick = ackTick;
        Full = full;
        Entities = entities;
        Removed = removed;
    }

    /// <summary>Convenience constructor for a keyframe with no ack and no removals.</summary>
    public SnapshotData(ulong tick, EntitySnapshotData[] entities)
        : this(tick, 0, true, entities, null)
    {
    }
}
