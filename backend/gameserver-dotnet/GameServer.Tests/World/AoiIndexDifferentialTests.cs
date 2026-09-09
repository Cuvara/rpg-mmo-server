using GameServer.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.World;

/// <summary>
/// The spatial index must return <b>exactly</b> what the brute-force scan returns — the
/// same entities, in the same order — for every query, not merely something similar.
///
/// <para>This is the deliverable as much as the index is. An index that is 99.9% right
/// does not fail loudly: it drops one entity from one snapshot, and the client sees a
/// player blink out of existence. So both implementations are kept and run against each
/// other here over randomised populations, with the cases that break naive grids called
/// out individually: entities exactly on the radius boundary, entities in diagonal
/// neighbour cells, entities on a cell edge, duplicate positions, everything in one cell,
/// negative coordinates (where truncation and flooring disagree), and positions far
/// outside the map.</para>
///
/// <para><b>Order is asserted, not just membership.</b> The delta encoder interns entity
/// ids in AOI arrival order, so a reordering changes the bytes on the wire even when the
/// set is identical. That would be a wire change disguised as an optimisation.</para>
///
/// <para><b>Two oracles, deliberately.</b> The primary one is
/// <see cref="EcsWorld.GetEntitiesInRange(Vec2, float, Span{EntityView})"/>, the shipped
/// brute-force scan, which never consults the index. The second is
/// <see cref="AoiLogic.GetNearbyEntities(System.Collections.Generic.IReadOnlyList{EntityState}, in Vec2, float, Span{EntityState})"/>
/// from <c>Shared.GameLogic</c> — the definition of visibility the Unity client also
/// compiles. Checking against both is what rules out the two paths having drifted
/// together away from the semantics the client predicts with.</para>
/// </summary>
public class AoiIndexDifferentialTests
{
    private const float Radius = GameConstants.DefaultAoiRadius;

    /// <summary>
    /// Brute force: the public view overload, which runs the full scan and never consults
    /// the index (the index is only built inside a gather scope).
    /// </summary>
    private static List<EntityView> BruteForce(EcsWorld world, Vec2 center, float radius)
    {
        var buffer = new EntityView[16];
        int count = world.GetEntitiesInRange(center, radius, buffer);
        if (count > buffer.Length)
        {
            buffer = new EntityView[count];
            count = world.GetEntitiesInRange(center, radius, buffer);
        }

        return buffer.Take(count).ToList();
    }

    /// <summary>
    /// Indexed: through <see cref="EcsWorld.ReadAll"/>, which rebuilds the index and hands
    /// out a reader — the exact path the tick loop's gather phase takes.
    /// </summary>
    private static List<EntityView> Indexed(EcsWorld world, Vec2 center, float radius)
    {
        var result = new List<EntityView>();
        world.ReadAll(reader =>
        {
            var buffer = new EntityView[16];
            int count = reader.GetEntitiesInRange(center, radius, buffer);
            if (count > buffer.Length)
            {
                buffer = new EntityView[count];
                count = reader.GetEntitiesInRange(center, radius, buffer);
            }

            for (int i = 0; i < count; i++) result.Add(buffer[i]);
        });

        return result;
    }

    /// <summary>
    /// The <c>Shared.GameLogic</c> oracle — the visibility rule the Unity client compiles.
    /// Fed the same entities in the same chunk order the scan sees, so order is comparable
    /// too.
    /// </summary>
    private static List<string> SharedLogicOracle(EcsWorld world, Vec2 center, float radius)
    {
        List<EntityState> all = world.GetEntitiesInRange(center, float.MaxValue);
        var destination = new EntityState[all.Count];
        int count = AoiLogic.GetNearbyEntities(all, in center, radius, destination);
        return destination.Take(count).Select(e => e.Id).ToList();
    }

    private static void AssertIdentical(EcsWorld world, Vec2 center, float radius, string because)
    {
        List<EntityView> expected = BruteForce(world, center, radius);
        List<EntityView> actual = Indexed(world, center, radius);

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
            Assert.Equal(expected[i].Key, actual[i].Key);
            Assert.Equal(expected[i].Type, actual[i].Type);
            Assert.Equal(expected[i].Position.X, actual[i].Position.X);
            Assert.Equal(expected[i].Position.Y, actual[i].Position.Y);
            Assert.Equal(expected[i].Hp, actual[i].Hp);
            Assert.Equal(expected[i].MaxHp, actual[i].MaxHp);
            Assert.Equal(expected[i].Speed, actual[i].Speed);
        }

        // Third opinion: the shared rule the client predicts with. Only for finite radii —
        // the oracle helper gathers with float.MaxValue, which a non-finite radius makes
        // meaningless.
        if (float.IsFinite(radius))
        {
            Assert.Equal(SharedLogicOracle(world, center, radius), actual.Select(a => a.Id).ToList());
        }
    }

    // ── Randomised ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
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
    /// Randomised queries centred <b>on an entity</b> rather than at a random point. This
    /// is the real access pattern — every AOI query in the server is centred on a viewer —
    /// and it puts the centre exactly on a stored position, which is where a cell-boundary
    /// mistake is most likely to show.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void QueriesCentredOnEntities_MatchBruteForceExactly(int seed)
    {
        var rng = new Random(seed);
        using var world = new EcsWorld();

        var positions = new List<Vec2>();
        for (int i = 0; i < 120; i++)
        {
            var p = new Vec2(
                (float)(rng.NextDouble() * 300 - 150),
                (float)(rng.NextDouble() * 300 - 150));
            positions.Add(p);
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", p.X, p.Y));
        }

        foreach (Vec2 centre in positions)
        {
            AssertIdentical(world, centre, Radius, $"seed {seed}, centred on ({centre.X},{centre.Y})");
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
    /// that trims its candidate rectangle too tightly loses it. This pins the inclusive
    /// boundary specifically, independent of the differential comparison.
    /// </summary>
    [Fact]
    public void EntityExactlyOnTheRadiusBoundary_IsIncluded()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("on", Radius, 0));
        world.AddEntity(TestHelpers.CreatePlayer("justOutside", Radius + 0.001f, 0));

        AssertIdentical(world, new Vec2(0, 0), Radius, "radius boundary");

        List<EntityView> got = Indexed(world, new Vec2(0, 0), Radius);
        Assert.Contains(got, e => e.Id == "on");
        Assert.DoesNotContain(got, e => e.Id == "justOutside");
    }

    /// <summary>
    /// The boundary case in every direction, including the diagonals, where the covering
    /// cell rectangle is tightest relative to the circle.
    /// </summary>
    [Fact]
    public void BoundaryEntitiesInEveryDirection_AreIncluded()
    {
        using var world = new EcsWorld();

        for (int deg = 0; deg < 360; deg += 15)
        {
            double rad = deg * Math.PI / 180.0;
            // Placed at exactly the radius along each bearing. Float rounding may put an
            // individual one a hair either side; the differential assert is what matters,
            // because the index must agree with the scan whichever side it lands on.
            world.AddEntity(TestHelpers.CreatePlayer($"b{deg}",
                (float)(Math.Cos(rad) * Radius), (float)(Math.Sin(rad) * Radius)));
        }

        AssertIdentical(world, new Vec2(0, 0), Radius, "boundary ring");
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

    /// <summary>
    /// Wholly negative-quadrant population and negative-quadrant queries — the case where
    /// a sign mistake in the cell maths is uniform and therefore easy to miss.
    /// </summary>
    [Fact]
    public void WhollyNegativeQuadrant_MatchesBruteForce()
    {
        var rng = new Random(4242);
        using var world = new EcsWorld();

        for (int i = 0; i < 150; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"n{i}",
                (float)(-rng.NextDouble() * 400 - 1), (float)(-rng.NextDouble() * 400 - 1)));
        }

        for (int q = 0; q < 30; q++)
        {
            AssertIdentical(world,
                new Vec2((float)(-rng.NextDouble() * 400), (float)(-rng.NextDouble() * 400)),
                Radius, $"negative quadrant query {q}");
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
            AssertIdentical(world, new Vec2(k * Radius + 0.001f, 0), Radius, $"just above edge {k}");
        }
    }

    /// <summary>
    /// Many entities sharing one exact position. Every one of them is in or out together,
    /// and they all hash to one cell — the case that would expose a bucket that assumed
    /// distinct keys.
    /// </summary>
    [Fact]
    public void DuplicatePositions_MatchBruteForce()
    {
        using var world = new EcsWorld();

        for (int i = 0; i < 40; i++) world.AddEntity(TestHelpers.CreatePlayer($"dup{i}", 12.5f, -7.25f));
        for (int i = 0; i < 10; i++) world.AddEntity(TestHelpers.CreatePlayer($"far{i}", 900f + i, 900f));

        AssertIdentical(world, new Vec2(12.5f, -7.25f), Radius, "duplicates, centred on them");
        AssertIdentical(world, new Vec2(12.5f + Radius, -7.25f), Radius, "duplicates, on the boundary");
        AssertIdentical(world, new Vec2(500f, 500f), Radius, "duplicates, nowhere near");
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
    /// Entities parked far outside any populated region, with queries out there too. The
    /// index is hashed rather than indexed into a bounds-derived array precisely so these
    /// bucket correctly instead of clamping onto an edge cell.
    /// </summary>
    [Fact]
    public void FarOutOfBoundsEntities_MatchBruteForce()
    {
        using var world = new EcsWorld();

        world.AddEntity(TestHelpers.CreatePlayer("home", 0, 0));
        world.AddEntity(TestHelpers.CreatePlayer("far", 100_000f, -100_000f));
        world.AddEntity(TestHelpers.CreatePlayer("farNeighbour", 100_000f + Radius * 0.5f, -100_000f));

        AssertIdentical(world, new Vec2(0, 0), Radius, "at home");
        AssertIdentical(world, new Vec2(100_000f, -100_000f), Radius, "far away");

        List<EntityView> far = Indexed(world, new Vec2(100_000f, -100_000f), Radius);
        Assert.Equal(2, far.Count);
        Assert.DoesNotContain(far, e => e.Id == "home");
    }

    /// <summary>
    /// Everyone in one cell is the case where a grid cannot help — the answer genuinely is
    /// "everybody". It must still be exactly right.
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
    /// The overflow contract must survive the index: a short buffer still reports the total
    /// needed, and refilling at that size gives the whole set.
    ///
    /// <para>Stronger than the scan's version of this test, because the index discovers
    /// matches cell-major and has to sort them back: a truncating query must write the same
    /// <i>prefix</i> the scan would have written, not an arbitrary subset of the right
    /// size.</para>
    /// </summary>
    [Fact]
    public void OverflowContract_IsPreservedThroughTheIndex()
    {
        using var world = new EcsWorld();
        for (int i = 0; i < 12; i++) world.AddEntity(TestHelpers.CreatePlayer($"p{i}", i * 0.5f, 0));

        List<EntityView> full = BruteForce(world, new Vec2(0, 0), Radius);
        Assert.Equal(12, full.Count);

        world.ReadAll(reader =>
        {
            int needed = reader.GetEntitiesInRange(new Vec2(0, 0), Radius, Span<EntityView>.Empty);
            Assert.Equal(12, needed);

            var small = new EntityView[5];
            Assert.Equal(12, reader.GetEntitiesInRange(new Vec2(0, 0), Radius, small));
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(full[i].Id, small[i].Id);
            }

            var exact = new EntityView[12];
            Assert.Equal(12, reader.GetEntitiesInRange(new Vec2(0, 0), Radius, exact));
            for (int i = 0; i < 12; i++)
            {
                Assert.Equal(full[i].Id, exact[i].Id);
            }
        });
    }

    /// <summary>
    /// The parallel gather is the shape that actually runs at 200 viewers: one index built
    /// by the owner, many workers querying it at once. Shared query scratch would be a data
    /// race, so this runs every viewer's query on several workers and checks each result
    /// against the serial brute-force answer.
    /// </summary>
    [Fact]
    public void ParallelGather_MatchesBruteForceOnEveryWorker()
    {
        var rng = new Random(31337);
        using var world = new EcsWorld(4);

        var centres = new List<Vec2>();
        for (int i = 0; i < 200; i++)
        {
            var p = new Vec2(
                (float)(rng.NextDouble() * 350 - 175),
                (float)(rng.NextDouble() * 350 - 175));
            centres.Add(p);
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", p.X, p.Y));
        }

        var expected = centres.Select(c => BruteForce(world, c, Radius).Select(e => e.Id).ToList()).ToList();
        var actual = new List<string>[centres.Count];

        world.ReadAllParallel(4, (reader, worker) =>
        {
            for (int i = worker; i < centres.Count; i += 4)
            {
                var buffer = new EntityView[256];
                int count = reader.GetEntitiesInRange(centres[i], Radius, buffer);
                Assert.True(count <= buffer.Length, $"viewer {i} overflowed the test buffer");
                actual[i] = buffer.Take(count).Select(e => e.Id).ToList();
            }
        });

        for (int i = 0; i < centres.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    /// <summary>
    /// With the index disabled the gather must still be correct — this is the fallback the
    /// A/B benchmark's control arm uses, and the path any caller outside a gather scope
    /// takes.
    /// </summary>
    [Fact]
    public void WithIndexDisabled_GatherStillMatches()
    {
        var rng = new Random(5150);
        using var world = new EcsWorld { AoiIndexEnabled = false };

        for (int i = 0; i < 100; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}",
                (float)(rng.NextDouble() * 300 - 150), (float)(rng.NextDouble() * 300 - 150)));
        }

        for (int q = 0; q < 20; q++)
        {
            AssertIdentical(world, new Vec2(
                (float)(rng.NextDouble() * 300 - 150),
                (float)(rng.NextDouble() * 300 - 150)), Radius, $"index disabled, query {q}");
        }
    }
}
