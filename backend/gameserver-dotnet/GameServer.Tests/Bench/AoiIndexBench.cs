using System.Diagnostics;
using GameServer.World;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Bench;

/// <summary>
/// Paired in-process A/B for the AOI spatial index against the brute-force scan, over a
/// sweep of entity counts and densities.
///
/// <para><b>Why this file exists at all.</b> BENCHMARK.md Part V measured a uniform grid
/// at 0.32-0.45x (2.8x slower) at realistic density and reverted it, and recorded the one
/// condition that would bring the argument back: <i>a composition path the index can use
/// as cheaply as the scan does</i>. Issue #237 trimmed the gather's product to
/// <see cref="EntityView"/>, which is small enough to store in the index itself — so
/// composition moves from once-per-match-per-viewer to once-per-entity-per-tick. This
/// bench is what decides whether that actually pays, rather than whether it sounds like it
/// should.</para>
///
/// <para><b>The other reason this file exists.</b> Part V's harness was never committed,
/// which made its absolute microseconds the one figure class in BENCHMARK.md that the
/// #153 clock audit could not trace. This one is committed, states its clock
/// (<see cref="Stopwatch"/>, never <c>DateTime</c>), and is re-runnable.</para>
///
/// <para><b>Discipline</b> (BENCHMARK.md Part V, §8): this box is shared and absolute
/// timings swing ±50%, so both arms run back to back inside every round and only the
/// within-run ratio is quotable. Not a test — no timing assertion, the output is the
/// deliverable — and skipped unless <c>BENCH_AOI=1</c>:
/// <code>BENCH_AOI=1 dotnet test --filter FullyQualifiedName~AoiIndexBench \
///   --logger "console;verbosity=detailed"</code></para>
///
/// <para><b>What each arm measures.</b> A full gather pass: every viewer's AOI query for
/// one tick, through <see cref="EcsWorld.ReadAll"/> — the path the tick loop takes. The
/// indexed arm's rebuild is <b>inside</b> the measured region, because a per-tick rebuild
/// is a real per-tick cost and an index that only looks good with its build time excluded
/// is not an index, it is an accounting error. Correctness of the two arms against each
/// other is not asserted here; that is
/// <c>AoiIndexDifferentialTests</c>'s job, and it runs unconditionally.</para>
/// </summary>
public sealed class AoiIndexBench
{
    private readonly ITestOutputHelper _out;

    public AoiIndexBench(ITestOutputHelper output) => _out = output;

    private const string EnvVar = "BENCH_AOI";
    private const float Radius = GameConstants.DefaultAoiRadius;
    private const int Rounds = 120;
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

    /// <summary>One measured configuration: how many entities, spread over what square.</summary>
    /// <param name="Disc">When &gt; 0, place on a disc of this radius instead of a square
    /// of side <paramref name="Spread"/> — the placement AoiComposeBench and
    /// TickBreakdownBench use, so one row here is directly comparable to the house
    /// "realistic" density rather than only to Part V's.</param>
    private readonly record struct Cell(int Entities, float Spread, string Label, float Disc = 0f);

    /// <summary>
    /// The sweep. The first three reproduce Part V's rows so the new numbers are directly
    /// comparable to the ones that killed the first index; the rest extend the entity count
    /// past what was measured then, which is where an O(n^2) scan is supposed to hurt.
    /// </summary>
    private static readonly Cell[] Cells =
    [
        new(200, 1000f, "sparse (Part V row 1)"),
        new(200, 250f, "realistic (Part V row 2)"),
        new(400, 250f, "dense (Part V row 3)"),
        new(400, 500f, "realistic, 400"),
        new(800, 700f, "realistic, 800"),
        new(1600, 1000f, "realistic, 1600"),

        // The two rows that answer "does this help the server as it runs today?".
        // The stock map is 1000x1000 (GameConstants.DefaultMapWidth/Height) with an AOI
        // radius of 50, so a full map of players spread over it is the first of these.
        // The second is the in-house "realistic" density every other bench in this folder
        // uses — a disc of radius 175 — which is a far tighter clump than the map allows.
        new(200, 1000f, "STOCK MAP 1000x1000, 200"),
        new(200, 0f, "house realistic (disc 175)", 175f),
        new(400, 0f, "house realistic (disc 175), 400", 175f),
    ];

    [SkippableFact]
    public void Bench_SpatialIndexVsBruteForce()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run this benchmark.");

        Line($"clock: Stopwatch, frequency {Stopwatch.Frequency} Hz, high-resolution {Stopwatch.IsHighResolution}");
        Line($"radius {Radius}, {Rounds} rounds after {Warmup} warmup, arms interleaved per round");
        Line($"{Repetitions} independent repetitions per cell; per Part V only the within-run ratios are quotable.");
        Line("");
        Line($"{"cell",-26} {"n",6} {"spread",7} {"avg/query",10} {"cells",7} {"brute us",10} {"indexed us",11} {"ratios",22}");

        foreach (Cell cell in Cells)
        {
            RunCell(cell);
        }

        Line("");
        Line("calibration: ratio against grid occupancy, sweeping spread at fixed n.");
        Line("The gate has to be driven by a statistic the rebuild already computes, and");
        Line("occupied-cell count is the one that separates the winning rows from the");
        Line("losing ones - entity count alone does not (dense 400 loses, sparse 200 wins).");
        Line("");
        Line($"{"cell",-26} {"n",6} {"spread",7} {"avg/query",10} {"cells",7} {"brute us",10} {"indexed us",11} {"ratios",22}");

        foreach (int n in new[] { 200, 400 })
        {
            foreach (float spread in new[] { 200f, 300f, 400f, 500f, 700f, 1000f })
            {
                RunCell(new Cell(n, spread, $"calib n={n} spread={spread:F0}"));
            }
        }
    }

    private void RunCell(Cell cell)
    {
        // Same population and the same viewer set for both arms — two worlds so the index
        // flag differs, built from one seeded layout so the geometry is identical.
        var rng = new Random(20260909 + cell.Entities);
        var positions = new Vec2[cell.Entities];
        for (int i = 0; i < cell.Entities; i++)
        {
            if (cell.Disc > 0f)
            {
                double angle = rng.NextDouble() * Math.PI * 2;
                double r = cell.Disc * Math.Sqrt(rng.NextDouble());
                positions[i] = new Vec2((float)(Math.Cos(angle) * r), (float)(Math.Sin(angle) * r));
            }
            else
            {
                positions[i] = new Vec2(
                    (float)(rng.NextDouble() * cell.Spread - cell.Spread / 2),
                    (float)(rng.NextDouble() * cell.Spread - cell.Spread / 2));
            }
        }

        using var brute = new EcsWorld { AoiIndexEnabled = false };
        using var indexed = new EcsWorld { AoiIndexEnabled = true };
        for (int i = 0; i < cell.Entities; i++)
        {
            brute.AddEntity(TestHelpers.CreatePlayer($"p{i}", positions[i].X, positions[i].Y));
            indexed.AddEntity(TestHelpers.CreatePlayer($"p{i}", positions[i].X, positions[i].Y));
        }

        // Every entity is a viewer, which is the shape of a full player map. The buffer is
        // sized so no query ever takes the resize-and-retry path in either arm; a retry in
        // one arm only would measure the retry, not the index.
        var buffer = new EntityView[cell.Entities];

        long totalMatches = 0;
        int totalQueries = 0;
        brute.ReadAll(reader =>
        {
            for (int i = 0; i < cell.Entities; i++)
            {
                totalMatches += reader.GetEntitiesInRange(positions[i], Radius, buffer);
                totalQueries++;
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
            // Arm order alternates. With a fixed order the second arm reads consistently
            // faster on rows where both arms take the *same* code path — an ordering bias
            // of ~5% that would otherwise be indistinguishable from a small real win, and
            // 5% is the size of several results here.
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
                for (int i = 0; i < cell.Entities; i++)
                {
                    reader.GetEntitiesInRange(positions[i], Radius, buffer);
                }
            });
        }

        lastBrute = Median(bruteSamples);
        lastIndexed = Median(indexedSamples);
        ratios.Add(lastBrute / lastIndexed);
        }

        double avgMatches = (double)totalMatches / totalQueries;
        int cells = indexed.AoiIndexOccupiedCells;
        string spread2 = string.Join(" ", ratios.Select(r => $"{r:F2}x"));

        Line($"{cell.Label,-26} {cell.Entities,6} {cell.Spread,7:F0} {avgMatches,10:F1} " +
             $"{cells,7} {lastBrute,10:F0} {lastIndexed,11:F0} {spread2,22}");
    }
}
