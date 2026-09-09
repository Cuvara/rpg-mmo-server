using System.Diagnostics;
using GameServer.World;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Bench;

/// <summary>
/// Does the AOI gate make the right call on populations shaped like a game rather than a
/// scatter plot?
///
/// <para><b>The question.</b> <c>SpatialGrid.MinOccupiedCellsToQuery = 96</c> was
/// calibrated in <c>AoiIndexBench</c> on <b>uniform random</b> layouts, and BENCHMARK.md
/// Part X says so. Players are not uniform: they pile onto spawn points, quest objectives,
/// boss doors and towns. Occupied-cell count is the gate's only input and clustering is
/// precisely what moves it, so a threshold tuned on uniform layouts could be systematically
/// wrong in the one regime that actually occurs.</para>
///
/// <para><b>Why occupancy is suspect a priori.</b> A uniform world examines
/// <c>9 / OccupiedCells</c> of itself per query, which is what makes occupancy a valid
/// proxy there. Clustering breaks that identity in both directions at once: <i>k</i> tight
/// hotspots occupy few cells — so occupancy reads "not worth indexing" — while a viewer
/// inside a hotspot still only examines its own hotspot, so the real saving is roughly
/// <i>k</i>-fold. If that is what happens, the gate is not mis-tuned, it is reading the
/// wrong quantity. This bench measures a candidate replacement alongside it:
/// <see cref="EcsWorld.AoiIndexCandidateFraction"/>, the mean fraction of the population a
/// query must examine, estimated from the cell histogram.</para>
///
/// <para><b>Method.</b> Same discipline as <c>AoiIndexBench</c> (Part V, §8): both arms run
/// a full gather back to back inside every round with <b>alternating arm order</b>, the
/// indexed arm's rebuild is inside the measured region, <see cref="Stopwatch"/> only, and
/// only within-run ratios are quotable. The indexed arm runs with the gate <b>forced
/// open</b> (<c>AoiIndexGateThreshold = 0</c>), because the whole point is to see what the
/// shipped gate is giving up on layouts it currently refuses.</para>
///
/// <code>BENCH_AOI=1 dotnet test --filter FullyQualifiedName~AoiClusteredGateBench \
///   --logger "console;verbosity=detailed"</code>
/// </summary>
public sealed class AoiClusteredGateBench
{
    private readonly ITestOutputHelper _out;

    public AoiClusteredGateBench(ITestOutputHelper output) => _out = output;

    private const string EnvVar = "BENCH_AOI";
    private const float Radius = GameConstants.DefaultAoiRadius;
    private const float MapSize = GameConstants.DefaultMapWidth; // 1000, the stock map
    private const int Rounds = 100;
    private const int Warmup = 20;
    private const int Repetitions = 3;

    private void Line(string s)
    {
        _out.WriteLine(s);
        Console.WriteLine(s);
    }

    private static double Us(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    private static double Median(List<long> samples)
    {
        samples.Sort();
        return Us(samples[samples.Count / 2]);
    }

    /// <summary>
    /// A population shaped like play: <paramref name="Hotspots"/> gathering points holding
    /// <paramref name="HotFraction"/> of the players, Gaussian-scattered with standard
    /// deviation <paramref name="Sigma"/>, and the remainder roaming the map uniformly.
    /// </summary>
    /// <remarks>
    /// The shape is chosen to resemble an actual map rather than to flatter either arm.
    /// A town, a boss door and a couple of quest objectives is a handful of hotspots, not
    /// dozens; most players present are at one of them and a minority are travelling. Sigma
    /// is quoted against the 50-unit AOI radius: sigma 25 is a crowd tighter than one AOI,
    /// sigma 100 is a loose gathering several AOIs across.
    /// </remarks>
    /// <param name="Spread">Side of the square the uniform component is scattered over.
    /// 0 means the full map. Lets uniform layouts be measured in <b>this</b> harness at
    /// matched statistics, rather than compared across harnesses to Part X.</param>
    private readonly record struct Layout(
        string Label, int Entities, int Hotspots, float Sigma, double HotFraction,
        float Spread = 0f);

    private static readonly Layout[] Layouts =
    [
        // The case the gate is most likely to get wrong: few, very tight crowds with empty
        // space between them. Low occupancy, but a viewer in a crowd sees only that crowd.
        new("2 tight crowds", 400, 2, 25f, 0.95),
        new("2 tight crowds, n=800", 800, 2, 25f, 0.95),
        new("4 tight crowds", 400, 4, 25f, 0.95),
        new("8 tight crowds", 400, 8, 25f, 0.95),
        new("16 tight crowds", 400, 16, 25f, 0.95),

        // Looser gatherings — a town square rather than a boss door.
        new("4 loose crowds", 400, 4, 50f, 0.90),
        new("8 loose crowds", 400, 8, 50f, 0.90),
        new("8 very loose crowds", 400, 8, 100f, 0.90),

        // A realistic mix: some players at objectives, a substantial roaming tail.
        new("4 crowds + 30% roaming", 400, 4, 40f, 0.70),
        new("8 crowds + 30% roaming", 400, 8, 40f, 0.70),
        new("8 crowds + 50% roaming", 400, 8, 40f, 0.50),

        // Uniform layouts across the same span of densities, measured HERE so the
        // clustered rows can be compared against them without crossing harnesses.
        new("uniform, spread 250", 400, 0, 0f, 0.0, 250f),
        new("uniform, spread 350", 400, 0, 0f, 0.0, 350f),
        new("uniform, spread 500", 400, 0, 0f, 0.0, 500f),
        new("uniform, spread 700", 400, 0, 0f, 0.0, 700f),
        new("uniform, full map", 400, 0, 0f, 0.0, 1000f),
        new("uniform, full map, n=200", 200, 0, 0f, 0.0, 1000f),
        new("uniform, full map, n=800", 800, 0, 0f, 0.0, 1000f),
    ];

    [SkippableFact]
    public void Bench_ClusteredLayouts_AgainstTheOccupancyGate()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run this benchmark.");

        Line($"clock: Stopwatch, frequency {Stopwatch.Frequency} Hz, high-resolution {Stopwatch.IsHighResolution}");
        Line($"map {MapSize}x{MapSize}, AOI radius {Radius}, cell size {Radius}");
        Line($"{Rounds} rounds after {Warmup} warmup, arm order alternating, {Repetitions} repetitions");
        Line($"indexed arm runs with the gate FORCED OPEN, to show what the shipped gate gives up");
        Line($"shipped gate: index used when occupied cells >= {SpatialGrid.MinOccupiedCellsToQuery}");
        Line("");
        Line($"{"layout",-28} {"n",5} {"cells",6} {"gate",5} {"match/q",8} {"cand.frac",10} {"brute us",9} {"idx us",8} {"ratios",20}");

        SpatialGrid.SortStructsInsteadOfPermutation = false;
        foreach (Layout layout in Layouts)
        {
            RunLayout(layout);
        }

        Line("");
        Line("Same layouts, but the index sorts EntityView structs instead of an int");
        Line("permutation — the ordering strategy as it stood when Part X was measured.");
        Line("The difference between the two tables is the cost of moving wide structs");
        Line("through the sort, which is a per-match cost the brute-force scan never pays.");
        Line("");
        Line($"{"layout",-28} {"n",5} {"cells",6} {"gate",5} {"match/q",8} {"cand.frac",10} {"brute us",9} {"idx us",8} {"ratios",20}");
        SpatialGrid.SortStructsInsteadOfPermutation = true;
        foreach (Layout layout in Layouts)
        {
            RunLayout(layout);
        }
        SpatialGrid.SortStructsInsteadOfPermutation = false;

        Line("");
        Line("gate   = what the SHIPPED occupancy gate decides (ON/OFF) for this layout");
        Line("match/q= mean entities in view per query");
        Line("cand.frac = mean fraction of the population a query must examine, from the cell");
        Line("           histogram — the candidate replacement statistic. Low is good for the index.");
        Line("ratios = brute / indexed, index forced on. >1 means the index wins.");
        Line("A row where gate=OFF and ratio>1 is the gate refusing a win it should take.");
    }

    private void RunLayout(Layout layout)
    {
        var rng = new Random(20260910 + layout.Entities * 31 + layout.Hotspots);
        var positions = new Vec2[layout.Entities];

        var centres = new Vec2[Math.Max(layout.Hotspots, 1)];
        for (int h = 0; h < layout.Hotspots; h++)
        {
            centres[h] = new Vec2(
                (float)(rng.NextDouble() * MapSize - MapSize / 2),
                (float)(rng.NextDouble() * MapSize - MapSize / 2));
        }

        for (int i = 0; i < layout.Entities; i++)
        {
            if (layout.Hotspots > 0 && rng.NextDouble() < layout.HotFraction)
            {
                Vec2 c = centres[rng.Next(layout.Hotspots)];
                positions[i] = new Vec2(
                    c.X + (float)Gaussian(rng) * layout.Sigma,
                    c.Y + (float)Gaussian(rng) * layout.Sigma);
            }
            else
            {
                float span = layout.Spread > 0f ? layout.Spread : MapSize;
                positions[i] = new Vec2(
                    (float)(rng.NextDouble() * span - span / 2),
                    (float)(rng.NextDouble() * span - span / 2));
            }
        }

        using var brute = new EcsWorld { AoiIndexEnabled = false };
        using var indexed = new EcsWorld { AoiIndexEnabled = true, AoiIndexGateThreshold = 0 };
        for (int i = 0; i < layout.Entities; i++)
        {
            brute.AddEntity(TestHelpers.CreatePlayer($"p{i}", positions[i].X, positions[i].Y));
            indexed.AddEntity(TestHelpers.CreatePlayer($"p{i}", positions[i].X, positions[i].Y));
        }

        var buffer = new EntityView[layout.Entities];

        long totalMatches = 0;
        brute.ReadAll(reader =>
        {
            for (int i = 0; i < layout.Entities; i++)
            {
                totalMatches += reader.GetEntitiesInRange(positions[i], Radius, buffer);
            }
        });

        var ratios = new List<double>(Repetitions);
        double lastBrute = 0, lastIndexed = 0;

        for (int rep = 0; rep < Repetitions; rep++)
        {
            var bruteSamples = new List<long>(Rounds);
            var indexedSamples = new List<long>(Rounds);

            for (int round = 0; round < Rounds + Warmup; round++)
            {
                // Alternating, for the ~5% ordering bias Part X records.
                bool bruteFirst = (round & 1) == 0;

                long t0 = Stopwatch.GetTimestamp();
                Gather(bruteFirst ? brute : indexed);
                long t1 = Stopwatch.GetTimestamp();
                Gather(bruteFirst ? indexed : brute);
                long t2 = Stopwatch.GetTimestamp();

                if (round < Warmup) continue;
                bruteSamples.Add(bruteFirst ? t1 - t0 : t2 - t1);
                indexedSamples.Add(bruteFirst ? t2 - t1 : t1 - t0);

                void Gather(EcsWorld w) => w.ReadAll(reader =>
                {
                    for (int i = 0; i < layout.Entities; i++)
                    {
                        reader.GetEntitiesInRange(positions[i], Radius, buffer);
                    }
                });
            }

            lastBrute = Median(bruteSamples);
            lastIndexed = Median(indexedSamples);
            ratios.Add(lastBrute / lastIndexed);
        }

        int cells = indexed.AoiIndexOccupiedCells;
        double candFrac = indexed.AoiIndexCandidateFraction;
        string gate = cells >= SpatialGrid.MinOccupiedCellsToQuery ? "ON" : "OFF";
        double avgMatches = (double)totalMatches / layout.Entities;
        string spread = string.Join(" ", ratios.Select(r => $"{r:F2}x"));

        Line($"{layout.Label,-28} {layout.Entities,5} {cells,6} {gate,5} {avgMatches,8:F1} " +
             $"{candFrac,10:F4} {lastBrute,9:F0} {lastIndexed,8:F0} {spread,20}");
    }

    /// <summary>Standard normal via Box-Muller. Clusters are Gaussian, not uniform discs.</summary>
    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
