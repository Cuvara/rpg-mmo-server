using Shared.GameLogic.Components;
using GameServer.Server;

namespace GameServer.Persistence;

/// <summary>
/// Where a joining player is placed, and with what carried state.
/// </summary>
/// <param name="Position">Entity position at join, already clamped into the map bounds.</param>
/// <param name="Hp">Current HP the entity starts with.</param>
/// <param name="MaxHp">Maximum HP the entity starts with.</param>
/// <param name="PositionRestored">
/// True when <see cref="Position"/> came from the persisted row; false when the player
/// was placed at the map's spawn point (no saved state, or state from another map).
/// </param>
/// <param name="DiscardedMapId">
/// When the saved coordinates were rejected because they belong to a different map, the
/// map id they belonged to — for logging. Null in every other case.
/// </param>
public readonly record struct SpawnDecision(
    Vec2 Position,
    int Hp,
    int MaxHp,
    bool PositionRestored,
    string? DiscardedMapId);

/// <summary>Outcome of the character spawn policy (ADR-31): <see cref="SpawnDecision"/> plus height, level and facing.</summary>
public readonly record struct CharacterSpawnDecision(
    Vec3 Position,
    int Hp,
    int MaxHp,
    bool PositionRestored,
    string? DiscardedMapId,
    int? Level,
    float? Yaw);

/// <summary>
/// Decides where a player entity starts when it is (re)created on join.
///
/// <para><b>Position is map-scoped; everything else is not.</b> A coordinate pair only
/// means something relative to the map it was recorded on — (480, 12) is a street corner
/// on one map and the inside of a cliff on another. HP is a property of the character,
/// not of the ground under it, so it carries across maps unchanged.</para>
///
/// <para>This is why the map id is compared at all: <c>player_states</c> holds exactly one
/// row per player, overwritten by whichever server currently hosts them, so a row reached
/// through a different map's server is stale by definition. Restoring its coordinates
/// teleported the player to wherever they last stood on the *other* map.</para>
///
/// <para>Pure and side-effect free so the policy can be tested exhaustively without a
/// database, a socket or a running server.</para>
/// </summary>
public static class PlayerSpawn
{
    /// <summary>
    /// The spawn point of every map, before clamping: the origin. Map bounds are built
    /// centered on it (<see cref="MapBounds.FromSize"/>), so this is the middle of the
    /// play area and gives equal room in every direction. Clamping still applies, for
    /// bounds that were given explicit edges not containing the origin.
    /// </summary>
    public static readonly Vec2 SpawnPoint = Vec2.Zero;

    /// <summary>
    /// Resolve the join-time position and carried stats for <paramref name="saved"/>.
    /// </summary>
    /// <param name="saved">The persisted row, or null when the player has none.</param>
    /// <param name="mapId">The map id of the server being joined.</param>
    /// <param name="bounds">Bounds of the map being joined; the result is clamped into them.</param>
    public static SpawnDecision Resolve(PlayerState? saved, string mapId, in MapBounds bounds)
    {
        Vec2 spawn = bounds.Clamp(SpawnPoint);

        // No saved state at all: a brand-new character.
        if (saved is null)
        {
            return new SpawnDecision(
                spawn,
                ServerDefaults.DefaultPlayerHp,
                ServerDefaults.DefaultPlayerHp,
                PositionRestored: false,
                DiscardedMapId: null);
        }

        if (SameMap(saved.MapId, mapId))
        {
            // Clamp the restored position too: the map may have been resized since the
            // state was written, and an out-of-bounds entity must not be recreated.
            return new SpawnDecision(
                bounds.Clamp(new Vec2(saved.X, saved.Y)),
                saved.Hp,
                saved.MaxHp,
                PositionRestored: true,
                DiscardedMapId: null);
        }

        // Different map (or unattributable state — see SameMap). The coordinates mean
        // nothing here, so drop them for the spawn point. HP is map-independent and is
        // carried across unchanged: crossing a map boundary must not heal or hurt anyone.
        return new SpawnDecision(
            spawn,
            saved.Hp,
            saved.MaxHp,
            PositionRestored: false,
            DiscardedMapId: saved.MapId);
    }

    /// <summary>
    /// Whether saved coordinates may be reused on the map being joined.
    ///
    /// <para>Both ids must be non-empty and equal. An empty id on either side is treated
    /// as a MISMATCH rather than a wildcard: <c>player_states.map_id</c> defaults to the
    /// empty string, so a row written before the column meant anything (or by a server
    /// started without a map id) has unknown provenance. Spawning such a player at the
    /// map's spawn point is recoverable; dropping them at coordinates from an unknown map
    /// is not.</para>
    ///
    /// <para>Comparison is <see cref="StringComparison.Ordinal"/> — map ids are opaque
    /// identifiers matched byte for byte everywhere else (the registry keys and the
    /// join-token claim), and a culture-sensitive compare here could disagree with them.</para>
    /// </summary>
    /// <summary>
    /// <see cref="Resolve(PlayerState?, string, in MapBounds)"/> with the map's own default
    /// spawn (ADR-28: the spawn point named <c>default</c> in the map file) used instead of
    /// the origin wherever the policy would have spawned at the origin.
    /// </summary>
    public static SpawnDecision Resolve(PlayerState? saved, string mapId, in MapBounds bounds, Vec2? defaultSpawn)
    {
        SpawnDecision d = Resolve(saved, mapId, bounds);
        if (defaultSpawn is { } spawn && !d.PositionRestored)
        {
            d = d with { Position = bounds.Clamp(spawn) };
        }

        return d;
    }

    /// <summary>
    /// The character form of the spawn policy (ADR-31): the saved position — WITH height —
    /// is reused only when the row belongs to this map; otherwise the player starts at
    /// <paramref name="defaultSpawn"/>. HP, level and facing carry across maps unchanged. A
    /// restored height below the ground (the map changed under the save) is lifted onto it
    /// by the caller's ground lookup, not here.
    /// </summary>
    public static CharacterSpawnDecision Resolve(
        CharacterState? saved, string mapId, in MapBounds bounds, Vec3 defaultSpawn)
    {
        Vec2 spawnXY = bounds.Clamp(new Vec2(defaultSpawn.X, defaultSpawn.Y));
        var spawn = new Vec3(spawnXY.X, spawnXY.Y, defaultSpawn.Z);

        if (saved is null)
        {
            return new CharacterSpawnDecision(
                spawn, ServerDefaults.DefaultPlayerHp, ServerDefaults.DefaultPlayerHp,
                PositionRestored: false, DiscardedMapId: null, Level: null, Yaw: null);
        }

        int maxHp = saved.MaxHp > 0 ? saved.MaxHp : ServerDefaults.DefaultPlayerHp;
        int hp = saved.Hp > 0 ? Math.Min(saved.Hp, maxHp) : saved.Hp;
        int? level = saved.Level > 0 ? saved.Level : null;

        if (SameMap(saved.MapId, mapId) && float.IsFinite(saved.X) && float.IsFinite(saved.Y) && float.IsFinite(saved.Z))
        {
            Vec2 xy = bounds.Clamp(new Vec2(saved.X, saved.Y));
            return new CharacterSpawnDecision(
                new Vec3(xy.X, xy.Y, saved.Z), hp, maxHp,
                PositionRestored: true, DiscardedMapId: null, Level: level, Yaw: saved.Yaw);
        }

        return new CharacterSpawnDecision(
            spawn, hp, maxHp, PositionRestored: false, DiscardedMapId: saved.MapId, Level: level, Yaw: saved.Yaw);
    }

    public static bool SameMap(string? savedMapId, string? joiningMapId) =>
        !string.IsNullOrEmpty(savedMapId) &&
        !string.IsNullOrEmpty(joiningMapId) &&
        string.Equals(savedMapId, joiningMapId, StringComparison.Ordinal);
}
