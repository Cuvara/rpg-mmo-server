using GameServer.Scaffolding;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;
using Xunit.Abstractions;

namespace GameServer.Tests.Scaffolding;

/// <summary>
/// Review finding R1-1: the separation grid's work bound counted only entries in the queried
/// cell, so hash-chain entries from colliding cells were stepped over for free. Enemies spread
/// over many distinct cells that hash into one bucket made every query walk the whole chain,
/// which is quadratic in total despite the documented O(n x K). <see cref="EnemySeparationSystem.MaxVisited"/>
/// now bounds every chain step. These tests pin that the bound holds under an adversarial
/// layout, and that it never changes the answer for an ordinary crowd.
/// </summary>
public class EnemySeparationVisitBudgetTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 15f;
    private const float Radius = 0.8f;
    private const float MaxStep = 2.5f * Dt;

    private static EnemyAiSettings Settings()
    {
        var env = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvAttacksEnabled] = "off",
            [EnemyAiSettings.EnvSeed] = "11",
        };
        Assert.True(EnemyAiSettings.TryCreate(k => env.GetValueOrDefault(k), MapBounds.Default, out var s, out var err), err);
        return s!;
    }

    /// <summary>
    /// One enemy at cell (0, 0) and <paramref name="others"/> enemies, each alone in its own cell
    /// far away, every one of those cells hashing into the bucket of cell (0, 0) for the table
    /// size the system will pick. No two of them are within the radius, so nothing should push.
    /// </summary>
    private static Vec2[] CollidingLayout(int others)
    {
        int n = others + 1;
        int tableSize = 1;
        while (tableSize < n * 2) tableSize <<= 1;
        int mask = tableSize - 1;
        int bucket = EnemySeparationSystem.Hash(0, 0) & mask;

        var points = new List<Vec2> { new(0.5f * Radius, 0.5f * Radius) };
        // Far from (0, 0), and every chosen cell at least 2 rows apart so no two are neighbours.
        for (int cy = 10; points.Count < n; cy += 2)
        {
            for (int cx = 10; cx < 10 + tableSize && points.Count < n; cx++)
            {
                if ((EnemySeparationSystem.Hash(cx, cy) & mask) != bucket) continue;
                points.Add(new Vec2((cx + 0.5f) * Radius, (cy + 0.5f) * Radius));
                break; // one per row keeps the cells far apart in x as well
            }
        }

        return points.ToArray();
    }

    [Fact]
    public void CollidingCells_DoNotMakeAQueryWalkTheWholeChain()
    {
        Vec2[] points = CollidingLayout(2000);
        float inv = 1f / Radius;
        int mask = 4096 - 1;
        int bucket = EnemySeparationSystem.Hash(0, 0) & mask;
        foreach (Vec2 p in points)
        {
            Assert.Equal(bucket, EnemySeparationSystem.Hash(EnemySeparationSystem.Cell(p.X, inv), EnemySeparationSystem.Cell(p.Y, inv)) & mask);
        }

        // Unbounded: the pre-fix behaviour. Every query walks all 2001 entries of the bucket.
        var unbounded = new EnemySeparationSystem(Dt, Settings()) { VisitBudget = int.MaxValue };
        unbounded.SetSnapshot(points);
        unbounded.ComputePushes(points.Length, Radius, MaxStep);

        var bounded = new EnemySeparationSystem(Dt, Settings());
        bounded.SetSnapshot(points);
        bounded.ComputePushes(points.Length, Radius, MaxStep);

        output.WriteLine($"max chain entries per query: unbounded {unbounded.LastMaxVisited}, bounded {bounded.LastMaxVisited}");
        Assert.True(unbounded.LastMaxVisited >= points.Length,
            $"the layout did not collide: an unbounded query visited only {unbounded.LastMaxVisited} entries");
        Assert.True(bounded.LastMaxVisited <= EnemySeparationSystem.MaxVisited,
            $"a query stepped over {bounded.LastMaxVisited} chain entries, above the budget of {EnemySeparationSystem.MaxVisited}");

        // No real neighbours, so no pushes either way.
        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(new Vec2(0f, 0f), bounded.PushOf(i));
            Assert.Equal(new Vec2(0f, 0f), unbounded.PushOf(i));
        }

        // Conservative where the budget bites: the free-spot query says "taken" rather than
        // claim a spot it did not finish checking. The unbounded scan finds it free.
        Vec2 probe = new(0.5f * Radius + 0.1f, 0.5f * Radius);
        Assert.False(bounded.IsFree(probe.X, probe.Y, points[0].X, points[0].Y));
        Assert.True(unbounded.IsFree(probe.X, probe.Y, points[0].X, points[0].Y));
    }

    public static IEnumerable<object[]> OrdinaryCrowds()
    {
        // (count, disc radius around the target): a sparse field, a chasing crowd at the
        // densities the testlab sees, the 300-enemy budget cap at two densities, heavy piles, a
        // coincident pile, and a large field. Deterministic (a Vogel spiral, no RNG), so the
        // worst chain walks are known: an offline model of the walk gives at most 58 entries
        // per push and 92 per query over these, against the budget of 256.
        yield return new object[] { 50, 20f };
        yield return new object[] { 165, 6f };
        yield return new object[] { 300, 4f };
        yield return new object[] { 300, 1f };
        yield return new object[] { 2000, 3f };
        yield return new object[] { 2000, 0f };   // coincident pile
        yield return new object[] { 2000, 8f };
        yield return new object[] { 5000, 10f };
    }

    /// <summary>
    /// The budget is a guard, not a policy: on every ordinary crowd the pushes and the queue and
    /// free-spot answers are bit-identical with the budget and without it.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrdinaryCrowds))]
    public void OrdinaryCrowds_AreBitIdenticalWithAndWithoutTheBudget(int count, float spread)
    {
        var points = new Vec2[count];
        for (int i = 0; i < count; i++)
        {
            double r = spread * Math.Sqrt((i + 0.5) / count);
            double a = i * 2.399963229728653; // golden angle
            points[i] = new Vec2((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)));
        }

        var unbounded = new EnemySeparationSystem(Dt, Settings()) { VisitBudget = int.MaxValue };
        unbounded.SetSnapshot(points);
        unbounded.ComputePushes(count, Radius, MaxStep);
        var bounded = new EnemySeparationSystem(Dt, Settings());
        bounded.SetSnapshot(points);
        bounded.ComputePushes(count, Radius, MaxStep);

        output.WriteLine($"n={count} spread={spread}: max chain entries per query {unbounded.LastMaxVisited}");
        Assert.True(unbounded.LastMaxVisited < EnemySeparationSystem.MaxVisited,
            $"an ordinary crowd reached the visit budget ({unbounded.LastMaxVisited}); the budget would decide something");
        for (int i = 0; i < count; i++) Assert.Equal(unbounded.PushOf(i), bounded.PushOf(i));

        float span = spread + 2f;
        for (int q = 0; q < 225; q++)
        {
            float x = -span + (2f * span * (q / 15) / 14f);
            float y = -span + (2f * span * (q % 15) / 14f);
            Assert.Equal(unbounded.IsFree(x, y, 99f, 99f), bounded.IsFree(x, y, 99f, 99f));
            Assert.Equal(
                unbounded.IsBlockedAhead(x, y, 0f, 0f, 2.5f * Dt, out float s1),
                bounded.IsBlockedAhead(x, y, 0f, 0f, 2.5f * Dt, out float s2));
            Assert.Equal(s1, s2);
        }
    }
}
