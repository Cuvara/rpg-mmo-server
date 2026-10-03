using Shared.GameLogic.World;

namespace GameServer.Tests.Shared;

/// <summary>
/// MapGeometry, HeightField, StaticBox, spawns/portals and MapGeometryValidation (ADR-28).
/// </summary>
public class MapGeometryTests
{
    private static readonly MapBounds Bounds = MapBounds.FromSize(100f, 100f);

    /// <summary>3 x 3 samples over [0, 2] x [0, 2], height = x + 10 * y.</summary>
    private static HeightField Grid() =>
        new(0f, 0f, 1f, 3, 3, new[] { 0f, 1f, 2f, 10f, 11f, 12f, 20f, 21f, 22f });

    // ── Flat / protocol 2 world ──────────────────────────────────────────

    [Fact]
    public void Flat_IsGroundZeroEverywhere_WithNoColliders()
    {
        var geo = MapGeometry.Flat(Bounds);
        Assert.True(geo.IsFlat);
        Assert.Equal(0f, geo.GroundHeight(0f, 0f));
        Assert.Equal(0f, geo.GroundHeight(-49f, 37f));
        Assert.Equal(0f, geo.SupportHeight(3f, 3f, 0.4f, 100f));
        Assert.Equal(0, geo.Boxes.Length);
        Assert.False(geo.FindSpawn("default", out _));
    }

    // ── HeightField ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 1f, 11f)]
    [InlineData(2f, 2f, 22f)]      // far corner: last cell with t = 1
    [InlineData(0.5f, 0f, 0.5f)]
    [InlineData(0.5f, 0.5f, 5.5f)] // bilinear centre of the first cell
    [InlineData(1.5f, 1.75f, 19f)]
    [InlineData(-0.1f, 1f, 0f)]    // outside: flat
    [InlineData(1f, 2.1f, 0f)]
    public void HeightField_Bilinear(float x, float y, float expected)
    {
        Assert.Equal(expected, Grid().Sample(x, y), precision: 5);
    }

    [Fact]
    public void HeightField_CopiesSamples()
    {
        var samples = new[] { 1f, 1f, 1f, 1f };
        var hf = new HeightField(0f, 0f, 1f, 2, 2, samples);
        samples[0] = 99f;
        Assert.Equal(1f, hf.Sample(0f, 0f));
    }

    [Fact]
    public void HeightField_Unusable_SamplesZero()
    {
        var hf = new HeightField(0f, 0f, 1f, 3, 3, new[] { 5f, 5f, 5f }); // wrong length
        Assert.False(hf.IsUsable);
        Assert.Equal(0f, hf.Sample(1f, 1f));
    }

    // ── Boxes ────────────────────────────────────────────────────────────

    [Fact]
    public void StaticBox_NormalizesCorners()
    {
        var b = new StaticBox(4f, 2f, 1f, 2f, -2f, 0f);
        Assert.Equal(2f, b.MinX);
        Assert.Equal(4f, b.MaxX);
        Assert.Equal(-2f, b.MinY);
        Assert.Equal(0f, b.MinZ);
        Assert.Equal(1f, b.MaxZ);
    }

    [Fact]
    public void StaticBox_Overlap_IsStrict()
    {
        var b = new StaticBox(0f, 0f, 0f, 2f, 2f, 1f);
        // Standing exactly on top is not overlapping.
        Assert.False(b.CapsuleOverlaps(new Vec3(1f, 1f, 1f), 0.4f, 1.8f));
        // Footprint exactly touching the side is not overlapping.
        Assert.False(b.FootprintOverlaps(-0.5f, 1f, 0.5f));
        Assert.True(b.FootprintOverlaps(-0.49f, 1f, 0.5f));
        Assert.True(b.CapsuleOverlaps(new Vec3(1f, 1f, 0.5f), 0.4f, 1.8f));
        // Capsule entirely below the box.
        Assert.False(b.CapsuleOverlaps(new Vec3(1f, 1f, -2f), 0.4f, 1.8f));
    }

    [Fact]
    public void SupportHeight_TakesBoxTopsAtOrBelowCap()
    {
        var geo = new MapGeometry(Bounds, null, new[] { new StaticBox(0f, 0f, 0f, 2f, 2f, 1f) }, null, null);
        Assert.Equal(1f, geo.SupportHeight(1f, 1f, 0.4f, 1f));
        Assert.Equal(1f, geo.SupportHeight(-0.3f, 1f, 0.4f, 5f)); // footprint overlaps edge
        Assert.Equal(0f, geo.SupportHeight(1f, 1f, 0.4f, 0.5f));  // top above cap: a wall, not a floor
        Assert.Equal(0, geo.FirstOverlappingBox(new Vec3(1f, 1f, 0f), 0.4f, 1.8f));
        Assert.Equal(-1, geo.FirstOverlappingBox(new Vec3(5f, 5f, 0f), 0.4f, 1.8f));
    }

    [Fact]
    public void Constructor_CopiesArrays()
    {
        var boxes = new[] { new StaticBox(0f, 0f, 0f, 1f, 1f, 1f) };
        var geo = new MapGeometry(Bounds, null, boxes, null, null);
        boxes[0] = new StaticBox(5f, 5f, 5f, 6f, 6f, 6f);
        Assert.Equal(1f, geo.Boxes[0].MaxX);
    }

    // ── SweepSphere ──────────────────────────────────────────────────────

    [Fact]
    public void Sweep_HitsBoxFace_GrownByRadius()
    {
        var geo = new MapGeometry(Bounds, null, new[] { new StaticBox(2f, -1f, 0f, 3f, 1f, 3f) }, null, null);
        Assert.True(geo.SweepSphere(new Vec3(0f, 0f, 1f), new Vec3(4f, 0f, 1f), 0.5f, out Vec3 hit, out float t));
        Assert.Equal(0.375f, t, precision: 5);
        Assert.Equal(1.5f, hit.X, precision: 5);
    }

    [Fact]
    public void Sweep_MissesWhenParallelAndOutside_AndGrazeIsNotHit()
    {
        var geo = new MapGeometry(Bounds, null, new[] { new StaticBox(2f, -1f, 0f, 3f, 1f, 1f) }, null, null);
        Assert.False(geo.SweepSphere(new Vec3(0f, 0f, 3f), new Vec3(5f, 0f, 3f), 0.5f, out Vec3 hit, out float t));
        Assert.Equal(1f, t);
        Assert.Equal(new Vec3(5f, 0f, 3f), hit);
        // Sphere bottom exactly level with the box top: touching, not a hit.
        Assert.False(geo.SweepSphere(new Vec3(0f, 0f, 1.5f), new Vec3(5f, 0f, 1.5f), 0.5f, out _, out _));
    }

    [Fact]
    public void Sweep_FlatGround_SolvedExactly()
    {
        var geo = MapGeometry.Flat(Bounds);
        Assert.True(geo.SweepSphere(new Vec3(0f, 0f, 2f), new Vec3(0f, 0f, -2f), 0.5f, out Vec3 hit, out float t));
        Assert.Equal(0.375f, t);
        Assert.Equal(0.5f, hit.Z);
        Assert.False(geo.SweepSphere(new Vec3(0f, 0f, 1f), new Vec3(10f, 0f, 1f), 0.5f, out _, out _));
    }

    [Fact]
    public void Sweep_Heightfield_FindsTerrainWithinRefinement()
    {
        // Ramp z = 0.5 * x over x in [0, 5].
        var h = new float[6 * 2];
        for (int r = 0; r < 2; r++) for (int c = 0; c < 6; c++) h[r * 6 + c] = 0.5f * c;
        var geo = new MapGeometry(Bounds, new HeightField(0f, -1f, 1f, 6, 2, h), null, null, null);

        Assert.True(geo.SweepSphere(new Vec3(0f, 0f, 1.3f), new Vec3(5f, 0f, 1.3f), 0.1f, out Vec3 hit, out _));
        Assert.Equal(2.4f, hit.X, precision: 3); // bottom 1.2 meets 0.5 * x
    }

    [Fact]
    public void Sweep_IsDeterministic()
    {
        var h = new float[] { 0f, 0.7f, 1.3f, 0.2f, 0.9f, 2.1f, 0.4f, 1.1f, 0.3f };
        var geo = new MapGeometry(Bounds, new HeightField(-1f, -1f, 1f, 3, 3, h),
            new[] { new StaticBox(0.5f, 0.5f, 0f, 0.8f, 0.9f, 3f) }, null, null);
        var a = new Vec3(-1f, -0.7f, 2.5f);
        var b = new Vec3(1f, 0.9f, 0.2f);
        geo.SweepSphere(a, b, 0.15f, out Vec3 h1, out float t1);
        geo.SweepSphere(a, b, 0.15f, out Vec3 h2, out float t2);
        Assert.Equal(BitConverter.SingleToInt32Bits(t1), BitConverter.SingleToInt32Bits(t2));
        Assert.Equal(h1, h2);
    }

    // ── Spawns and portals ───────────────────────────────────────────────

    [Fact]
    public void FindSpawn_ByOrdinalName()
    {
        var geo = new MapGeometry(Bounds, null, null,
            new[] { new SpawnPoint("default", new Vec3(1f, 2f, 0f)), new SpawnPoint("gate", new Vec3(5f, 5f, 1f)) }, null);
        Assert.True(geo.FindSpawn("gate", out SpawnPoint s));
        Assert.Equal(new Vec3(5f, 5f, 1f), s.Position);
        Assert.False(geo.FindSpawn("Gate", out _));
        Assert.False(geo.FindSpawn(null!, out _));
    }

    [Fact]
    public void FindPortal_UsesUprightCylinder()
    {
        var portal = new Portal("to_dungeon", new Vec3(10f, 10f, 0f), 1f, 2f, "dungeon_01", "entry");
        var geo = new MapGeometry(Bounds, null, null, null, new[] { portal });
        Assert.Equal(0, geo.FindPortal(new Vec3(10.5f, 10.5f, 1.5f)));
        Assert.Equal(0, geo.FindPortal(new Vec3(11f, 10f, 0f)));   // on the rim
        Assert.Equal(-1, geo.FindPortal(new Vec3(10f, 10f, 2.5f))); // above it
        Assert.Equal(-1, geo.FindPortal(new Vec3(12f, 10f, 0f)));
    }

    // ── Validation ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_FlatMap_IsValid()
    {
        var errors = new List<string>();
        Assert.True(MapGeometryValidation.Validate("map_01", MapGeometry.Flat(Bounds), errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_FullValidMap_IsValid()
    {
        var geo = new MapGeometry(Bounds, Grid(),
            new[] { new StaticBox(0f, 0f, 0f, 1f, 1f, 1f) },
            new[] { new SpawnPoint("default", Vec3.Zero) },
            new[] { new Portal("p", new Vec3(5f, 5f, 0f), 1f, 2f, "map_02", "default") });
        var errors = new List<string>();
        Assert.True(MapGeometryValidation.Validate("map_01", geo, errors), string.Join("\n", errors));
    }

    [Fact]
    public void Validate_ReportsEveryProblem_NamingTheElement()
    {
        var geo = new MapGeometry(
            new MapBounds(0f, 0f, 0f, 10f),                                  // zero width
            new HeightField(0f, 0f, 0f, 1, 3, new[] { 0f, float.NaN }),     // too few, bad cell, length, NaN
            new[] { new StaticBox(0f, 0f, 0f, 1f, 1f, 0f), new StaticBox(float.NaN, 0f, 0f, 1f, 1f, 1f) },
            new[] { new SpawnPoint("a", Vec3.Zero), new SpawnPoint("a", Vec3.Zero), new SpawnPoint("", new Vec3(50f, 0f, 0f)) },
            new[] { new Portal("p", Vec3.Zero, 0f, -1f, "", " ") });

        var errors = new List<string>();
        Assert.False(MapGeometryValidation.Validate("broken", geo, errors));

        string all = string.Join("\n", errors);
        Assert.All(errors, e => Assert.StartsWith("map broken: ", e));
        Assert.Contains("bounds have zero width", all);
        Assert.Contains("heightfield: needs at least 2 x 2", all);
        Assert.Contains("heightfield: cell size", all);
        Assert.Contains("columns x rows is 3", all);
        Assert.Contains("is not finite", all);
        Assert.Contains("box 0: has zero extent", all);
        Assert.Contains("box 1: has a non-finite edge", all);
        Assert.Contains("spawn 'a': duplicate name", all);
        Assert.Contains("spawn #2: name is empty", all);
        Assert.Contains("spawn #2: lies outside the map bounds", all);
        Assert.Contains("portal 'p': radius", all);
        Assert.Contains("portal 'p': height", all);
        Assert.Contains("portal 'p': target map id is empty", all);
        Assert.Contains("portal 'p': target spawn is empty", all);
    }

    [Fact]
    public void Validate_RejectsTooManyBoxes()
    {
        var boxes = new StaticBox[MapGeometryValidation.MaxBoxes + 1];
        for (int i = 0; i < boxes.Length; i++) boxes[i] = new StaticBox(0f, 0f, 0f, 1f, 1f, 1f);
        var errors = new List<string>();
        Assert.False(MapGeometryValidation.Validate("big", new MapGeometry(Bounds, null, boxes, null, null), errors));
        Assert.Single(errors);
    }
}
