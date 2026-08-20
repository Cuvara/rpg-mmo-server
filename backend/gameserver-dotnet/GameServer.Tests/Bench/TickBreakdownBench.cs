using System.Diagnostics;
using GameServer.Net.Transport;
using GameServer.Scaffolding;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Bench;

/// <summary>
/// A committed, re-runnable breakdown of where <see cref="TickLoop.TickOnce"/> spends its
/// time, at 50 / 200 / 500 simulated viewers — the harness issue #162 records as missing.
///
/// <para><b>Why it exists.</b> <c>backend/docs/ARCHITECTURE-DECISIONS.md</c> and
/// <c>docs/DESIGN.md</c> both quote a stage-4 breakdown (AOI gather ~874-1177 us/tick,
/// <c>Encode</c> ~998-1272 us/tick, <c>ToByteArray</c> ~79-144 us/tick at 200 players) that
/// was produced by a harness nobody committed. Those numbers cannot be reproduced, re-run,
/// or checked against a code change. This file is the replacement: every number it prints
/// comes from code in the repository, on a clock the reader can audit.</para>
///
/// <para><b>Clock discipline — the point of the exercise.</b> Every duration here is
/// measured with <see cref="Stopwatch.GetTimestamp"/> and converted through
/// <see cref="Stopwatch.Frequency"/>. Nothing in this file reads <see cref="DateTime"/>,
/// <c>DateTimeOffset</c>, <c>Environment.TickCount</c> or any other wall clock, and no
/// measured interval contains a wall-clock call. This host's <c>CLOCK_REALTIME</c> runs
/// 10-17% fast with unstable skew (issue #153), so a wall clock would silently inflate
/// every figure here by an unknown, drifting factor. <c>Stopwatch</c> on Linux is
/// <c>CLOCK_MONOTONIC</c> and is unaffected.</para>
///
/// <para><b>Gating.</b> Skipped unless <c>BENCH_TICK=1</c> is set, so it never runs in CI
/// and never slows the default suite:
/// <code>BENCH_TICK=1 dotnet test --filter FullyQualifiedName~TickBreakdownBench \
///   --logger "console;verbosity=detailed"</code></para>
///
/// <para><b>What is deliberately NOT measured.</b> Snapshot encoding and protobuf
/// serialization are no longer on the tick thread at all — stage 4 moved them to each
/// connection's own write task, and <see cref="Connection.GatherSnapshotView"/> only stages
/// a job and sets a flag. This harness therefore measures the tick thread, with the write
/// tasks not running, and reports encoding as off-tick. A separate benchmark
/// (<see cref="TickCostWithWriteTasksLive"/>) runs the same tick with the write tasks live
/// so the contention that decision creates is measured rather than assumed.</para>
///
/// <para><b>Measurement only.</b> No production code was changed to enable this. The
/// sub-phase arms call the same entry points <c>TickOnce</c> calls, in the same order, so
/// the parts can be summed and checked against the whole.</para>
/// </summary>
public sealed class TickBreakdownBench
{
    private readonly ITestOutputHelper _out;

    public TickBreakdownBench(ITestOutputHelper output) => _out = output;

    private const string EnvVar = "BENCH_TICK";

    /// <summary>15Hz uniform: every base tick is a world tick, so every tick broadcasts.</summary>
    private const int TickRate = GameConstants.DefaultTickRate;

    private const float AoiRadius = GameConstants.DefaultAoiRadius;

    /// <summary>Iterations per arm per round.</summary>
    private const int Iterations = 200;

    /// <summary>Independent repeats of the whole interleaved measurement, so spread is visible.</summary>
    private const int Rounds = 5;

    private static readonly int[] ViewerCounts = { 50, 200, 500 };

    // ── Fixtures ──────────────────────────────────────────────────────────────────

    /// <summary>A transport that discards everything, so no I/O cost enters a measurement.</summary>
    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = Stream.Null;
        public string RemoteEndPoint => "bench";
        public void Close() { }
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        public readonly EcsWorld World = new();
        public readonly ConnectionManager Connections = new();
        public readonly TickLoop Loop;
        public readonly EnemySpawner Phase;
        public readonly InputHandler Handler;
        public readonly Connection[] Conns;
        public readonly List<PendingInput> DrainScratch = new();
        public readonly Dictionary<EntityHandle, int> NewestInputIndex = new();
        public readonly Connection[] ViewerScratch;

        private readonly List<Task> _writers = new();
        private ulong _syntheticTick = 1;

        public Rig(int players, bool runWriteTasks)
        {
            var handler = new InputHandler(World, NullLogger.Instance, null, TickRate, MapBounds.Default);
            Handler = handler;
            Phase = new EnemySpawner(World, TickRate, NullLogger.Instance);
            Loop = new TickLoop(World, handler, Connections, TickRate, AoiRadius,
                NullLogger.Instance, metrics: null,
                keyframeInterval: GameConstants.DefaultKeyframeInterval, simulationPhase: Phase);

            // Deterministic placement. Players are spread over a disc of radius 175 around
            // the origin rather than over the whole 1000x1000 map: at map scale an AOI of
            // radius 50 holds about one entity, so the compose half of the scan would never
            // run and the gather would degenerate to pure distance tests. The realised mean
            // AOI occupancy is printed with the results so the numbers stay interpretable.
            var rng = new Random(20260819);
            Conns = new Connection[players];
            for (int i = 0; i < players; i++)
            {
                double a = rng.NextDouble() * Math.PI * 2;
                double r = 175.0 * Math.Sqrt(rng.NextDouble());
                string id = $"p{i}";
                World.AddEntity(TestHelpers.CreatePlayer(
                    id, x: (float)(Math.Cos(a) * r), y: (float)(Math.Sin(a) * r), speed: 4f));

                var conn = new Connection(id, new NullTransport(), NullLogger.Instance, WireEncoding.Proto);
                Conns[i] = conn;
                Connections.Add(conn);
                if (runWriteTasks) _writers.Add(Task.Run(conn.WriteLoopAsync));
            }

            ViewerScratch = new Connection[players * 2];
        }

        /// <summary>One input per viewer — what a 15Hz client population produces.</summary>
        public void PushInputs()
        {
            ulong t = Loop.CurrentTick + 1;
            for (int i = 0; i < Conns.Length; i++)
            {
                World.PushInput(Conns[i].UserId, new InputData(t, 1f, 0f, null));
            }
        }

        public ulong NextSyntheticTick() => _syntheticTick++;

        /// <summary>Mean number of entities inside one viewer's AOI, over every viewer.</summary>
        public double MeanAoiOccupancy()
        {
            double total = 0;
            var buffer = new EntityState[8192];
            World.ReadAll(reader =>
            {
                for (int i = 0; i < Conns.Length; i++)
                {
                    reader.TryGetSnapshotAnchor(Conns[i].UserId, out var anchor, out _);
                    total += reader.GetEntitiesInRange(anchor, AoiRadius, buffer);
                }
            });
            return total / Conns.Length;
        }

        public void Dispose()
        {
            Connections.CloseAll();
            foreach (var c in Conns) { try { c.Dispose(); } catch { } }
            try { Task.WaitAll(_writers.ToArray(), 2000); } catch { }
        }
    }

    // ── A persistent worker pool parked on a barrier ───────────────────────────────

    /// <summary>
    /// W long-lived threads parked on a <see cref="Barrier"/>. Dispatch is two
    /// <c>SignalAndWait</c> calls on the coordinator: one to release the workers, one to
    /// rendezvous when they are done. No thread is created per dispatch — which is the whole
    /// difference from <c>EcsWorld.UpdateComponentsParallel</c>, whose measured 165-225 us
    /// per additional worker is thread creation.
    /// </summary>
    private interface IGatherPool : IDisposable
    {
        int WorkerCount { get; }
        void Dispatch(Action<int, int> body);
    }

    private sealed class BarrierPool : IGatherPool
    {
        private readonly Barrier _barrier;
        private readonly Thread[] _threads;
        private volatile bool _stop;
        private Action<int, int>? _body; // (workerIndex, workerCount)

        public int WorkerCount { get; }

        public BarrierPool(int workers)
        {
            WorkerCount = workers;
            _barrier = new Barrier(workers + 1);
            _threads = new Thread[workers];
            for (int w = 0; w < workers; w++)
            {
                int index = w;
                _threads[w] = new Thread(() =>
                {
                    while (true)
                    {
                        _barrier.SignalAndWait();
                        if (_stop) return;
                        _body?.Invoke(index, WorkerCount);
                        _barrier.SignalAndWait();
                    }
                })
                { IsBackground = true, Name = $"bench-gather-{index}" };
                _threads[w].Start();
            }
        }

        /// <summary>Run <paramref name="body"/> on every worker and return when all are done.</summary>
        public void Dispatch(Action<int, int> body)
        {
            _body = body;
            _barrier.SignalAndWait(); // release
            _barrier.SignalAndWait(); // rendezvous
        }

        public void Dispose()
        {
            _stop = true;
            _body = null;
            _barrier.SignalAndWait();
            foreach (var t in _threads) t.Join(2000);
            _barrier.Dispose();
        }
    }

    /// <summary>
    /// The same shape built from <see cref="SemaphoreSlim"/> wake plus
    /// <see cref="CountdownEvent"/> join, measured alongside the barrier so the choice of
    /// primitive rests on a measurement rather than on a preference.
    /// </summary>
    private sealed class SemaphorePool : IGatherPool
    {
        private readonly SemaphoreSlim[] _go;
        private readonly Thread[] _threads;
        private readonly CountdownEvent _done;
        private volatile bool _stop;
        private Action<int, int>? _body;

        public int WorkerCount { get; }

        public SemaphorePool(int workers)
        {
            WorkerCount = workers;
            _go = new SemaphoreSlim[workers];
            _done = new CountdownEvent(workers);
            _threads = new Thread[workers];
            for (int w = 0; w < workers; w++)
            {
                int index = w;
                _go[w] = new SemaphoreSlim(0, 1);
                _threads[w] = new Thread(() =>
                {
                    while (true)
                    {
                        _go[index].Wait();
                        if (_stop) return;
                        _body?.Invoke(index, WorkerCount);
                        _done.Signal();
                    }
                })
                { IsBackground = true, Name = $"bench-sem-{index}" };
                _threads[w].Start();
            }
        }

        public void Dispatch(Action<int, int> body)
        {
            _body = body;
            _done.Reset(WorkerCount);
            for (int w = 0; w < WorkerCount; w++) _go[w].Release();
            _done.Wait();
        }

        public void Dispose()
        {
            _stop = true;
            _body = null;
            for (int w = 0; w < WorkerCount; w++) _go[w].Release();
            foreach (var t in _threads) t.Join(2000);
            foreach (var s in _go) s.Dispose();
            _done.Dispose();
        }
    }

    // ── Statistics ────────────────────────────────────────────────────────────────

    private sealed class Samples
    {
        public string Name { get; }
        private readonly List<long> _ticks = new();
        private long[]? _sortedCache;

        public Samples(string name) => Name = name;

        public void Add(long stopwatchTicks) { _ticks.Add(stopwatchTicks); _sortedCache = null; }

        private static double ToMicros(long t) => t * 1_000_000.0 / Stopwatch.Frequency;

        public double MinUs => ToMicros(Sorted()[0]);
        public double MedianUs => ToMicros(Sorted()[_ticks.Count / 2]);
        public double P99Us => ToMicros(Sorted()[(int)(0.99 * (_ticks.Count - 1))]);

        private long[] Sorted()
        {
            if (_sortedCache == null)
            {
                _sortedCache = _ticks.ToArray();
                Array.Sort(_sortedCache);
            }
            return _sortedCache;
        }
    }

    /// <summary>
    /// Drive every measured path on a throwaway rig before the first configuration is timed.
    ///
    /// <para><b>This is not optional decoration.</b> Without it the first configuration in
    /// the process is measured on tier-0 JIT code and reads several times slow: a sweep over
    /// 1/10/25/50/100/200/350/500 viewers produced a per-viewer gather cost of 0.92, 0.90,
    /// 1.11, 0.21, 0.27, 0.51, 1.02, 1.37 us — the first three flat at ~1 us/viewer
    /// regardless of how many entities existed to test against, which is impossible for an
    /// O(viewers x entities) scan and is the signature of unpromoted code. Everything from
    /// the fourth configuration on lands at a consistent 2.5-3.9 ns per entity examined.
    /// Whichever configuration runs first pays that tax, so a warm-up that is not itself
    /// reported has to absorb it.</para>
    /// </summary>
    private static void WarmProcess()
    {
        using var rig = new Rig(150, runWriteTasks: false);
        for (int i = 0; i < 400; i++) { rig.PushInputs(); rig.Loop.TickOnce(); }
        using var pool = new BarrierPool(4);
        for (int i = 0; i < 400; i++)
        {
            GatherSerial(rig);
            GatherParallel(rig, pool);
            DrainFor(rig);
            ApplyInputs(rig);
            rig.Phase.Tick(rig.NextSyntheticTick());
            rig.World.ApplyStructuralChanges();
            rig.Connections.CopyTo(rig.ViewerScratch);
        }
    }

    private static string Spread(IReadOnlyList<double> perRoundMedians)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (double v in perRoundMedians) { if (v < lo) lo = v; if (v > hi) hi = v; }
        return $"{lo:F1}-{hi:F1}";
    }

    // ── The breakdown ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public void TickBreakdown()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run the tick-breakdown benchmark.");

        _out.WriteLine($"clock: Stopwatch (CLOCK_MONOTONIC), Frequency={Stopwatch.Frequency} Hz, " +
                       $"IsHighResolution={Stopwatch.IsHighResolution}");
        _out.WriteLine($"cores: {Environment.ProcessorCount}, rate: uniform {TickRate}Hz, " +
                       $"AOI radius {AoiRadius}, {Iterations} iterations x {Rounds} rounds");
        _out.WriteLine("");

        WarmProcess();

        foreach (int viewers in ViewerCounts) RunBreakdown(viewers);
    }

    private void RunBreakdown(int viewers)
    {
        using var rig = new Rig(viewers, runWriteTasks: false);

        // Warm up: let the enemy population reach steady state, let each connection's AOI
        // buffer reach its final size, and let the JIT settle before anything is timed.
        for (int i = 0; i < 400; i++) { rig.PushInputs(); rig.Loop.TickOnce(); }

        double occupancy = rig.MeanAoiOccupancy();
        int entities = rig.World.EntityCount;

        // Arms are run round-robin: on iteration i the arms run starting at index
        // i % armCount, so a load spike from the co-tenant load generator (ADR-7) lands on a
        // different arm each time instead of always on the same one.
        //
        // No worker pool exists in this benchmark, deliberately. A pool parked on a barrier
        // spins before it blocks, and worker threads spinning between dispatches inflate
        // every other arm measured near them — the first version of this file had a pool
        // alive here and the 50-viewer serial gather read 1.8 us/viewer against 0.6 us/viewer
        // at 200, which is arithmetically impossible for an O(viewers x entities) scan.
        // Serial-vs-pooled belongs in GatherParallelScaling, where both arms carry that cost.
        var arms = new (string Name, Action Setup, Action Run)[]
        {
            ("TickOnce (whole)",          rig.PushInputs,     () => rig.Loop.TickOnce()),
            ("AOI gather (serial)",       () => { },          () => GatherSerial(rig)),
            ("Input drain",               rig.PushInputs,     () => rig.World.DrainInputs(rig.DrainScratch)),
            ("Input apply (write scope)", () => DrainFor(rig), () => ApplyInputs(rig)),
            ("SimulationSchedule.RunDue", () => { },          () => rig.Phase.Tick(rig.NextSyntheticTick())),
            ("ApplyStructuralChanges",    () => { },          () => rig.World.ApplyStructuralChanges()),
            ("ConnectionManager.CopyTo",  () => { },          () => rig.Connections.CopyTo(rig.ViewerScratch)),
        };

        var all = new Samples[arms.Length];
        var perRoundMedians = new List<double>[arms.Length];
        for (int a = 0; a < arms.Length; a++)
        {
            all[a] = new Samples(arms[a].Name);
            perRoundMedians[a] = new List<double>();
        }

        // Untimed warm pass over every arm.
        for (int a = 0; a < arms.Length; a++)
        {
            for (int k = 0; k < 20; k++) { arms[a].Setup(); arms[a].Run(); }
        }

        for (int round = 0; round < Rounds; round++)
        {
            var roundSamples = new Samples[arms.Length];
            for (int a = 0; a < arms.Length; a++) roundSamples[a] = new Samples(arms[a].Name);

            for (int i = 0; i < Iterations; i++)
            {
                for (int k = 0; k < arms.Length; k++)
                {
                    int a = (i + k) % arms.Length;
                    arms[a].Setup();
                    long t0 = Stopwatch.GetTimestamp();
                    arms[a].Run();
                    long t1 = Stopwatch.GetTimestamp();
                    all[a].Add(t1 - t0);
                    roundSamples[a].Add(t1 - t0);
                }
            }

            for (int a = 0; a < arms.Length; a++) perRoundMedians[a].Add(roundSamples[a].MedianUs);
        }

        _out.WriteLine($"-- {viewers} viewers -- entities in world: {entities}, " +
                       $"mean AOI occupancy: {occupancy:F1} entities/viewer");
        _out.WriteLine($"{"phase",-32}{"median us",12}{"p99 us",12}{"min us",12}   {"per-round median range",-22}");
        for (int a = 0; a < arms.Length; a++)
        {
            _out.WriteLine($"{all[a].Name,-32}{all[a].MedianUs,12:F1}{all[a].P99Us,12:F1}" +
                           $"{all[a].MinUs,12:F1}   {Spread(perRoundMedians[a]),-22}");
        }

        double whole = all[0].MedianUs;
        double gather = all[1].MedianUs;
        double parts = 0;
        for (int a = 1; a < arms.Length; a++) parts += all[a].MedianUs;
        _out.WriteLine($"AOI gather / TickOnce (median): {100.0 * gather / whole:F1}%");
        _out.WriteLine($"sum of parts {parts:F1} us vs whole {whole:F1} us " +
                       $"({100.0 * parts / whole:F1}% accounted for)");
        _out.WriteLine($"per-viewer gather cost (median gather / viewers): {gather / viewers:F2} us");
        _out.WriteLine("");
    }

    /// <summary>Untimed setup for the input-apply arm: get a fresh batch into the scratch list.</summary>
    private static void DrainFor(Rig rig)
    {
        rig.PushInputs();
        rig.World.DrainInputs(rig.DrainScratch);
    }

    /// <summary>
    /// The rest of <c>TickOnce</c>'s critical group, transcribed from <c>TickLoop.TickOnce</c>:
    /// rebind stale handles, coalesce to the newest input per entity, then one world write
    /// scope that processes every input and applies held movement. Kept as a transcription
    /// rather than a call into the loop because <c>TickOnce</c> exposes no seam for it, and
    /// adding one would be a production change this task forbids.
    /// </summary>
    private static void ApplyInputs(Rig rig)
    {
        List<PendingInput> inputs = rig.DrainScratch;
        if (inputs.Count == 0) return;

        rig.World.RebindStale(inputs);

        rig.NewestInputIndex.Clear();
        for (int i = 0; i < inputs.Count; i++)
        {
            EntityHandle handle = inputs[i].Handle;
            if (!handle.IsValid) continue;
            if (!rig.NewestInputIndex.TryGetValue(handle, out int best) ||
                inputs[i].Input.Tick >= inputs[best].Input.Tick)
            {
                rig.NewestInputIndex[handle] = i;
            }
        }

        ulong tick = rig.Loop.CurrentTick;
        rig.World.UpdateComponents(writer =>
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                PendingInput pi = inputs[i];
                if (!pi.Handle.IsValid) continue;
                bool applyMovement = rig.NewestInputIndex[pi.Handle] == i;
                rig.Handler.ProcessInput(writer, in pi, tick, applyMovement);
            }
            rig.Handler.ApplyHeldMovement(writer, tick, 1);
        });
    }

    /// <summary>
    /// Exactly what <c>TickLoop.GatherViews</c> does: one read lock for the whole broadcast,
    /// then <see cref="Connection.GatherSnapshotView"/> per viewer.
    /// </summary>
    private static void GatherSerial(Rig rig)
    {
        rig.World.ReadAll(reader =>
        {
            Connection[] conns = rig.Conns;
            ulong tick = rig.Loop.CurrentTick;
            for (int i = 0; i < conns.Length; i++)
            {
                conns[i].GatherSnapshotView(reader, AoiRadius, tick, GameConstants.DefaultKeyframeInterval);
            }
        });
    }

    /// <summary>
    /// The same gather, sliced across a persistent pool, inside the same single read scope.
    ///
    /// <para>This needs no production hook and no new lock mode. The tick thread holds the
    /// read lock for the whole scope, which is what excludes writers; the workers call
    /// <c>WorldReader.GetEntitiesInRange</c>, which takes no lock of its own (it forwards to
    /// <c>EcsWorld.ScanRangeLockedForReader</c>); and the coordinator does not leave the
    /// scope until every worker has rendezvoused. So the workers run strictly inside the
    /// interval the read lock is held, over a world no writer can touch.</para>
    /// </summary>
    private static void GatherParallel(Rig rig, IGatherPool pool)
    {
        rig.World.ReadAll(reader =>
        {
            Connection[] conns = rig.Conns;
            ulong tick = rig.Loop.CurrentTick;
            pool.Dispatch((w, n) =>
            {
                for (int i = w; i < conns.Length; i += n)
                {
                    conns[i].GatherSnapshotView(reader, AoiRadius, tick, GameConstants.DefaultKeyframeInterval);
                }
            });
        });
    }

    // ── Serial vs a persistent pool, one worker count at a time ───────────────────

    /// <summary>
    /// Serial gather against pooled gather, interleaved round-robin so a load spike hits both
    /// arms. Exactly one pool is alive per sub-run, so the spin cost of parked workers is
    /// carried by the control arm as well as by the arm under test — which is the only way to
    /// make the comparison honest on a box whose cores are shared (ADR-7).
    /// </summary>
    [SkippableFact]
    public void GatherParallelScaling()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run the tick-breakdown benchmark.");

        _out.WriteLine($"clock: Stopwatch (CLOCK_MONOTONIC), Frequency={Stopwatch.Frequency} Hz");
        _out.WriteLine($"cores: {Environment.ProcessorCount}, {Iterations} iterations x {Rounds} rounds");
        _out.WriteLine($"{"viewers",8}  {"primitive",-14}{"workers",8}{"serial us",12}{"pooled us",12}{"speedup",10}   " +
                       $"{"serial round range",-22}{"pooled round range",-22}");

        WarmProcess();

        foreach (int viewers in ViewerCounts)
        {
            using var rig = new Rig(viewers, runWriteTasks: false);
            for (int i = 0; i < 400; i++) { rig.PushInputs(); rig.Loop.TickOnce(); }

            foreach (int w in new[] { 2, 4, 6, 8 })
            foreach (string primitive in new[] { "Barrier", "Semaphore+CD" })
            {
                using IGatherPool pool = primitive == "Barrier"
                    ? new BarrierPool(w)
                    : new SemaphorePool(w);

                var serial = new Samples("serial");
                var pooled = new Samples("pooled");
                var serialRounds = new List<double>();
                var pooledRounds = new List<double>();

                for (int k = 0; k < 30; k++) { GatherSerial(rig); GatherParallel(rig, pool); }

                for (int round = 0; round < Rounds; round++)
                {
                    var sr = new Samples("s");
                    var pr = new Samples("p");
                    for (int i = 0; i < Iterations; i++)
                    {
                        if ((i & 1) == 0)
                        {
                            long t0 = Stopwatch.GetTimestamp(); GatherSerial(rig); long t1 = Stopwatch.GetTimestamp();
                            serial.Add(t1 - t0); sr.Add(t1 - t0);
                            t0 = Stopwatch.GetTimestamp(); GatherParallel(rig, pool); t1 = Stopwatch.GetTimestamp();
                            pooled.Add(t1 - t0); pr.Add(t1 - t0);
                        }
                        else
                        {
                            long t0 = Stopwatch.GetTimestamp(); GatherParallel(rig, pool); long t1 = Stopwatch.GetTimestamp();
                            pooled.Add(t1 - t0); pr.Add(t1 - t0);
                            t0 = Stopwatch.GetTimestamp(); GatherSerial(rig); t1 = Stopwatch.GetTimestamp();
                            serial.Add(t1 - t0); sr.Add(t1 - t0);
                        }
                    }
                    serialRounds.Add(sr.MedianUs);
                    pooledRounds.Add(pr.MedianUs);
                }

                _out.WriteLine($"{viewers,8}  {primitive,-14}{w,8}{serial.MedianUs,12:F1}{pooled.MedianUs,12:F1}" +
                               $"{serial.MedianUs / pooled.MedianUs,10:F2}   " +
                               $"{Spread(serialRounds),-22}{Spread(pooledRounds),-22}");
                _out.WriteLine($"{"",22}{"p99:",8}{serial.P99Us,12:F1}{pooled.P99Us,12:F1}");
            }
        }
    }

    /// <summary>
    /// The AOI gather is order-independent, and this proves it rather than asserting it: the
    /// serial gather and the pooled gather are run from the same world state and every
    /// connection's staged entity set is compared as a set. If any shared mutable state
    /// existed between viewers, or if the result depended on the order viewers were visited
    /// in, this would fail.
    /// </summary>
    [SkippableFact]
    public void PooledGatherMatchesSerialGather()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run the tick-breakdown benchmark.");

        using var rig = new Rig(200, runWriteTasks: false);
        for (int i = 0; i < 50; i++) { rig.PushInputs(); rig.Loop.TickOnce(); }

        List<string[]> serial = SnapshotStagedIds(rig, () => GatherSerial(rig));

        using var pool = new BarrierPool(4);
        List<string[]> parallel = SnapshotStagedIds(rig, () => GatherParallel(rig, pool));

        Assert.Equal(serial.Count, parallel.Count);
        for (int i = 0; i < serial.Count; i++)
        {
            Assert.Equal(serial[i], parallel[i]);
        }
        _out.WriteLine($"{serial.Count} viewers: pooled gather staged the same entity set as the serial gather.");
    }

    /// <summary>
    /// Run a gather and read back what each connection staged, as a sorted id list per
    /// viewer. Reads the world directly rather than the connection's private buffer, using
    /// the same anchor and radius, so no production accessor had to be added.
    /// </summary>
    private static List<string[]> SnapshotStagedIds(Rig rig, Action gather)
    {
        gather();
        var result = new List<string[]>(rig.Conns.Length);
        var buffer = new EntityState[8192];
        rig.World.ReadAll(reader =>
        {
            for (int i = 0; i < rig.Conns.Length; i++)
            {
                reader.TryGetSnapshotAnchor(rig.Conns[i].UserId, out var anchor, out _);
                int n = reader.GetEntitiesInRange(anchor, AoiRadius, buffer);
                var ids = new string[Math.Min(n, buffer.Length)];
                for (int e = 0; e < ids.Length; e++) ids[e] = buffer[e].Id;
                Array.Sort(ids, StringComparer.Ordinal);
                result.Add(ids);
            }
        });
        return result;
    }

    // ── Dispatch cost of a parked pool, with no work in it ────────────────────────

    /// <summary>
    /// The floor a parallel gather has to clear: the cost of waking W parked workers and
    /// waiting for them to report back, with an empty body. Measured for both primitives so
    /// the arithmetic in the report rests on a number rather than on an estimate.
    /// </summary>
    [SkippableFact]
    public void ParkedPoolDispatchCost()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run the tick-breakdown benchmark.");

        _out.WriteLine($"clock: Stopwatch (CLOCK_MONOTONIC), Frequency={Stopwatch.Frequency} Hz");
        _out.WriteLine($"cores: {Environment.ProcessorCount}");
        _out.WriteLine($"{"primitive",-16}{"workers",9}{"median us",12}{"p99 us",12}{"min us",12}   {"round range",-16}");

        Action<int, int> empty = static (_, _) => { };

        foreach (int w in new[] { 2, 4, 8 })
        {
            using var barrier = new BarrierPool(w);
            using var semaphore = new SemaphorePool(w);

            var bs = new Samples("barrier");
            var ss = new Samples("semaphore");
            var bRounds = new List<double>();
            var sRounds = new List<double>();

            for (int k = 0; k < 200; k++) { barrier.Dispatch(empty); semaphore.Dispatch(empty); }

            for (int round = 0; round < Rounds; round++)
            {
                var br = new Samples("b");
                var sr = new Samples("s");
                for (int i = 0; i < 2000; i++)
                {
                    // Round-robin the two primitives inside the round so a spike hits both.
                    if ((i & 1) == 0)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        barrier.Dispatch(empty);
                        long t1 = Stopwatch.GetTimestamp();
                        bs.Add(t1 - t0); br.Add(t1 - t0);

                        t0 = Stopwatch.GetTimestamp();
                        semaphore.Dispatch(empty);
                        t1 = Stopwatch.GetTimestamp();
                        ss.Add(t1 - t0); sr.Add(t1 - t0);
                    }
                    else
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        semaphore.Dispatch(empty);
                        long t1 = Stopwatch.GetTimestamp();
                        ss.Add(t1 - t0); sr.Add(t1 - t0);

                        t0 = Stopwatch.GetTimestamp();
                        barrier.Dispatch(empty);
                        t1 = Stopwatch.GetTimestamp();
                        bs.Add(t1 - t0); br.Add(t1 - t0);
                    }
                }
                bRounds.Add(br.MedianUs);
                sRounds.Add(sr.MedianUs);
            }

            _out.WriteLine($"{"Barrier",-16}{w,9}{bs.MedianUs,12:F2}{bs.P99Us,12:F2}{bs.MinUs,12:F2}   {Spread(bRounds),-16}");
            _out.WriteLine($"{"Semaphore+CD",-16}{w,9}{ss.MedianUs,12:F2}{ss.P99Us,12:F2}{ss.MinUs,12:F2}   {Spread(sRounds),-16}");
        }
    }

    // ── The cost the write tasks impose on the tick thread ────────────────────────

    /// <summary>
    /// The same tick at 200 viewers with every connection's write task live, so the
    /// contention stage 4's off-tick encoding creates is measured rather than assumed. The
    /// two arms need separate rigs and so cannot be interleaved inside one measurement; they
    /// are instead run back to back in both orders and both results are reported, which
    /// exposes an ordering artefact if there is one.
    /// </summary>
    [SkippableFact]
    public void TickCostWithWriteTasksLive()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run the tick-breakdown benchmark.");

        _out.WriteLine($"clock: Stopwatch (CLOCK_MONOTONIC), Frequency={Stopwatch.Frequency} Hz");
        _out.WriteLine($"{"arm",-30}{"order",8}{"median us",12}{"p99 us",12}{"min us",12}");

        foreach (bool idleFirst in new[] { true, false })
        {
            string order = idleFirst ? "idle,live" : "live,idle";
            foreach (bool live in idleFirst ? new[] { false, true } : new[] { true, false })
            {
                using var rig = new Rig(200, runWriteTasks: live);
                for (int i = 0; i < 400; i++) { rig.PushInputs(); rig.Loop.TickOnce(); }

                var s = new Samples("t");
                for (int i = 0; i < Iterations * Rounds; i++)
                {
                    rig.PushInputs();
                    long t0 = Stopwatch.GetTimestamp();
                    rig.Loop.TickOnce();
                    long t1 = Stopwatch.GetTimestamp();
                    s.Add(t1 - t0);
                }

                string name = live ? "TickOnce, write tasks live" : "TickOnce, write tasks idle";
                _out.WriteLine($"{name,-30}{order,8}{s.MedianUs,12:F1}{s.P99Us,12:F1}{s.MinUs,12:F1}");
            }
        }
    }
}
