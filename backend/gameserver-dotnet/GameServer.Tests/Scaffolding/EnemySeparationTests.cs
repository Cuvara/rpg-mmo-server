using System.Diagnostics;
using GameServer.Scaffolding;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace GameServer.Tests.Scaffolding;

/// <summary>
/// rpg-mmo-server#469: enemies chasing one player stacked on a single point (the testlab measured
/// a median nearest-neighbour distance of 0.002-0.022 units and 100 % of enemies overlapping).
/// <see cref="EnemySeparationSystem"/> spreads them; these pin that it does, that it is bounded,
/// deterministic and never moves an enemy faster than it walks, and that pursuit stays stable.
/// </summary>
public class EnemySeparationTests
{
    private const int TickRate = 15;
    private const float Dt = 1f / TickRate;

    private static EnemyAiSettings Settings(params (string Name, string Value)[] vars)
    {
        var env = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvAttacksEnabled] = "off",
            [EnemyAiSettings.EnvSeed] = "11",
        };
        foreach (var (n, v) in vars) env[n] = v;
        Assert.True(EnemyAiSettings.TryCreate(k => env.GetValueOrDefault(k), MapBounds.Default, out var s, out var err), err);
        return s!;
    }

    private static List<Vec2> Enemies(EcsWorld world) =>
        world.GetEntitiesInRange(new Vec2(0f, 0f), 1e6f)
            .Where(e => e.Type == "mob" && !e.Dead)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => e.Position)
            .ToList();

    private static (double OverlapRatio, double MedianNn) Crowd(List<Vec2> p, float overlap = 0.3f)
    {
        var nn = new List<double>();
        int overlapped = 0;
        for (int i = 0; i < p.Count; i++)
        {
            double best = double.MaxValue;
            for (int j = 0; j < p.Count; j++)
            {
                if (i == j) continue;
                double d = Math.Sqrt(((p[i].X - p[j].X) * (p[i].X - p[j].X)) + ((p[i].Y - p[j].Y) * (p[i].Y - p[j].Y)));
                best = Math.Min(best, d);
            }

            nn.Add(best);
            if (best < overlap) overlapped++;
        }

        nn.Sort();
        return ((double)overlapped / p.Count, nn[nn.Count / 2]);
    }

    /// <summary>A pile on one exact point spreads to the configured spacing, each step capped.</summary>
    [Fact]
    public void ACoincidentPile_SpreadsToTheRadius_WithEveryStepCapped()
    {
        var system = new EnemySeparationSystem(Dt, Settings());
        var points = Enumerable.Repeat(new Vec2(5f, 5f), 40).ToArray();
        float maxStep = 2.5f * Dt;

        for (int step = 0; step < 300; step++)
        {
            system.SetSnapshot(points);
            system.ComputePushes(points.Length, 0.8f, maxStep);
            for (int i = 0; i < points.Length; i++)
            {
                Vec2 push = system.PushOf(i);
                Assert.True(MathF.Sqrt((push.X * push.X) + (push.Y * push.Y)) <= maxStep + 1e-5f,
                    "a separation push exceeded the walking distance of one step");
                points[i] = new Vec2(points[i].X + push.X, points[i].Y + push.Y);
            }
        }

        var (overlap, nn) = Crowd(points.ToList());
        Assert.True(overlap < 0.05, $"{overlap:P0} still overlapping after spreading");
        Assert.True(nn > 0.6, $"median nearest neighbour {nn:F3} < 0.6 of a 0.8 spacing");
    }

    /// <summary>
    /// Through the real schedule, enemies chasing three players who stand together: with
    /// separation the crowd keeps its spacing; the control arm (separation 0) is the pile the
    /// testlab measured, so the test can fail.
    /// </summary>
    [Fact]
    public void AChasingCrowd_KeepsItsSpacing_AndTheControlArmStillPiles()
    {
        (double overlap, double nn) Run(string separation)
        {
            using var world = new EcsWorld();
            for (int i = 0; i < 3; i++) world.AddEntity(TestHelpers.CreatePlayer($"p{i}", i * 0.5f, 0f));
            var ai = new EnemySpawner(world, TickRate, Settings((EnemyAiSettings.EnvSeparation, separation)), NullLogger.Instance);
            for (ulong t = 1; t <= 15 * 60; t++) ai.Tick(t);
            return Crowd(Enemies(world));
        }

        var on = Run("0.8");
        var off = Run("0");
        Assert.True(off.overlap > 0.8, $"control arm overlap {off.overlap:P0}: the pile this fixes did not form");
        Assert.True(on.overlap < 0.1, $"with separation {on.overlap:P0} of enemies still overlap (control {off.overlap:P0})");
        Assert.True(on.nn > 0.5, $"median nearest neighbour {on.nn:F3}");
    }

    /// <summary>Settled around a standing player, the crowd stops moving: no visible jitter.</summary>
    [Fact]
    public void ASettledCrowd_IsStable()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p0", 0f, 0f));
        var ai = new EnemySpawner(world, TickRate, Settings(), NullLogger.Instance);
        ulong t = 1;
        for (; t <= 15 * 60; t++) ai.Tick(t);

        List<Vec2> before = Enemies(world);
        for (int i = 0; i < 15; i++) ai.Tick(t++);
        List<Vec2> after = Enemies(world);
        Assert.Equal(before.Count, after.Count);

        double mean = 0;
        for (int i = 0; i < before.Count; i++)
        {
            mean += Math.Sqrt(((after[i].X - before[i].X) * (after[i].X - before[i].X)) + ((after[i].Y - before[i].Y) * (after[i].Y - before[i].Y)));
        }

        mean /= before.Count;
        Assert.True(mean < 0.05, $"settled crowd still drifts {mean:F3} units per second on average");
    }

    [Fact]
    public void SameSeed_SameCrowd()
    {
        List<Vec2> Run()
        {
            using var world = new EcsWorld();
            for (int i = 0; i < 2; i++) world.AddEntity(TestHelpers.CreatePlayer($"p{i}", i * 6f, 0f));
            var ai = new EnemySpawner(world, TickRate, Settings(), NullLogger.Instance);
            for (ulong t = 1; t <= 15 * 30; t++) ai.Tick(t);
            return Enemies(world);
        }

        Assert.Equal(Run(), Run());
    }

    /// <summary>
    /// Bounded work: 20,000 enemies on one point would be 4x10^8 pair tests unbounded; with the
    /// neighbour cap it is a few hundred thousand. The generous time bound only catches the
    /// quadratic case, it is not a benchmark.
    /// </summary>
    [Fact]
    public void TheWorstCasePile_IsLinear()
    {
        var system = new EnemySeparationSystem(Dt, Settings());
        var points = Enumerable.Repeat(new Vec2(0f, 0f), 20_000).ToArray();
        system.SetSnapshot(points);

        var sw = Stopwatch.StartNew();
        system.ComputePushes(points.Length, 0.8f, 2.5f * Dt);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms for 20,000 coincident enemies");
    }

    /// <summary>
    /// The side-step's free-spot query is bounded by the neighbour cap, so where the cap bites it
    /// must say "taken": an enemy it did not examine may stand on the spot. Thirteen enemies
    /// ringing a point at 1.0 leave the point itself clear but exhaust the cap; a single enemy
    /// at the same distance does not.
    /// </summary>
    [Fact]
    public void FreeSpotQuery_IsConservativeWhereTheNeighbourCapBites()
    {
        var system = new EnemySeparationSystem(Dt, Settings());
        var centre = new Vec2(0.4f, 0.4f); // middle of a 0.8 cell

        Vec2[] ring = Enumerable.Range(0, 13)
            .Select(k => new Vec2(centre.X + MathF.Cos(k * MathF.Tau / 13f), centre.Y + MathF.Sin(k * MathF.Tau / 13f)))
            .ToArray();
        system.SetSnapshot(ring);
        system.ComputePushes(ring.Length, 0.8f, 2.5f * Dt);
        Assert.False(system.IsFree(centre.X, centre.Y, 99f, 99f));

        Vec2[] two = { ring[0], new Vec2(50f, 50f) };
        system.SetSnapshot(two);
        system.ComputePushes(two.Length, 0.8f, 2.5f * Dt);
        Assert.True(system.IsFree(centre.X, centre.Y, 99f, 99f));
    }

    [Fact]
    public void SeparationZero_TouchesNothing()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p0", 0f, 0f));
        var settings = Settings((EnemyAiSettings.EnvSeparation, "0"));
        var system = new EnemySeparationSystem(Dt, settings);
        var ai = new EnemySpawner(world, TickRate, settings, NullLogger.Instance);
        for (ulong t = 1; t <= 60; t++) ai.Tick(t);
        world.UpdateComponents(w => system.Run(w, 61));
        Assert.Equal(0, system.LastPushed);
    }
}
