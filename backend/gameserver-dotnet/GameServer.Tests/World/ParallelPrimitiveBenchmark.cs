using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.World;

/// <summary>
/// Cost model for <see cref="EcsWorld.UpdateComponentsParallel"/>.
///
/// <para><b>Not a test.</b> Nothing here asserts a timing bound — a timing assertion on a
/// shared developer box is a flake generator, and ADR-12 decision 7 says a speed claim
/// needs a measurement, not a green check. These are skipped unless
/// <c>BENCH_PARALLEL=1</c> is set, and their printed output is the deliverable.</para>
///
/// <para><b>Clock.</b> <see cref="Stopwatch"/> only. This host's <c>CLOCK_REALTIME</c>
/// runs fast with unstable skew (issue #153), so a <c>DateTime</c> figure would be wrong
/// by a double-digit percentage and wrong by a varying amount.</para>
///
/// <para><b>Noise control.</b> A load generator shares this box (ADR-7). Every comparison
/// interleaves the arms it compares within one run rather than running arm A to
/// completion and then arm B, so a load spike lands on both arms; each arm reports median,
/// p99 and min over the stated sample count, so a reader can see when the median is not
/// trustworthy.</para>
/// </summary>
public class ParallelPrimitiveBenchmark
{
    private readonly ITestOutputHelper _out;

    public ParallelPrimitiveBenchmark(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("BENCH_PARALLEL") == "1";

    private const int MaxSlots = 8;

    // ------------------------------------------------------------------ plumbing

    private void Line(string s)
    {
        _out.WriteLine(s);
        Console.WriteLine(s);
    }

    /// <summary>Microseconds, from raw Stopwatch ticks.</summary>
    private static double Us(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    private readonly struct Stats
    {
        public readonly double Median;
        public readonly double P99;
        public readonly double Min;

        public Stats(List<long> samples)
        {
            samples.Sort();
            Median = Us(samples[samples.Count / 2]);
            int p99 = (int)(samples.Count * 0.99);
            if (p99 >= samples.Count) p99 = samples.Count - 1;
            P99 = Us(samples[p99]);
            Min = Us(samples[0]);
        }

        public override string ToString() =>
            $"med {Median,10:F2}  p99 {P99,10:F2}  min {Min,10:F2}";
    }

    /// <summary>
    /// Run each arm once per round, round-robin, so a background load spike lands on every
    /// arm rather than on whichever one happened to be running.
    /// </summary>
    private static List<long>[] Interleave(int rounds, int warmup, params Action[] arms)
    {
        var samples = new List<long>[arms.Length];
        for (int a = 0; a < arms.Length; a++) samples[a] = new List<long>(rounds);

        for (int w = 0; w < warmup; w++)
        {
            for (int a = 0; a < arms.Length; a++) arms[a]();
        }

        var sw = new Stopwatch();
        for (int r = 0; r < rounds; r++)
        {
            for (int a = 0; a < arms.Length; a++)
            {
                sw.Restart();
                arms[a]();
                sw.Stop();
                samples[a].Add(sw.ElapsedTicks);
            }
        }

        return samples;
    }

    private static EcsWorld MakeWorld(int entities, int slots = MaxSlots)
    {
        var world = new EcsWorld(slots);
        for (int i = 0; i < entities; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"e{i}", x: i % 512, y: i / 512f));
        }
        return world;
    }

    /// <summary>
    /// Handles for every entity, resolved once outside the timed region. Arch entities are
    /// stable identities and nothing timed below is structural, so these stay valid.
    /// </summary>
    private static EntityHandle[] AllHandles(EcsWorld world, int entities)
    {
        var handles = new EntityHandle[entities];
        world.UpdateComponents(w =>
        {
            for (int i = 0; i < entities; i++) handles[i] = w.Resolve($"e{i}");
        });
        return handles;
    }

    /// <summary>
    /// The per-entity work. Deliberately the same arithmetic as <c>EnemyMoveSystem.Body</c>:
    /// read Health, read/write Position, one sqrt. Through handles rather than chunk spans
    /// because that is the only access path a partitioned worker has — <c>VisitChunks</c>
    /// visits every chunk and cannot be sliced.
    /// </summary>
    private static void MoveRange(WorldWriter w, EntityHandle[] handles, int from, int to, float dt)
    {
        for (int i = from; i < to; i++)
        {
            ref readonly EntityHandle h = ref handles[i];
            if (w.HealthOf(in h).Dead) continue;

            ref Position p = ref w.PositionOf(in h);
            float dx = -p.Value.X;
            float dy = -p.Value.Y;
            float distSq = dx * dx + dy * dy;
            if (distSq <= 0.01f) continue;

            float invDist = 1.0f / MathF.Sqrt(distSq);
            p.Value = new Vec2(p.Value.X + dx * invDist * 2.5f * dt,
                               p.Value.Y + dy * invDist * 2.5f * dt);
        }
    }

    /// <summary>
    /// Drive every path a benchmark arm uses until the JIT has promoted it out of tier 0.
    ///
    /// <para><b>Why this is not optional.</b> The <i>first</i> configuration measured in a
    /// process runs on tier-0 code and reads impossibly flat: an early sweep timed 500
    /// entities slower than 2 000, and 30 entities at 103 ns/entity against 9.5 ns/entity
    /// for the same body at 10 000. Per-arm warm-up rounds do not fix it, because tier-1
    /// promotion needs both a call count and time for the background compile — a few dozen
    /// rounds of a 9 microsecond body supply neither. So the whole shape is exercised once,
    /// on a throwaway world, before the first configuration is timed.</para>
    /// </summary>
    private static void WarmUpJit()
    {
        using var world = MakeWorld(2_000, MaxSlots);
        EntityHandle[] handles = AllHandles(world, 2_000);

        for (int r = 0; r < 400; r++)
        {
            world.UpdateComponents(w => MoveRange(w, handles, 0, 2_000, 1f / 15f));
            world.UpdateComponentsParallel(4, (w, slot) =>
            {
                Slice(2_000, 4, slot, out int from, out int to);
                MoveRange(w, handles, from, to, 1f / 15f);
            });
        }

        Thread.Sleep(200);   // let the tier-1 compilations land before anything is timed
    }

    private static void Slice(int total, int workers, int slot, out int from, out int to)
    {
        int per = total / workers;
        from = slot * per;
        to = slot == workers - 1 ? total : from + per;
    }

    // ------------------------------------------------------ 1. overhead floor

    [SkippableFact]
    public void Bench1_OverheadFloor()
    {
        Skip.IfNot(Enabled, "Set BENCH_PARALLEL=1 to run.");

        const int rounds = 2000;
        using var world = new EcsWorld(MaxSlots);

        Action empty = () => world.UpdateComponents(_ => { });
        Action p1 = () => world.UpdateComponentsParallel(1, (_, _) => { });
        Action p2 = () => world.UpdateComponentsParallel(2, (_, _) => { });
        Action p4 = () => world.UpdateComponentsParallel(4, (_, _) => { });
        Action p8 = () => world.UpdateComponentsParallel(8, (_, _) => { });

        List<long>[] s = Interleave(rounds, warmup: 50, empty, p1, p2, p4, p8);
        string[] names = { "serial UpdateComponents", "parallel w=1", "parallel w=2", "parallel w=4", "parallel w=8" };

        Line($"== 1. Overhead floor, empty body, {rounds} rounds interleaved, microseconds ==");
        for (int i = 0; i < s.Length; i++) Line($"{names[i],-26} {new Stats(s[i])}");

        // Slot-count sensitivity of the drain: it walks every slot even when all are empty.
        using var w1 = new EcsWorld(1);
        using var w8 = new EcsWorld(8);
        List<long>[] d = Interleave(rounds, 50,
            () => w1.UpdateComponentsParallel(1, (_, _) => { }),
            () => w8.UpdateComponentsParallel(1, (_, _) => { }));
        Line($"{"w=1, world has 1 slot",-26} {new Stats(d[0])}");
        Line($"{"w=1, world has 8 slots",-26} {new Stats(d[1])}");
    }

    /// <summary>
    /// The floor again, but with the workers <b>parked</b> rather than hot.
    ///
    /// <para><b>Why this exists.</b> A pooled worker's dispatch cost depends on whether it
    /// is still spinning or already blocked in the kernel, and the live tick always meets
    /// it blocked — regions are 66 ms apart. Workers currently park immediately
    /// (<c>EcsWorld.SimWorkerPool.WorkerParkSpinMicros</c> is zero), so this arm and
    /// <see cref="Bench1_OverheadFloor"/> should agree; they stop agreeing the moment
    /// someone reintroduces a worker-side spin, which is exactly when the difference needs
    /// to be visible rather than assumed.</para>
    ///
    /// <para>The gap is produced with a busy loop rather than <c>Thread.Sleep</c>: sleep
    /// would also deschedule the <i>owner</i>, so the arm would measure the owner's own
    /// wake-up as well as the workers'.</para>
    /// </summary>
    [SkippableFact]
    public void Bench1b_ParkedOverheadFloor()
    {
        Skip.IfNot(Enabled, "Set BENCH_PARALLEL=1 to run.");

        const int rounds = 400;
        const double gapMicros = 2000;   // far past any plausible worker spin budget
        long gapTicks = (long)(gapMicros * Stopwatch.Frequency / 1_000_000.0);

        using var world = new EcsWorld(MaxSlots);

        static void Gap(long ticks)
        {
            long until = Stopwatch.GetTimestamp() + ticks;
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
        }

        Line($"== 1b. Overhead floor with workers parked ({gapMicros:F0} us idle between regions), " +
             $"{rounds} rounds interleaved, microseconds ==");

        foreach (int w in new[] { 1, 2, 4, 8 })
        {
            var samples = new List<long>(rounds);
            var sw = new Stopwatch();

            for (int r = -20; r < rounds; r++)
            {
                Gap(gapTicks);
                sw.Restart();
                world.UpdateComponentsParallel(w, (_, _) => { });
                sw.Stop();
                if (r >= 0) samples.Add(sw.ElapsedTicks);
            }

            var st = new Stats(samples);
            double perWorker = w > 1 ? st.Median / (w - 1) : 0;
            Line($"{"parallel w=" + w,-26} {st}  per extra worker {perWorker,8:F2}");
        }
    }

    // ------------------------------------------------------------ 2. scaling

    [SkippableFact]
    public void Bench2_Scaling()
    {
        Skip.IfNot(Enabled, "Set BENCH_PARALLEL=1 to run.");

        WarmUpJit();

        Line("== 2. Scaling: identical total work, serial vs N workers. microseconds ==");
        Line($"{"entities",8} {"arm",-14} {"median",10} {"p99",10} {"min",10} {"speedup",9}");

        foreach (int n in new[] { 100, 1_000, 10_000, 100_000 })
        {
            using EcsWorld world = MakeWorld(n);
            EntityHandle[] handles = AllHandles(world, n);
            const float dt = 1f / 15f;

            Action serial = () => world.UpdateComponents(w => MoveRange(w, handles, 0, n, dt));
            Func<int, Action> par = workers => () => world.UpdateComponentsParallel(workers, (w, slot) =>
            {
                Slice(n, workers, slot, out int from, out int to);
                MoveRange(w, handles, from, to, dt);
            });

            int rounds = n >= 100_000 ? 200 : n >= 10_000 ? 600 : 2000;
            List<long>[] s = Interleave(rounds, warmup: 30, serial, par(2), par(4), par(8));
            string[] names = { "serial", "parallel w=2", "parallel w=4", "parallel w=8" };

            double baseline = new Stats(s[0]).Median;
            for (int i = 0; i < s.Length; i++)
            {
                var st = new Stats(s[i]);
                Line($"{n,8} {names[i],-14} {st.Median,10:F2} {st.P99,10:F2} {st.Min,10:F2} {baseline / st.Median,8:F2}x  (n={rounds})");
            }
        }
    }

    /// <summary>Sweep entity count to locate where parallel w=4 first beats serial.</summary>
    [SkippableFact]
    public void Bench2b_CrossoverSweep()
    {
        Skip.IfNot(Enabled, "Set BENCH_PARALLEL=1 to run.");

        WarmUpJit();

        Line("== 2b. Crossover sweep (parallel w=4 vs serial). microseconds ==");
        Line($"{"entities",8} {"serial med",11} {"w4 med",11} {"w4/serial",10}");

        foreach (int n in new[] { 500, 1_000, 2_000, 4_000, 8_000, 16_000, 32_000, 64_000, 128_000, 256_000 })
        {
            using EcsWorld world = MakeWorld(n);
            EntityHandle[] handles = AllHandles(world, n);
            const float dt = 1f / 15f;

            Action serial = () => world.UpdateComponents(w => MoveRange(w, handles, 0, n, dt));
            Action par4 = () => world.UpdateComponentsParallel(4, (w, slot) =>
            {
                Slice(n, 4, slot, out int from, out int to);
                MoveRange(w, handles, from, to, dt);
            });

            int rounds = n >= 64_000 ? 300 : 1500;
            List<long>[] s = Interleave(rounds, 30, serial, par4);
            var a = new Stats(s[0]);
            var b = new Stats(s[1]);
            Line($"{n,8} {a.Median,11:F2} {b.Median,11:F2} {b.Median / a.Median,10:F2}");
        }
    }

    // ------------------------------------------------- 3. structural-op cost

    [SkippableFact]
    public void Bench3_StructuralOps()
    {
        Skip.IfNot(Enabled, "Set BENCH_PARALLEL=1 to run.");

        Line("== 3. Structural ops: queue+slot-ordered replay vs immediate create. microseconds ==");
        Line($"{"ops",8} {"arm",-28} {"median",11} {"p99",11} {"us/op",9}");

        foreach (int ops in new[] { 10, 100, 1_000, 10_000 })
        {
            // Arm 0: serial scope, no deferral -> Arch.Create runs immediately.
            // Arm 1: parallel region w=1 -> every op queued, replayed on join.
            // Arm 2: parallel region w=4 -> ops split over 4 slots, replayed in slot order.
            // Each round rebuilds the world outside the timed section so all arms start empty.
            var samples = new List<long>[3];
            for (int i = 0; i < 3; i++) samples[i] = new List<long>();

            int rounds = ops >= 10_000 ? 60 : ops >= 1_000 ? 200 : 800;
            var sw = new Stopwatch();

            for (int r = -5; r < rounds; r++)
            {
                for (int arm = 0; arm < 3; arm++)
                {
                    using var world = new EcsWorld(MaxSlots);
                    sw.Restart();
                    switch (arm)
                    {
                        case 0:
                            world.UpdateComponents(w =>
                            {
                                for (int i = 0; i < ops; i++)
                                    w.Spawn(TestHelpers.CreatePlayer($"s{i}", i, i), EntityTags.None);
                            });
                            break;
                        case 1:
                            world.UpdateComponentsParallel(1, (w, _) =>
                            {
                                for (int i = 0; i < ops; i++)
                                    w.Spawn(TestHelpers.CreatePlayer($"s{i}", i, i), EntityTags.None);
                            });
                            break;
                        default:
                            world.UpdateComponentsParallel(4, (w, slot) =>
                            {
                                Slice(ops, 4, slot, out int from, out int to);
                                for (int i = from; i < to; i++)
                                    w.Spawn(TestHelpers.CreatePlayer($"s{i}", i, i), EntityTags.None);
                            });
                            break;
                    }
                    sw.Stop();
                    if (r >= 0) samples[arm].Add(sw.ElapsedTicks);
                }
            }

            string[] names = { "serial, applied immediately", "w=1, queued then replayed", "w=4, 4 slots then replayed" };
            for (int i = 0; i < 3; i++)
            {
                var st = new Stats(samples[i]);
                Line($"{ops,8} {names[i],-28} {st.Median,11:F2} {st.P99,11:F2} {st.Median / ops,9:F3}  (n={rounds})");
            }
        }
    }

    // ---------------------------------------------------- 4. contention reality

    /// <summary>
    /// Worker occupancy: each worker times its own body, the region owner times the whole
    /// region. occupancy = mean(body) / region. A worker that spends wall time blocked on
    /// the world lock, or starved, shows up as occupancy well below 1.
    /// </summary>
    [SkippableFact]
    public void Bench4_WorkerOccupancy()
    {
        Skip.IfNot(Enabled, "Set BENCH_PARALLEL=1 to run.");

        Line("== 4. Worker occupancy and lock behaviour ==");
        Line($"{"entities",8} {"workers",7} {"region us",10} {"mean body us",13} {"occupancy",10} {"slow/fast body",15}");

        foreach (int n in new[] { 1_000, 10_000, 100_000 })
        {
            using EcsWorld world = MakeWorld(n);
            EntityHandle[] handles = AllHandles(world, n);
            const float dt = 1f / 15f;

            foreach (int workers in new[] { 2, 4, 8 })
            {
                var bodyTicks = new long[workers];
                var regionSamples = new List<long>();
                var occupancies = new List<double>();
                var imbalances = new List<double>();
                var sw = new Stopwatch();

                for (int r = -20; r < 400; r++)
                {
                    sw.Restart();
                    world.UpdateComponentsParallel(workers, (w, slot) =>
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        Slice(n, workers, slot, out int from, out int to);
                        MoveRange(w, handles, from, to, dt);
                        bodyTicks[slot] = Stopwatch.GetTimestamp() - t0;
                    });
                    sw.Stop();
                    if (r < 0) continue;

                    long sum = 0, max = long.MinValue, min = long.MaxValue;
                    for (int i = 0; i < workers; i++)
                    {
                        sum += bodyTicks[i];
                        if (bodyTicks[i] > max) max = bodyTicks[i];
                        if (bodyTicks[i] < min) min = bodyTicks[i];
                    }
                    regionSamples.Add(sw.ElapsedTicks);
                    occupancies.Add((sum / (double)workers) / sw.ElapsedTicks);
                    imbalances.Add(min <= 0 ? 0 : max / (double)min);
                }

                occupancies.Sort();
                imbalances.Sort();
                var reg = new Stats(regionSamples);
                double occ = occupancies[occupancies.Count / 2];
                Line($"{n,8} {workers,7} {reg.Median,10:F2} {reg.Median * occ,13:F2} {occ,10:F3} {imbalances[imbalances.Count / 2],15:F2}");
            }
        }

        // The only lock traffic that exists: one Enter/Exit pair per region, on the owning
        // thread. The region owner enters the write lock before starting any worker, and the
        // workers call *Locked internals that never touch the lock.
        using var probe = new EcsWorld(MaxSlots);
        const int rounds = 5000;
        List<long>[] s = Interleave(rounds, 100,
            () => probe.UpdateComponents(_ => { }),
            () => { });
        Line($"serial scope, empty body (lock enter+exit+drain check): {new Stats(s[0])} (n={rounds})");
        Line($"empty delegate (measurement floor):                     {new Stats(s[1])}");
    }

    // ------------------------------- 5. what the real schedule would actually do

    /// <summary>
    /// The concrete question: the live schedule is three enemy systems over at most
    /// <c>EnemyAiTuning.MaxEnemies</c> (30) entities. This times a serial pass over that
    /// workload against the best case a parallel schedule could reach, so the answer to
    /// "what if we wired the schedule to run in parallel" is arithmetic on measured numbers.
    /// </summary>
    [SkippableFact]
    public void Bench5_RealScheduleWorkload()
    {
        Skip.IfNot(Enabled, "Set BENCH_PARALLEL=1 to run.");

        WarmUpJit();

        Line("== 5. Live-schedule-sized workload vs region overhead. microseconds ==");

        foreach (int n in new[] { 30, 150, 1_000 })
        {
            using EcsWorld world = MakeWorld(n);
            EntityHandle[] handles = AllHandles(world, n);
            const float dt = 1f / 15f;

            // Three passes over the same entities: the shape of spawn/move/reap minus the
            // structural work, which cannot be parallelised at all (IsDisjointFrom returns
            // false for any structural system).
            Action serial3 = () => world.UpdateComponents(w =>
            {
                MoveRange(w, handles, 0, n, dt);
                MoveRange(w, handles, 0, n, dt);
                MoveRange(w, handles, 0, n, dt);
            });

            // The best case a parallel schedule could reach: three regions, one per system,
            // each split over 4 workers.
            Action par3 = () =>
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    world.UpdateComponentsParallel(4, (w, slot) =>
                    {
                        Slice(n, 4, slot, out int from, out int to);
                        MoveRange(w, handles, from, to, dt);
                    });
                }
            };

            List<long>[] s = Interleave(2000, 50, serial3, par3);
            var a = new Stats(s[0]);
            var b = new Stats(s[1]);
            Line($"{n,7} entities  serial, 3 passes:        {a}");
            Line($"{n,7} entities  3 parallel regions w=4:  {b}   ratio {b.Median / a.Median:F2}x");
        }
    }
}
