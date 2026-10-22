using System.Text.Json;
using GameServer.Content;
using GameServer.Gameplay;
using GameServer.Observability;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Observability;

/// <summary>
/// <c>/status</c> reports the map id and the play-area rectangle in force, so a reader can
/// judge positions against the bounds the server clamped to — including on a map with no
/// content file (map_01), whose bounds exist nowhere else.
/// </summary>
public class ServerStatusMapBoundsTests
{
    private static JsonElement Serialize(ServerStatus status) =>
        JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(status, ServerStatusContext.Default.ServerStatus))
            .RootElement.Clone();

    /// <summary>
    /// A map without a content file: the bounds are the configured size centred on the origin,
    /// read from the same MapLoader result Program.cs builds the server from.
    /// </summary>
    [Fact]
    public void AMapWithoutAFile_ReportsTheConfiguredSize()
    {
        MapLoadResult map = MapLoader.Load(ContentLoader.ResolveDefaultDirectory(), "map_01", MapBounds.FromSize(400f, 300f));
        Assert.False(map.FromFile, "map_01 gained a content file; pick a map id that has none");

        JsonElement root = Serialize(new ServerStatus
        {
            MapId = "map_01",
            MapBounds = MapBoundsStatus.From(map.Geometry.Bounds, map.FromFile),
        });

        Assert.Equal("map_01", root.GetProperty("map_id").GetString());
        JsonElement b = root.GetProperty("map_bounds");
        Assert.Equal(-200f, b.GetProperty("min_x").GetSingle());
        Assert.Equal(-150f, b.GetProperty("min_y").GetSingle());
        Assert.Equal(200f, b.GetProperty("max_x").GetSingle());
        Assert.Equal(150f, b.GetProperty("max_y").GetSingle());
        Assert.Equal("configured_size", b.GetProperty("source").GetString());
    }

    /// <summary>A map with a content file reports the file's bounds, not the configured size.</summary>
    [Fact]
    public void AMapWithAFile_ReportsTheFilesBounds()
    {
        MapBounds configured = MapBounds.FromSize(400f, 300f);
        MapLoadResult map = MapLoader.Load(ContentLoader.ResolveDefaultDirectory(), "dev_arena", configured);
        Assert.True(map.FromFile, "dev_arena has no content file any more; this test needs one");
        Assert.NotEqual(configured, map.Geometry.Bounds);

        JsonElement b = Serialize(new ServerStatus
        {
            MapId = "dev_arena",
            MapBounds = MapBoundsStatus.From(map.Geometry.Bounds, map.FromFile),
        }).GetProperty("map_bounds");

        Assert.Equal(map.Geometry.Bounds.MinX, b.GetProperty("min_x").GetSingle());
        Assert.Equal(map.Geometry.Bounds.MinY, b.GetProperty("min_y").GetSingle());
        Assert.Equal(map.Geometry.Bounds.MaxX, b.GetProperty("max_x").GetSingle());
        Assert.Equal(map.Geometry.Bounds.MaxY, b.GetProperty("max_y").GetSingle());
        Assert.Equal("map_file", b.GetProperty("source").GetString());
    }
}
