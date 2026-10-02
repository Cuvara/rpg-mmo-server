using Shared.GameLogic.Components;

namespace GameServer.World;

/// <summary>
/// The trimmed per-match compose for the snapshot gather path: exactly the ten
/// fields the wire encoder consumes (<c>Id</c>/<c>Type</c>/<c>X</c>/<c>Y</c>/<c>Hp</c>/
/// <c>MaxHp</c>/<c>Speed</c>/<c>FacingBrad</c>/<c>Action</c>/<c>ActionSeq</c>) plus the world-stable integer key the delta encoder
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

    /// <summary>
    /// Facing as biased 16-bit binary radians, in the wire's own form. 0 means
    /// "unknown". See <see cref="Components.Locomotion.FacingBrad"/>.
    /// </summary>
    public readonly uint FacingBrad;

    /// <summary>
    /// What the entity is doing. <see cref="EntityAction.Unspecified"/> means
    /// "unknown", never "idle".
    /// </summary>
    public readonly EntityAction Action;

    /// <summary>
    /// Retrigger counter for <see cref="Action"/>; 0 means "never set". Rides the same
    /// <c>Locomotion</c> span as <see cref="Action"/> and <see cref="FacingBrad"/>, which
    /// is why carrying it costs the gather nothing.
    /// </summary>
    public readonly uint ActionSeq;

    // ── Protocol 3 (ADR-28..30). Composed by EcsWorld.ComposeView; zero for a protocol 2
    // world, so an encoder that ignores them produces the protocol 2 bytes. ──

    /// <summary>
    /// Height (wire EntitySnapshot field 14). Feet for a character, sphere centre for a
    /// projectile, ground for an item. See <see cref="Components.Position.Z"/>.
    /// </summary>
    public readonly float Z;

    /// <summary>Ground-plane velocity X in units/s (wire field 15); see <see cref="Components.Locomotion.VelocityX"/>.</summary>
    public readonly float VelX;

    /// <summary>Ground-plane velocity Y in units/s (wire field 16).</summary>
    public readonly float VelY;

    /// <summary>Vertical velocity in units/s (wire field 17).</summary>
    public readonly float VelZ;

    /// <summary>
    /// Owner of a projectile (the caster), or null. The wire carries it as the owner's
    /// interned handle; <see cref="OwnerKey"/> is the stable key to intern by.
    /// </summary>
    public readonly string? OwnerId;

    /// <summary>World-stable key of <see cref="OwnerId"/>; 0 when there is no owner.</summary>
    public readonly int OwnerKey;

    /// <summary>
    /// <c>InputMessage.spawn_seq</c> of the input that fired this projectile; 0 for anything
    /// else. ADR-29 decision 2: sent to the OWNER's connection only.
    /// </summary>
    public readonly uint SpawnSeq;

    /// <summary>
    /// Changes whenever this entity's replicated stat block may have changed (a base stat, or
    /// a status modifying a content stat). Read the values with
    /// <see cref="WorldReader.CopyStats"/>. Equal versions for the same <see cref="Key"/>
    /// mean the block is unchanged; a respawn of the key never repeats a version.
    /// </summary>
    public readonly uint StatsVersion;

    /// <summary>
    /// Changes whenever this entity's active statuses changed (applied, stacked, refreshed,
    /// expired, removed). Read them with <see cref="WorldReader.CopyStatuses"/>.
    /// </summary>
    public readonly uint StatusesVersion;

    public EntityView(
        int key, string id, string type, Vec2 position, int hp, int maxHp, float speed,
        uint facingBrad, EntityAction action, uint actionSeq = 0)
        : this(key, id, type, position, hp, maxHp, speed, facingBrad, action, actionSeq,
               0f, 0f, 0f, 0f, null, 0, 0u, 0u, 0u)
    {
    }

    /// <summary>Full protocol 3 view.</summary>
    public EntityView(
        int key, string id, string type, Vec2 position, int hp, int maxHp, float speed,
        uint facingBrad, EntityAction action, uint actionSeq,
        float z, float velX, float velY, float velZ,
        string? ownerId, int ownerKey, uint spawnSeq, uint statsVersion, uint statusesVersion)
    {
        Key = key;
        Id = id;
        Type = type;
        Position = position;
        Hp = hp;
        MaxHp = maxHp;
        Speed = speed;
        FacingBrad = facingBrad;
        Action = action;
        ActionSeq = actionSeq;
        Z = z;
        VelX = velX;
        VelY = velY;
        VelZ = velZ;
        OwnerId = ownerId;
        OwnerKey = ownerKey;
        SpawnSeq = spawnSeq;
        StatsVersion = statsVersion;
        StatusesVersion = statusesVersion;
    }
}
