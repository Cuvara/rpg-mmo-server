using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Tests.World;

/// <summary>
/// The spatial index must return <b>exactly</b> what the brute-force scan returns — the
/// same entities, in the same order — for every query, not merely something similar.
///
/// <para>This is the deliverable as much as the index is. An index that is 99.9% right
/// does not fail loudly: it drops one entity from one snapshot, and the client sees a
/// player blink out of existence. So the two implementations are both kept and run
/// against each other here over randomised populations, with the cases that break naive
/// grids called out individually: entities exactly on the radius boundary, entities in
/// diagonal neighbour cells, entities on a cell edge, negative coordinates (where
/// truncation and flooring disagree), and positions far outside the map.</para>
///
/// <para><b>Order is asserted, not just membership.</b> The delta encoder interns entity
/// ids in AOI arrival order, so a reordering changes the bytes on the wire even when the
/// set is identical. That would be a wire change disguised as an optimisation.</para>
/// </summary>
public class AoiIndexDifferentialTests
{
    private const float Radius = GameConstants.DefaultAoiRadius;

    /// <summary>Brute force: the public overload, which never consults the index.</summary>
    private static List<EntityState> BruteForce(EcsWorld world, Vec2 center, float radius) =>
        world.GetEntitiesInRange(center, radius);

    /// <summary>
    /// Indexed: through <see cref="EcsWorld.ReadAll"/>, which rebuilds the index and hands
    /// out a reader — the exact path the tick loop's gather phase takes.
    /// </summary>
    private static List<EntityState> Indexed(EcsWorld world, Vec2 center, float radius)
    {
        var result = new List<EntityState>();
        world.ReadAll(reader =>
        {
            var buffer = new EntityState[16];
            int count = reader.GetEntitiesInRange(center, radius, buffer);
            if (count > buffer.Length)
            {
                buffer = new EntityState[count];
                count = reader.GetEntitiesInRange(center, radius, buffer);
            }
            for (int i = 0; i < count; i++) result.Add(buffer[i]);
        });
        return result;
    }

    private static void AssertIdentical(EcsWorld world, Vec2 center, float radius, string because)
    {
        List<EntityState> expected = BruteForce(world, center, radius);
        List<EntityState> actual = Indexed(world, center, radius);

        Assert.True(expected.Count == actual.Count,
            $"{because}: brute force found {expected.Count}, index found {actual.Count}. " +
            $"missing=[{string.Join(",", expected.Select(e => e.Id).Except(actual.Select(a => a.Id)))}] " +
            $"extra=[{string.Join(",", actual.Select(a => a.Id).Except(expected.Select(e => e.Id)))}]");

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].Id == actual[i].Id,
                $"{because}: order differs at {i} — brute force '{expected[i].Id}', " +
                $"index '{actual[i].Id}'. Order is wire-visible: the delta encoder interns " +
                "ids in AOI arrival order.");
            Assert.Equal(expected[i].Position.X, actual[i].Position.X);
            Assert.Equal(expected[i].Position.Y, actual[i].Position.Y);
        }
    }

    // ── Randomised ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RandomisedPopulations_MatchBruteForceExactly(int seed)
    {
        var rng = new Random(seed);
        using var world = new EcsWorld();

        int population = 40 + rng.Next(160);
        for (int i = 0; i < population; i++)
        {
            // Deliberately wider than the map, so out-of-bounds entities are covered.
            float x = (float)(rng.NextDouble() * 1400 - 700);
            float y = (float)(rng.NextDouble() * 1400 - 700);
            world.AddEntity(i % 3 == 0
                ? TestHelpers.CreateMob($"m{i}", x, y)
                : TestHelpers.CreatePlayer($"p{i}", x, y));
        }

        for (int q = 0; q < 40; q++)
        {
            var center = new Vec2(
                (float)(rng.NextDouble() * 1400 - 700),
                (float)(rng.NextDouble() * 1400 - 700));
            float radius = (float)(rng.NextDouble() * 120);

            AssertIdentical(world, center, radius, $"seed {seed}, query {q}, radius {radius:F2}");
        }
    }

    /// <summary>
    /// Positions that move between rebuilds are the failure mode an incrementally
    /// maintained index has and this one does not — the index is rebuilt from component
    /// storage inside the same read scope that queries it. Moving everything between
    /// queries and re-checking is how that gets verified rather than asserted.
    /// </summary>
    [Fact]
    public void AfterEntitiesMove_TheIndexAgreesAgain()
    {
        var rng = new Random(99);
        using var world = new EcsWorld();

        for (int i = 0; i < 60; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}",
                (float)(rng.NextDouble() * 400 - 200), (float)(rng.NextDouble() * 400 - 200)));
        }

        for (int round = 0; round < 10; round++)
        {
            AssertIdentical(world, new Vec2(0, 0), Radius, $"round {round} before move");

            for (int i = 0; i < 60; i++)
            {
                EntityState e = world.GetEntity($"p{i}")!.Value;
                e.Position = new Vec2(
                    e.Position.X + (float)(rng.NextDouble() * 60 - 30),
                    e.Position.Y + (float)(rng.NextDouble() * 60 - 30));
                world.AddEntity(e);
            }

            AssertIdentical(world, new Vec2(0, 0), Radius, $"round {round} after move");
        }
    }

    // ── The cases that break naive grids ─────────────────────────────────────

    /// <summary>
    /// An entity at exactly the radius is inside — <c>DistanceSq &lt;= radiusSq</c>. A grid
    /// that trims its candidate rectangle too tightly loses it.
    /// </summary>
    [Fact]
    public void EntityExactlyOnTheRadiusBoundary_IsIncluded()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("on", Radius, 0));
        world.AddEntity(TestHelpers.CreatePlayer("justOutside", Radius + 0.001f, 0));

        AssertIdentical(world, new Vec2(0, 0), Radius, "radius boundary");

        List<EntityState> got = Indexed(world, new Vec2(0, 0), Radius);
        Assert.Contains(got, e => e.Id == "on");
        Assert.DoesNotContain(got, e => e.Id == "justOutside");
    }

    /// <summary>
    /// Diagonal neighbours: an entity in the corner cell is within the radius even though
    /// the cell centre is not. A grid that visits only the four orthogonal neighbours
    /// misses it.
    /// </summary>
    [Fact]
    public void EntitiesInDiagonalNeighbourCells_AreFound()
    {
        using var world = new EcsWorld();

        // Cell size is the AOI radius, so these land in the four diagonal neighbours of
        // the query's own cell while staying inside the circle.
        float d = Radius * 0.5f;
        world.AddEntity(TestHelpers.CreatePlayer("ne", d, d));
        world.AddEntity(TestHelpers.CreatePlayer("nw", -d, d));
        world.AddEntity(TestHelpers.CreatePlayer("se", d, -d));
        world.AddEntity(TestHelpers.CreatePlayer("sw", -d, -d));

        AssertIdentical(world, new Vec2(0.1f, 0.1f), Radius, "diagonal neighbours");
        Assert.Equal(4, Indexed(world, new Vec2(0.1f, 0.1f), Radius).Count);
    }

    /// <summary>
    /// Negative coordinates are where truncation and flooring disagree: <c>(int)(-0.4)</c>
    /// is 0 but the cell is -1. Getting this wrong puts an entity one cell away from where
    /// its neighbours look for it, and only for half the map.
    /// </summary>
    [Fact]
    public void NegativeCoordinates_BucketByFloorNotTruncation()
    {
        using var world = new EcsWorld();

        for (int i = 0; i < 24; i++)
        {
            float a = i * 0.35f;
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", -a, a - 4f));
        }

        foreach (float cx in new[] { -0.4f, -0.001f, 0f, 0.001f, -Radius, Radius })
        {
            AssertIdentical(world, new Vec2(cx, -cx), Radius, $"centre {cx}");
        }
    }

    [Fact]
    public void EntitiesExactlyOnCellEdges_AreFound()
    {
        using var world = new EcsWorld();

        foreach (int k in new[] { -2, -1, 0, 1, 2 })
        {
            world.AddEntity(TestHelpers.CreatePlayer($"edge{k}", k * Radius, 0));
            world.AddEntity(TestHelpers.CreatePlayer($"edgeY{k}", 0, k * Radius));
        }

        foreach (int k in new[] { -2, -1, 0, 1, 2 })
        {
            AssertIdentical(world, new Vec2(k * Radius, 0), Radius, $"on cell edge {k}");
            AssertIdentical(world, new Vec2(k * Radius - 0.001f, 0), Radius, $"just below edge {k}");
        }
    }

    [Fact]
    public void HugeRadius_FallsBackAndStillMatches()
    {
        var rng = new Random(7);
        using var world = new EcsWorld();
        for (int i = 0; i < 50; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}",
                (float)(rng.NextDouble() * 2000 - 1000), (float)(rng.NextDouble() * 2000 - 1000)));
        }

        AssertIdentical(world, new Vec2(0, 0), 10_000f, "huge radius");
        Assert.Equal(50, Indexed(world, new Vec2(0, 0), 10_000f).Count);
    }

    [Fact]
    public void ZeroRadius_MatchesBruteForce()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("origin", 0, 0));
        world.AddEntity(TestHelpers.CreatePlayer("near", 0.5f, 0));

        AssertIdentical(world, new Vec2(0, 0), 0f, "zero radius");
    }

    [Fact]
    public void EmptyWorld_MatchesBruteForce()
    {
        using var world = new EcsWorld();
        AssertIdentical(world, new Vec2(0, 0), Radius, "empty world");
    }

    [Fact]
    public void SingleEntity_MatchesBruteForce()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("only", 3, 4));

        AssertIdentical(world, new Vec2(0, 0), Radius, "single entity, in range");
        AssertIdentical(world, new Vec2(500, 500), Radius, "single entity, out of range");
    }

    /// <summary>
    /// Everyone in one cell is the case where a grid cannot help — the answer genuinely is
    /// "everybody". It must still be exactly right, and it is the distribution the
    /// benchmark probes use, which is why the measured win there is smaller than the
    /// spread-out case.
    /// </summary>
    [Fact]
    public void DenselyClusteredPopulation_MatchesBruteForce()
    {
        using var world = new EcsWorld();
        for (int i = 0; i < 200; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", (i % 20) * 0.5f, (i / 20) * 0.5f));
        }

        AssertIdentical(world, new Vec2(0, 0), Radius, "dense cluster");
        Assert.Equal(200, Indexed(world, new Vec2(0, 0), Radius).Count);
    }

    /// <summary>
    /// The overflow contract must survive the index: a short buffer still reports the
    /// total needed, and refilling at that size gives the whole set.
    /// </summary>
    [Fact]
    public void OverflowContract_IsPreservedThroughTheIndex()
    {
        using var world = new EcsWorld();
        for (int i = 0; i < 12; i++) world.AddEntity(TestHelpers.CreatePlayer($"p{i}", i * 0.5f, 0));

        world.ReadAll(reader =>
        {
            int needed = reader.GetEntitiesInRange(new Vec2(0, 0), Radius, Span<EntityState>.Empty);
            Assert.Equal(12, needed);

            var small = new EntityState[5];
            Assert.Equal(12, reader.GetEntitiesInRange(new Vec2(0, 0), Radius, small));
            Assert.All(small, e => Assert.False(string.IsNullOrEmpty(e.Id)));

            var exact = new EntityState[12];
            Assert.Equal(12, reader.GetEntitiesInRange(new Vec2(0, 0), Radius, exact));
        });
    }
}
