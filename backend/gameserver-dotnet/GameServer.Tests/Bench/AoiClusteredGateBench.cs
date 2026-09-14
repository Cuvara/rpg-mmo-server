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
        Line($"{"layout",-26} {"n",5} {"cells",5} {"gate",4} {"match/q",7} {"cand.fr",8} {"brute",7} {"perm",7} {"struct",7} {"perm ratios",17} {"struct ratios",17}");

        foreach (Layout layout in Layouts)
        {
            RunLayout(layout);
        }

        Line("");
        Line("gate   = what the SHIPPED occupancy gate decides (ON/OFF) for this layout");
        Line("match/q= mean entities in view per query");
        Line("cand.frac = mean fraction of the population a query must examine, from the cell");
        Line("           histogram — the candidate replacement statistic. Low is good for the index.");
        Line("perm   = the shipped index (sorts an int permutation)");
        Line("struct = verbatim replica of the pre-fix index (sorts EntityView structs)");
        Line("ratios = brute / index, index forced on. >1 means the index wins.");
        Line("A gate=ON row whose ratio is below 1 is the gate admitting a loss.");
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

        // The replica is fed the same views the index composes, snapshotted once: it stands
        // in for the shipped code's rebuild, which composes them from chunk spans.
        var views = new EntityView[layout.Entities];
        // Snapshotted through the brute-force overload, which never consults the index, so
        // the replica is fed the same entities in the same chunk order the scan produces.
        int viewCount = brute.GetEntitiesInRange(new Vec2(0f, 0f), float.MaxValue, views);

        var legacy = new LegacyStructSortGrid(Radius);
        legacy.Rebuild(views, viewCount);

        double lastBrute = 0, lastPerm = 0, lastStruct = 0;

        // The replica must return what the scan returns, or the arm below is timing
        // something that is fast for the wrong reason. Checked on every layout, against the
        // same oracle AoiIndexDifferentialTests uses, before a single timing is taken.
        var expected = new EntityView[layout.Entities];
        var got = new EntityView[layout.Entities];
        for (int i = 0; i < layout.Entities; i++)
        {
            int want = brute.GetEntitiesInRange(positions[i], Radius, expected);
            int have = legacy.Query(positions[i], Radius, got);
            if (want != have)
            {
                throw new InvalidOperationException(
                    $"{layout.Label}: replica found {have} entities where the scan found {want}.");
            }

            for (int k = 0; k < want; k++)
            {
                if (expected[k].Id != got[k].Id)
                {
                    throw new InvalidOperationException(
                        $"{layout.Label}: replica order differs at {k} — scan '{expected[k].Id}', " +
                        $"replica '{got[k].Id}'.");
                }
            }
        }

        // Two SEPARATE paired measurements, not one three-way interleave.
        //
        // Running all three arms in one round changed both ratios materially — the
        // uniform-full-map row read 1.76-1.91x as a pair and 1.12x with a third arm added,
        // because three working sets evict each other where two do not, and the index arms
        // carry more state than the scan. Each pair is therefore measured on its own, which
        // is also what keeps these numbers comparable with Part X's and with the two-arm
        // figures in Part XI.
        var permRatios = MeasurePair(measureLegacy: false);
        var structRatios = MeasurePair(measureLegacy: true);

        List<double> MeasurePair(bool measureLegacy)
        {
            var ratios = new List<double>(Repetitions);

            for (int rep = 0; rep < Repetitions; rep++)
            {
                var bruteSamples = new List<long>(Rounds);
                var otherSamples = new List<long>(Rounds);

                for (int round = 0; round < Rounds + Warmup; round++)
                {
                    // Alternating, for the ~5% ordering bias Part X records.
                    bool bruteFirst = (round & 1) == 0;

                    long t0 = Stopwatch.GetTimestamp();
                    if (bruteFirst) GatherBrute(); else GatherOther();
                    long t1 = Stopwatch.GetTimestamp();
                    if (bruteFirst) GatherOther(); else GatherBrute();
                    long t2 = Stopwatch.GetTimestamp();

                    if (round < Warmup) continue;
                    bruteSamples.Add(bruteFirst ? t1 - t0 : t2 - t1);
                    otherSamples.Add(bruteFirst ? t2 - t1 : t1 - t0);
                }

                double b = Median(bruteSamples);
                double o = Median(otherSamples);
                ratios.Add(b / o);

                lastBrute = b;
                if (measureLegacy) lastStruct = o; else lastPerm = o;
            }

            return ratios;

            void GatherBrute() => brute.ReadAll(reader =>
            {
                for (int i = 0; i < layout.Entities; i++)
                {
                    reader.GetEntitiesInRange(positions[i], Radius, buffer);
                }
            });

            void GatherOther()
            {
                if (measureLegacy)
                {
                    legacy.Rebuild(views, viewCount);
                    for (int i = 0; i < layout.Entities; i++)
                    {
                        legacy.Query(positions[i], Radius, buffer);
                    }

                    return;
                }

                indexed.ReadAll(reader =>
                {
                    for (int i = 0; i < layout.Entities; i++)
                    {
                        reader.GetEntitiesInRange(positions[i], Radius, buffer);
                    }
                });
            }
        }

        int cells = indexed.AoiIndexOccupiedCells;
        double candFrac = indexed.AoiIndexCandidateFraction;
        string gate = cells >= SpatialGrid.MinOccupiedCellsToQuery ? "ON" : "OFF";
        double avgMatches = (double)totalMatches / layout.Entities;
        string permSpread = string.Join(" ", permRatios.Select(r => $"{r:F2}x"));
        string structSpread = string.Join(" ", structRatios.Select(r => $"{r:F2}x"));

        Line($"{layout.Label,-26} {layout.Entities,5} {cells,5} {gate,4} {avgMatches,7:F1} " +
             $"{candFrac,8:F4} {lastBrute,7:F0} {lastPerm,7:F0} {lastStruct,7:F0} " +
             $"{permSpread,17} {structSpread,17}");
    }

    /// <summary>
    /// <b>Verbatim replica of the pre-fix ordering strategy</b> — the spatial index as it
    /// was merged, which sorted an array of <see cref="EntityView"/> structs to restore
    /// scan order instead of sorting an <c>int</c> permutation.
    ///
    /// <para><b>Why a replica rather than a switch in production.</b> The same reason
    /// Part VII's <c>LegacyStringKeyedDeltaState</c> lives in its bench file: the A arm
    /// should be the code that actually ran, and production should carry nothing that
    /// exists only for a benchmark. An earlier revision of this bench flipped a mutable
    /// static inside <c>SpatialGrid</c>; this replaces it, and <c>SpatialGrid</c> now has
    /// exactly one ordering strategy.</para>
    ///
    /// <para><b>What differs from production, and which way it biases.</b> Production
    /// rebuilds from Arch chunk spans, composing each <see cref="EntityView"/> as it goes;
    /// this rebuilds from a pre-composed array, so it does <i>less</i> work per rebuild than
    /// the code it stands in for. The struct-sort penalty this arm reports is therefore a
    /// <b>lower bound</b> — the real pre-fix cost was at least this bad. Everything that
    /// decides the comparison is copied unchanged: cell size, floor-based cell assignment,
    /// the counting sort that keeps scan order within a cell, the covering-neighbourhood
    /// walk, the full-sweep fallback, the inclusive distance predicate, and the
    /// struct-sorting emit.</para>
    /// </summary>
    private sealed class LegacyStructSortGrid
    {
        private struct Entry
        {
            public EntityView View;
            public int ScanOrdinal;
        }

        private readonly float _cellSize;
        private readonly float _invCellSize;
        private readonly Dictionary<long, int> _cellOrdinals = new();
        private int[] _cellStart = Array.Empty<int>();
        private int[] _cellCount = Array.Empty<int>();
        private int[] _cursor = Array.Empty<int>();
        private long[] _entryKeys = Array.Empty<long>();
        private Entry[] _entries = Array.Empty<Entry>();
        private Entry[] _bucketed = Array.Empty<Entry>();
        private int _entryCount;
        private int _cellsUsed;

        private int[] _scratchOrdinals = Array.Empty<int>();
        private EntityView[] _scratchViews = Array.Empty<EntityView>();

        public LegacyStructSortGrid(float cellSize)
        {
            _cellSize = cellSize;
            _invCellSize = 1f / cellSize;
        }

        public int OccupiedCells => _cellsUsed;

        /// <summary>One whole-index rebuild, as production does once per gather scope.</summary>
        public void Rebuild(EntityView[] views, int count)
        {
            if (_entries.Length < count)
            {
                _entries = new Entry[count];
                _entryKeys = new long[count];
                _bucketed = new Entry[count];
                _scratchOrdinals = new int[count];
                _scratchViews = new EntityView[count];
            }

            _entryCount = 0;
            _cellsUsed = 0;
            _cellOrdinals.Clear();

            for (int i = 0; i < count; i++)
            {
                long key = CellKey(views[i].Position);
                _entries[_entryCount] = new Entry { View = views[i], ScanOrdinal = _entryCount };
                _entryKeys[_entryCount] = key;
                _entryCount++;
                if (!_cellOrdinals.ContainsKey(key)) _cellOrdinals[key] = _cellsUsed++;
            }

            if (_cellStart.Length < _cellsUsed + 1)
            {
                _cellStart = new int[_cellsUsed + 1];
                _cellCount = new int[_cellsUsed + 1];
                _cursor = new int[_cellsUsed + 1];
            }

            Array.Clear(_cellCount, 0, _cellsUsed);
            for (int i = 0; i < _entryCount; i++) _cellCount[_cellOrdinals[_entryKeys[i]]]++;

            int running = 0;
            for (int c = 0; c < _cellsUsed; c++)
            {
                _cellStart[c] = running;
                _cursor[c] = running;
                running += _cellCount[c];
            }

            for (int i = 0; i < _entryCount; i++)
            {
                _bucketed[_cursor[_cellOrdinals[_entryKeys[i]]]++] = _entries[i];
            }

            Array.Copy(_bucketed, _entries, _entryCount);
        }

        public int Query(in Vec2 center, float radius, Span<EntityView> destination)
        {
            if (_entryCount == 0) return 0;

            float radiusSq = radius * radius;
            int matches = 0;

            int minX = CellCoord(center.X - radius);
            int maxX = CellCoord(center.X + radius);
            int minY = CellCoord(center.Y - radius);
            int maxY = CellCoord(center.Y + radius);

            long cellsSpanned = (long)(maxX - minX + 1) * (maxY - minY + 1);
            if (cellsSpanned >= _entryCount)
            {
                for (int i = 0; i < _entryCount; i++)
                {
                    ref Entry e = ref _entries[i];
                    if (Vec2.DistanceSq(center, e.View.Position) > radiusSq) continue;
                    _scratchOrdinals[matches] = e.ScanOrdinal;
                    _scratchViews[matches] = e.View;
                    matches++;
                }

                return EmitByStructSort(matches, destination);
            }

            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cy = minY; cy <= maxY; cy++)
                {
                    if (!_cellOrdinals.TryGetValue(Pack(cx, cy), out int ordinal)) continue;

                    int start = _cellStart[ordinal];
                    int end = start + _cellCount[ordinal];
                    for (int i = start; i < end; i++)
                    {
                        ref Entry e = ref _entries[i];
                        if (Vec2.DistanceSq(center, e.View.Position) > radiusSq) continue;
                        _scratchOrdinals[matches] = e.ScanOrdinal;
                        _scratchViews[matches] = e.View;
                        matches++;
                    }
                }
            }

            return EmitByStructSort(matches, destination);
        }

        /// <summary>The line this whole class exists to preserve: sorting the views.</summary>
        private int EmitByStructSort(int matches, Span<EntityView> destination)
        {
            Span<int> keys = _scratchOrdinals.AsSpan(0, matches);
            Span<EntityView> values = _scratchViews.AsSpan(0, matches);
            keys.Sort(values);

            int emitted = Math.Min(matches, destination.Length);
            values[..emitted].CopyTo(destination[..emitted]);
            return matches;
        }

        private int CellCoord(float v) => (int)MathF.Floor(v * _invCellSize);

        private long CellKey(in Vec2 position) => Pack(CellCoord(position.X), CellCoord(position.Y));

        private static long Pack(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
    }

    /// <summary>Standard normal via Box-Muller. Clusters are Gaussian, not uniform discs.</summary>
    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
