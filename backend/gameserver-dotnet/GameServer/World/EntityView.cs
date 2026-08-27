using Shared.GameLogic.Components;

namespace GameServer.World;

/// <summary>
/// The trimmed per-match compose for the snapshot gather path: exactly the seven
/// fields the wire encoder consumes (<c>Id</c>/<c>Type</c>/<c>X</c>/<c>Y</c>/<c>Hp</c>/
/// <c>MaxHp</c>/<c>Speed</c>) plus the world-stable integer key the delta encoder
/// keys its maps on.
///
/// <para><b>Why this exists (issue #237).</b> The AOI scan used to compose a full
/// 11-field <see cref="EntityState"/> per match — two extra string-bearing component
/// fetches per chunk and five fields (<c>Attack</c>/<c>Defense</c>/
/// <c>CooldownUntilTick</c>/<c>LastInputTick</c>/<c>Dead</c>) the snapshot encoder
/// never reads. BENCHMARK.md Part V measured that composing matches, not distance
/// tests, is the scan's dominant cost, and the gather is 77-83% of a 200-viewer
/// tick, so the oversized compose was the dominant cost of the tick's dominant
/// phase. This struct is what the gather composes instead; the full
/// <see cref="EntityState"/> compose remains for the cold paths (combat, persistence,
/// tests) that genuinely need every field.</para>
///
/// <para><b>Key.</b> See <see cref="Components.EntityIdRef.Stable"/>: unique per id
/// string for the life of the world, never reused, so it is interchangeable with the
/// id string as a map key. It never reaches the wire — the wire's interning handles
/// are per-connection and unchanged.</para>
/// </summary>
public readonly struct EntityView
{
    /// <summary>World-stable integer key for <see cref="Id"/>. Server-side only.</summary>
    public readonly int Key;

    /// <summary>Entity identifier, exactly <see cref="EntityState.Id"/>.</summary>
    public readonly string Id;

    /// <summary>Entity type string, exactly <see cref="EntityState.Type"/>.</summary>
    public readonly string Type;

    /// <summary>World-space position.</summary>
    public readonly Vec2 Position;

    /// <summary>Current hit points.</summary>
    public readonly int Hp;

    /// <summary>Maximum hit points.</summary>
    public readonly int MaxHp;

    /// <summary>Movement speed in world units per second.</summary>
    public readonly float Speed;

    public EntityView(int key, string id, string type, Vec2 position, int hp, int maxHp, float speed)
    {
        Key = key;
        Id = id;
        Type = type;
        Position = position;
        Hp = hp;
        MaxHp = maxHp;
        Speed = speed;
    }
}
