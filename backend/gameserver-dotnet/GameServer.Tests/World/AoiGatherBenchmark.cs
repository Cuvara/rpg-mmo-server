using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GameServer.World;
using Shared.GameLogic.Components;
using Xunit;
using Xunit.Abstractions;

namespace GameServer.Tests.World;

/// <summary>
/// Cost model for the AOI gather, serial against the pooled read region.
///
/// <para><b>Why this phase.</b> It is where the tick budget goes: at 200 viewers the
/// gather is 77-83% of <c>TickOnce</c>, at 500 viewers 79-88%, while the whole system
/// schedule is 0.5-1.9 microseconds. Parallelising the schedule is the obvious-looking
/// move and is the wrong one; this is the phase with work in it.</para>
///
/// <para><b>Not a test.</b> Same contract as <see cref="ParallelPrimitiveBenchmark"/>:
/// nothing here asserts a timing bound, output is the deliverable, and the whole file is
/// skipped unless <c>BENCH_PARALLEL=1</c>. <see cref="Stopwatch"/> only — this host's
/// <c>CLOCK_REALTIME</c> runs 10-17% fast with unstable skew (#153).</para>
///
/// <para><b>What is measured.</b> The gather shape, not the tick: N viewers each running
/// one range query over the same world into their own destination buffer, which is what
/// <c>Connection.GatherSnapshotView</c> does minus the connection plumbing. Arms are
/// interleaved within a run, because the 500-viewer serial median has been seen to move
/// 76% between runs on this box — only same-run ratios are quotable.</para>
/// </summary>
public class AoiGatherBenchmark
{
    private readonly ITestOutputHelper _out;

    public AoiGatherBenchmark(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("BENCH_PARALLEL") == "1";

    private const int MaxWorkers = 8;

    /// <summary>Entities in the world, independent of viewer count: the scan is
    /// O(viewers x entities) and the second factor has to be held still.</summary>
    private const int WorldEntities = 400;

    private void Line(string s)
    {
        _out.WriteLine(s);
        Console.WriteLine(s);
    }

    private static double Us(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    private readonly struct Stats
    {
        public readonly double Median, P99, Min, Max;

        public Stats(List<long> samples)
        {
            samples.Sort();
            Median = Us(samples[samples.Count / 2]);
            int p99 = Math.Min((int)(samples.Count * 0.99), samples.Count - 1);
            P99 = Us(samples[p99]);
            Min = Us(samples[0]);
            Max = Us(samples[^1]);
        }

        public override string ToString() =>
            $"med {Median,9:F2}  p99 {P99,9:F2}  min {Min,9:F2}  max {Max,9:F2}";
    }

    /// <summary>A world of <see cref="WorldEntities"/> players spread over the map, and one
    /// destination buffer per viewer — the connection-owned double buffer, minus the
    /// connection.</summary>
    private static EcsWorld MakeWorld(int workers)
    {
        var world = new EcsWorld(workers);
        for (int i = 0; i < WorldEntities; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"e{i}", x: (i * 37) % 200, y: (i * 53) % 200));
        }
        return world;
    }

    private static EntityState[][] MakeBuffers(int viewers) =>
        MakeBuffers(viewers, WorldEntities);

    private static EntityState[][] MakeBuffers(int viewers, int capacity)
    {
        var buffers = new EntityState[viewers][];
        for (int i = 0; i < viewers; i++) buffers[i] = new EntityState[capacity];
        return buffers;
    }

    private static Vec2[] MakeAnchors(int viewers)
    {
        var anchors = new Vec2[viewers];
        for (int i = 0; i < viewers; i++) anchors[i] = new Vec2((i * 29) % 200, (i * 61) % 200);
        return anchors;
    }

    private const float Radius = 50f;

    private static void GatherRange(WorldReader reader, Vec2[] anchors, EntityState[][] buffers, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            reader.GetEntitiesInRange(anchors[i], Radius, buffers[i]);
        }
    }

    /// <summary>See <c>ParallelPrimitiveBenchmark.WarmUpJit</c> — the first configuration
    /// in a process runs on tier-0 code and reads nonsense.</summary>
    private static void WarmUpJit()
    {
        using EcsWorld world = MakeWorld(MaxWorkers);
        Vec2[] anchors = MakeAnchors(64);
        EntityState[][] buffers = MakeBuffers(64);

        for (int r = 0; r < 300; r++)
        {
            world.ReadAll(reader => GatherRange(reader, anchors, buffers, 0, 64));
            world.ReadAllParallel(4, (reader, slot) =>
            {
                int per = 64 / 4;
                GatherRange(reader, anchors, buffers, slot * per, slot == 3 ? 64 : slot * per + per);
            });
        }

        Thread.Sleep(200);
    }

    [SkippableFact]
    public void Bench_GatherAtViewerCounts()
    {
        Skip.IfNot(Enabled, "Set BENCH_PARALLEL=1 to run.");

        WarmUpJit();

        Line($"== AOI gather, {WorldEntities} entities, radius {Radius}, serial vs pooled read region. " +
             "microseconds, arms interleaved within the run ==");
        Line($"{"viewers",8} {"arm",-10} {"median",9} {"p99",9} {"min",9} {"max",9} {"speedup",9}");

        foreach (int viewers in new[] { 50, 200, 500 })
        {
            using EcsWorld world = MakeWorld(MaxWorkers);
            Vec2[] anchors = MakeAnchors(viewers);
            EntityState[][] buffers = MakeBuffers(viewers);

            var arms = new List<(string Name, Action Run)>
            {
                ("serial", () => world.ReadAll(reader => GatherRange(reader, anchors, buffers, 0, viewers))),
            };

            foreach (int w in new[] { 2, 4, 8 })
            {
                int workers = w;
                arms.Add(($"pooled w={workers}", () => world.ReadAllParallel(workers, (reader, slot) =>
                {
                    int per = viewers / workers;
                    int from = slot * per;
                    int to = slot == workers - 1 ? viewers : from + per;
                    GatherRange(reader, anchors, buffers, from, to);
                })));
            }

            int rounds = viewers >= 500 ? 400 : 1200;
            var samples = new List<long>[arms.Count];
            for (int a = 0; a < arms.Count; a++) samples[a] = new List<long>(rounds);

            for (int w = 0; w < 30; w++) foreach (var arm in arms) arm.Run();

            var sw = new Stopwatch();
            for (int r = 0; r < rounds; r++)
            {
                for (int a = 0; a < arms.Count; a++)
                {
                    sw.Restart();
                    arms[a].Run();
                    sw.Stop();
                    samples[a].Add(sw.ElapsedTicks);
                }
            }

            double baseline = new Stats(samples[0]).Median;
            for (int a = 0; a < arms.Count; a++)
            {
                var st = new Stats(samples[a]);
                Line($"{viewers,8} {arms[a].Name,-10} {st.Median,9:F2} {st.P99,9:F2} {st.Min,9:F2} {st.Max,9:F2} " +
                     $"{baseline / st.Median,8:F2}x  (n={rounds})");
            }

            Line($"{viewers,8} per-viewer serial: {baseline / viewers:F3} us");
        }
    }
}

/// <summary>
/// The correctness half of the pooled gather: same world, same anchors, same buffers —
/// the parallel path must stage exactly what the serial path stages.
///
/// <para>This is a real test, not a benchmark, and it runs unconditionally. It is what
/// justifies <see cref="EcsWorld.ReadAllParallel"/> carrying no determinism machinery:
/// the claim "each viewer writes its own buffer, so interleaving cannot change the
/// output" is checked rather than asserted.</para>
/// </summary>
public class PooledGatherEquivalenceTests
{
    private const int Entities = 300;
    private const int Viewers = 64;
    private const float Radius = 40f;

    private static EcsWorld MakeWorld(int workers)
    {
        var world = new EcsWorld(workers);
        for (int i = 0; i < Entities; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"e{i}", x: (i * 37) % 200, y: (i * 53) % 200));
        }
        return world;
    }

    private static Vec2[] Anchors()
    {
        var anchors = new Vec2[Viewers];
        for (int i = 0; i < Viewers; i++) anchors[i] = new Vec2((i * 29) % 200, (i * 61) % 200);
        return anchors;
    }

    private static (EntityState[][] Buffers, int[] Counts) Gather(EcsWorld world, Vec2[] anchors, int workers)
    {
        var buffers = new EntityState[Viewers][];
        for (int i = 0; i < Viewers; i++) buffers[i] = new EntityState[Entities];
        var counts = new int[Viewers];

        if (workers == 1)
        {
            world.ReadAll(reader =>
            {
                for (int i = 0; i < Viewers; i++)
                    counts[i] = reader.GetEntitiesInRange(anchors[i], Radius, buffers[i]);
            });
        }
        else
        {
            world.ReadAllParallel(workers, (reader, slot) =>
            {
                int per = Viewers / workers;
                int from = slot * per;
                int to = slot == workers - 1 ? Viewers : from + per;
                for (int i = from; i < to; i++)
                    counts[i] = reader.GetEntitiesInRange(anchors[i], Radius, buffers[i]);
            });
        }

        return (buffers, counts);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void APooledGatherStagesExactlyWhatTheSerialGatherStages(int workers)
    {
        using EcsWorld world = MakeWorld(8);
        Vec2[] anchors = Anchors();

        (EntityState[][] expectedBuffers, int[] expectedCounts) = Gather(world, anchors, 1);
        (EntityState[][] actualBuffers, int[] actualCounts) = Gather(world, anchors, workers);

        Assert.Equal(expectedCounts, actualCounts);

        for (int v = 0; v < Viewers; v++)
        {
            // Element order too, not just the set: the scan walks chunks in a fixed order
            // and each viewer's scan is independent of every other viewer's, so the order a
            // viewer sees must not move either.
            for (int i = 0; i < expectedCounts[v]; i++)
            {
                Assert.Equal(expectedBuffers[v][i].Id, actualBuffers[v][i].Id);
                Assert.Equal(expectedBuffers[v][i].Position.X, actualBuffers[v][i].Position.X);
                Assert.Equal(expectedBuffers[v][i].Position.Y, actualBuffers[v][i].Position.Y);
                Assert.Equal(expectedBuffers[v][i].Hp, actualBuffers[v][i].Hp);
            }
        }
    }

    [Fact]
    public void ASingleWorkerReadRegionStartsNoThread()
    {
        using var world = new EcsWorld(4);
        int callingThread = Environment.CurrentManagedThreadId;
        int bodyThread = -1;

        world.ReadAllParallel(1, (_, _) => bodyThread = Environment.CurrentManagedThreadId);

        Assert.Equal(callingThread, bodyThread);
    }

    [Fact]
    public void AFailingGatherWorkerSurfacesAndLeavesTheWorldUsable()
    {
        using var world = new EcsWorld(4);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            world.ReadAllParallel(4, (_, slot) =>
            {
                if (slot == 2) throw new InvalidOperationException("gather worker 2 failed");
            }));

        Assert.Equal("gather worker 2 failed", ex.Message);

        // The read lock was released, so the world still takes work of either kind.
        world.AddEntity(TestHelpers.CreatePlayer("after"));
        Assert.NotNull(world.GetEntity("after"));
        world.ReadAllParallel(4, (_, _) => { });
    }

    /// <summary>
    /// The pool is the world's, and it outlives regions rather than threads. Two hundred
    /// regions must not leave two hundred threads behind — the whole point of the change.
    /// </summary>
    [Fact]
    public void RepeatedRegionsReuseTheSameThreads()
    {
        using var world = new EcsWorld(4);
        var seen = new HashSet<int>();

        for (int r = 0; r < 200; r++)
        {
            world.ReadAllParallel(4, (_, _) =>
            {
                lock (seen) seen.Add(Environment.CurrentManagedThreadId);
            });
        }

        // Three workers plus the calling thread, for every one of the 200 regions.
        Assert.Equal(4, seen.Count);
    }
}
