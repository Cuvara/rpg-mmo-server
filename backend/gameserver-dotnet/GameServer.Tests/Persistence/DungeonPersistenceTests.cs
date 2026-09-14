using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Persistence;
using GameServer.Server;
using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Persistence;

/// <summary>
/// A dungeon run must cost the player nothing of their character and nothing of where
/// they stood (ADR-26 decision 5).
///
/// <para><c>player_states</c> holds ONE row per player with a single <c>map_id</c>, and
/// <see cref="PlayerSpawn.Resolve"/> discards coordinates belonging to another map. A
/// dungeon server that saved the row in full would therefore stamp the dungeon's id over
/// the origin map, and the player's next join on the origin map would put them at its
/// spawn point instead of where they left — a silent, permanent teleport as the price of
/// entering a dungeon.</para>
/// </summary>
public class DungeonPersistenceTests
{
    private const string OriginMap = "map_origin";
    private const string DungeonId = "dungeon_abyss#7";

    /// <summary>Where the player stood on the origin map before entering the dungeon.</summary>
    private static readonly Vec2 LeftFrom = new(42.5f, -17.25f);

    private static readonly MapBounds Bounds = MapBounds.FromSize(1000, 1000);

    private static AsyncSaver SaverFor(IPlayerStore store, EcsWorld world, string mapId, PlayerSaveScope scope) =>
        new(store, world, mapId, TimeSpan.FromHours(1), NullLogger.Instance, null, scope);

    private static EcsWorld WorldWith(string userId, Vec2 at, int hp)
    {
        var world = new EcsWorld();
        world.AddEntity(new EntityState
        {
            Id = userId,
            Type = "player",
            Position = at,
            Hp = hp,
            MaxHp = 100,
            Speed = ServerDefaults.DefaultPlayerSpeed,
        });
        return world;
    }

    /// <summary>
    /// The regression this exists for. Save a player from inside a dungeon, then resolve
    /// their spawn as the ORIGIN map's server would on their return: they must land where
    /// they left, carrying the HP the dungeon cost them.
    /// </summary>
    [Fact]
    public async Task SavingInsideADungeon_LeavesTheOriginMapPositionIntact()
    {
        const string userId = "user-delver";
        var store = new MemoryPlayerStore();

        // The origin map wrote the row on the way in.
        await store.SavePlayerAsync(new PlayerState(userId, LeftFrom.X, LeftFrom.Y, 100, 100, OriginMap), default);

        // Now the dungeon instance saves them, deep inside its own coordinate space and
        // 40 HP down.
        var dungeonWorld = WorldWith(userId, new Vec2(-400, 380), hp: 60);
        var saver = SaverFor(store, dungeonWorld, DungeonId, PlayerSaveScope.StatsOnly);
        Assert.True(await saver.SavePlayerAsync(userId));

        // The row still belongs to the origin map, with the origin map's coordinates.
        var row = await store.LoadPlayerAsync(userId, default);
        Assert.NotNull(row);
        Assert.Equal(OriginMap, row!.MapId);
        Assert.Equal(LeftFrom.X, row.X);
        Assert.Equal(LeftFrom.Y, row.Y);
        // ...and the map-independent half DID move.
        Assert.Equal(60, row.Hp);

        // The assertion that matters: the origin map hands them back their spot.
        var spawn = PlayerSpawn.Resolve(row, OriginMap, Bounds);
        Assert.True(spawn.PositionRestored);
        Assert.Equal(LeftFrom.X, spawn.Position.X);
        Assert.Equal(LeftFrom.Y, spawn.Position.Y);
        Assert.Equal(60, spawn.Hp);
    }

    /// <summary>
    /// The behaviour being prevented, pinned so the test above cannot pass for the wrong
    /// reason: a FULL save from the same dungeon really does teleport the player to the
    /// origin map's spawn point.
    /// </summary>
    [Fact]
    public async Task SavingInsideADungeonInFullScope_WouldTeleportThePlayerToTheSpawnPoint()
    {
        const string userId = "user-delver-full";
        var store = new MemoryPlayerStore();
        await store.SavePlayerAsync(new PlayerState(userId, LeftFrom.X, LeftFrom.Y, 100, 100, OriginMap), default);

        var dungeonWorld = WorldWith(userId, new Vec2(-400, 380), hp: 60);
        var saver = SaverFor(store, dungeonWorld, DungeonId, PlayerSaveScope.Full);
        Assert.True(await saver.SavePlayerAsync(userId));

        var row = await store.LoadPlayerAsync(userId, default);
        Assert.Equal(DungeonId, row!.MapId);

        var spawn = PlayerSpawn.Resolve(row, OriginMap, Bounds);
        Assert.False(spawn.PositionRestored);
        Assert.Equal(DungeonId, spawn.DiscardedMapId);
        Assert.Equal(PlayerSpawn.SpawnPoint.X, spawn.Position.X);
        Assert.Equal(PlayerSpawn.SpawnPoint.Y, spawn.Position.Y);
    }

    /// <summary>The periodic sweep honours the scope too, not only the on-removal save.</summary>
    [Fact]
    public async Task PeriodicSweepInADungeon_AlsoLeavesTheOriginRowsAlone()
    {
        const string userId = "user-sweep";
        var store = new MemoryPlayerStore();
        await store.SavePlayerAsync(new PlayerState(userId, LeftFrom.X, LeftFrom.Y, 100, 100, OriginMap), default);

        var dungeonWorld = WorldWith(userId, new Vec2(123, 456), hp: 71);
        await SaverFor(store, dungeonWorld, DungeonId, PlayerSaveScope.StatsOnly).SaveAllAsync();

        var row = await store.LoadPlayerAsync(userId, default);
        Assert.Equal(OriginMap, row!.MapId);
        Assert.Equal(LeftFrom.X, row.X);
        Assert.Equal(71, row.Hp);
    }

    /// <summary>
    /// A player who has never been saved anywhere still gets their HP recorded. The row
    /// created for them carries an EMPTY map id, which <see cref="PlayerSpawn.SameMap"/>
    /// reads as unattributable — so the next join spawns them at that map's spawn point,
    /// exactly where a missing row would have put them, with the HP preserved.
    /// </summary>
    [Fact]
    public async Task StatsOnlySave_ForAPlayerWithNoRow_RecordsHpWithNoMapClaim()
    {
        const string userId = "user-fresh";
        var store = new MemoryPlayerStore();

        var dungeonWorld = WorldWith(userId, new Vec2(5, 5), hp: 33);
        Assert.True(await SaverFor(store, dungeonWorld, DungeonId, PlayerSaveScope.StatsOnly)
            .SavePlayerAsync(userId));

        var row = await store.LoadPlayerAsync(userId, default);
        Assert.NotNull(row);
        Assert.Equal(string.Empty, row!.MapId);
        Assert.Equal(33, row.Hp);

        var spawn = PlayerSpawn.Resolve(row, OriginMap, Bounds);
        Assert.False(spawn.PositionRestored);
        Assert.Equal(33, spawn.Hp);
    }

    /// <summary>A map server is unaffected: it still writes the whole row.</summary>
    [Fact]
    public async Task MapServer_StillWritesTheWholeRow()
    {
        const string userId = "user-mapper";
        var store = new MemoryPlayerStore();
        await store.SavePlayerAsync(new PlayerState(userId, LeftFrom.X, LeftFrom.Y, 100, 100, OriginMap), default);

        var world = WorldWith(userId, new Vec2(7, 9), hp: 88);
        await SaverFor(store, world, "map_second", PlayerSaveScope.Full).SaveAllAsync();

        var row = await store.LoadPlayerAsync(userId, default);
        Assert.Equal("map_second", row!.MapId);
        Assert.Equal(7, row.X);
        Assert.Equal(9, row.Y);
        Assert.Equal(88, row.Hp);
    }
}
