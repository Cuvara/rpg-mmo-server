using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Server;
using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Server;

/// <summary>
/// <see cref="ServerOptions.Aoi"/> has to reach two independent places: the gather radius
/// on the tick loop, and the spatial index's cell size on the world.
///
/// <para><b>Why this needs a test at all.</b> Wiring only the gather radius produces a
/// server that is entirely correct — both the index and the brute-force scan return the
/// same entities at any cell size, because <c>SpatialGrid.Query</c> derives its cell span
/// from the radius it is handed rather than assuming a 3x3 neighbourhood. The only symptom
/// is an index that narrows less than it should, which is a performance defect with no
/// failing assertion anywhere: the differential tests stay green, the snapshots stay byte
/// correct, and a benchmark would have to be looking for it.</para>
/// </summary>
public class AoiRadiusWiringTests
{
    private const string Secret = "aoi-radius-wiring-test-secret-32b!!";

    private static ServerOptions NewOptions(AoiSettings aoi) => new()
    {
        ServerAddr = ":0",
        ServerId = "gs-aoi-wiring",
        MapId = "map_aoi_wiring",
        Mode = "map",
        TickRate = 20,
        Capacity = 4,
        JwtSecret = Secret,
        JoinTokenSecret = Secret,
        Aoi = aoi,
        LoggerFactory = NullLoggerFactory.Instance,
    };

    private static AoiSettings Radius(string raw)
    {
        Assert.True(AoiSettings.TryCreate(
            raw, GameConstants.DefaultMapWidth, GameConstants.DefaultMapHeight,
            out AoiSettings? s, out string? err), err);
        return s!;
    }

    [Theory]
    [InlineData("12.5")]
    [InlineData("25")]
    [InlineData("120")]
    public async Task ConfiguredRadius_ReachesBothTheGatherAndTheIndex(string raw)
    {
        AoiSettings aoi = Radius(raw);
        await using var host = new GameServerHost(NewOptions(aoi));

        Assert.Equal(aoi.Radius, host.WiredAoiRadius);
        Assert.Equal(aoi.Radius, host.WiredAoiCellSize);
    }

    /// <summary>
    /// Guards the guard. If the test above ever ran at the compiled-in default it would
    /// pass against a host that ignored the option entirely.
    /// </summary>
    [Fact]
    public async Task TheTestValuesAreNotTheCompiledInDefault()
    {
        AoiSettings aoi = Radius("25");
        Assert.NotEqual(GameConstants.DefaultAoiRadius, aoi.Radius);

        await using var host = new GameServerHost(NewOptions(aoi));
        Assert.NotEqual(GameConstants.DefaultAoiRadius, host.WiredAoiRadius);
        Assert.NotEqual(GameConstants.DefaultAoiRadius, host.WiredAoiCellSize);
    }

    [Fact]
    public async Task Unconfigured_StillUsesTheCompiledInDefault()
    {
        await using var host = new GameServerHost(NewOptions(AoiSettings.Default));

        Assert.Equal(GameConstants.DefaultAoiRadius, host.WiredAoiRadius);
        Assert.Equal(GameConstants.DefaultAoiRadius, host.WiredAoiCellSize);
    }

    /// <summary>
    /// The behavioural half: a smaller radius must actually exclude entities a larger one
    /// includes. Asserted through the world, so it covers the predicate and not just the
    /// plumbing.
    /// </summary>
    [Theory]
    [InlineData(20f, 1)]    // only the entity at 10 units
    [InlineData(40f, 2)]    // plus the one at 30
    [InlineData(120f, 3)]   // plus the one at 100
    public void SmallerRadius_ExcludesWhatALargerOneIncludes(float radius, int expected)
    {
        using var world = new EcsWorld(1, radius);
        world.AddEntity(TestHelpers.CreatePlayer("near", x: 10f, y: 0f));
        world.AddEntity(TestHelpers.CreatePlayer("mid", x: 30f, y: 0f));
        world.AddEntity(TestHelpers.CreatePlayer("far", x: 100f, y: 0f));

        var buffer = new EntityView[8];
        int n = world.GetEntitiesInRange(new Vec2(0f, 0f), radius, buffer.AsSpan());

        Assert.Equal(expected, n);
    }

    /// <summary>
    /// Both arms must agree at a NON-default cell size. The differential suite pins this at
    /// 50 only, so a grid whose cell size now varies needs the same proof at the sizes it
    /// can take — the index is only sound because the query derives its cell span from the
    /// radius, and that is exactly the code a cell-size change exercises differently.
    /// </summary>
    [Theory]
    [InlineData(15f)]
    [InlineData(50f)]
    [InlineData(130f)]
    public void IndexAndScanAgree_AtAnyCellSize(float cellSize)
    {
        // Gate threshold forced to 1. The production gate wants 96 occupied cells before it
        // trusts the index, and a 200-unit spread at cellSize 130 occupies about four — so
        // without this the "indexed" world would quietly fall back to the scan and the test
        // would compare the scan against itself and pass. An empty comparison reads exactly
        // like a passing one.
        using var indexed = new EcsWorld(1, cellSize) { AoiIndexGateThreshold = 1 };
        using var scanned = new EcsWorld(1, cellSize) { AoiIndexEnabled = false };

        // Spread out, so the occupancy gate lets the index actually run.
        for (int i = 0; i < 400; i++)
        {
            float x = ((i * 37) % 200) - 100f;
            float y = ((i * 71) % 200) - 100f;
            indexed.AddEntity(TestHelpers.CreatePlayer($"e{i}", x, y));
            scanned.AddEntity(TestHelpers.CreatePlayer($"e{i}", x, y));
        }

        var a = new EntityView[512];
        var b = new EntityView[512];

        foreach (var centre in new[] { new Vec2(0, 0), new Vec2(-90, 40), new Vec2(77, -66) })
        {
            int na = 0, nb = 0;
            int cells = 0;
            indexed.ReadAll(r =>
            {
                cells = indexed.AoiIndexOccupiedCells;
                na = r.GetEntitiesInRange(centre, cellSize, a.AsSpan());
            });
            scanned.ReadAll(r => nb = r.GetEntitiesInRange(centre, cellSize, b.AsSpan()));

            // Guards the guard: one occupied cell means the "index" is one bucket holding
            // everything, which narrows nothing and agrees with the scan trivially.
            Assert.True(cells > 1,
                $"cellSize {cellSize} put all {indexed.EntityCount} entities in {cells} cell(s); " +
                "the index cannot narrow anything, so this comparison proves nothing");

            Assert.Equal(nb, na);
            for (int i = 0; i < na; i++) Assert.Equal(b[i].Id, a[i].Id);
        }
    }
}
