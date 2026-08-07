using GameServer.Persistence;
using GameServer.Server;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Persistence;

/// <summary>
/// The join-time placement policy, exercised without a database, a socket or a
/// running server. <see cref="PlayerSpawn.Resolve"/> is the single point that
/// decides whether persisted coordinates may be reused, so every branch of that
/// decision is pinned here; the integration tests then prove the live join path
/// actually routes through it.
/// </summary>
public class PlayerSpawnTests
{
    private static readonly MapBounds Bounds = MapBounds.FromSize(1000, 1000);
    private const string ThisMap = "map_01";
    private const string OtherMap = "map_02";

    private static PlayerState Saved(string mapId, float x = 480f, float y = 12f, int hp = 77, int maxHp = 120)
        => new("u-1", x, y, hp, maxHp, mapId);

    // ── No saved state ──────────────────────────────────────────────────────

    [Fact]
    public void NoSavedState_SpawnsAtSpawnPointWithDefaultStats()
    {
        var d = PlayerSpawn.Resolve(null, ThisMap, Bounds);

        Assert.Equal(Bounds.Clamp(PlayerSpawn.SpawnPoint), d.Position);
        Assert.Equal(ServerDefaults.DefaultPlayerHp, d.Hp);
        Assert.Equal(ServerDefaults.DefaultPlayerHp, d.MaxHp);
        Assert.False(d.PositionRestored);
        Assert.Null(d.DiscardedMapId);
    }

    // ── Same map: the position is the whole point of persisting ─────────────

    [Fact]
    public void SameMap_RestoresExactPositionAndStats()
    {
        var d = PlayerSpawn.Resolve(Saved(ThisMap), ThisMap, Bounds);

        Assert.Equal(480f, d.Position.X);
        Assert.Equal(12f, d.Position.Y);
        Assert.Equal(77, d.Hp);
        Assert.Equal(120, d.MaxHp);
        Assert.True(d.PositionRestored);
        Assert.Null(d.DiscardedMapId);
    }

    [Fact]
    public void SameMap_ClampsAPositionTheMapNoLongerContains()
    {
        // The map was shrunk since the row was written; the restored entity must
        // not be recreated outside the play area.
        var small = MapBounds.FromSize(100, 100);

        var d = PlayerSpawn.Resolve(Saved(ThisMap, x: 480f, y: 12f), ThisMap, small);

        Assert.Equal(50f, d.Position.X); // clamped to MaxX
        Assert.Equal(12f, d.Position.Y); // already inside
        Assert.True(d.PositionRestored);
    }

    // ── Cross-map: the regression this class exists for ─────────────────────

    [Fact]
    public void DifferentMap_DiscardsStaleCoordinates()
    {
        var d = PlayerSpawn.Resolve(Saved(OtherMap, x: 480f, y: 12f), ThisMap, Bounds);

        Assert.Equal(Bounds.Clamp(PlayerSpawn.SpawnPoint), d.Position);
        Assert.False(d.PositionRestored);
        Assert.Equal(OtherMap, d.DiscardedMapId);
    }

    [Fact]
    public void DifferentMap_CarriesHpAcross()
    {
        // HP belongs to the character, not the ground under it: crossing a map
        // boundary must neither heal nor hurt.
        var d = PlayerSpawn.Resolve(Saved(OtherMap, hp: 13, maxHp: 120), ThisMap, Bounds);

        Assert.Equal(13, d.Hp);
        Assert.Equal(120, d.MaxHp);
    }

    [Theory]
    // player_states.map_id defaults to '': a row of unknown provenance must not be
    // treated as belonging to whatever map happens to read it.
    [InlineData("", ThisMap)]
    [InlineData(ThisMap, "")]
    [InlineData("", "")]
    // Map ids are opaque identifiers compared byte for byte everywhere else.
    [InlineData("MAP_01", ThisMap)]
    [InlineData("map_01 ", ThisMap)]
    [InlineData("map_010", ThisMap)]
    public void UnattributableOrMismatchedMapId_IsNotTreatedAsSameMap(string savedMap, string joiningMap)
    {
        Assert.False(PlayerSpawn.SameMap(savedMap, joiningMap));

        var d = PlayerSpawn.Resolve(Saved(savedMap), joiningMap, Bounds);
        Assert.False(d.PositionRestored);
        Assert.Equal(Bounds.Clamp(PlayerSpawn.SpawnPoint), d.Position);
    }

    [Fact]
    public void SameMap_IsOrdinalEquality()
    {
        Assert.True(PlayerSpawn.SameMap(ThisMap, ThisMap));
        Assert.True(PlayerSpawn.SameMap("dungeon_abc#7", "dungeon_abc#7"));
        Assert.False(PlayerSpawn.SameMap(null, ThisMap));
        Assert.False(PlayerSpawn.SameMap(ThisMap, null));
    }

    /// <summary>
    /// The bug in its original form: a player who last stood far from the origin on
    /// another map used to be recreated at those exact coordinates. Pinning the old
    /// behaviour as forbidden stops a future refactor from reinstating it.
    /// </summary>
    [Fact]
    public void CrossMapJoin_DoesNotLandOnTheOtherMapsCoordinates()
    {
        var stale = Saved(OtherMap, x: 480f, y: -377f);

        var d = PlayerSpawn.Resolve(stale, ThisMap, Bounds);

        Assert.NotEqual(new Vec2(480f, -377f), d.Position);
        Assert.Equal(0f, d.Position.X);
        Assert.Equal(0f, d.Position.Y);
    }
}
