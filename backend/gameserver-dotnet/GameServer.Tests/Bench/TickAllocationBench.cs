using GameServer.Net.Transport;
using GameServer.Scaffolding;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Bench;

/// <summary>
/// A committed, re-runnable measurement of <b>allocated bytes</b> on the tick path — the
/// proof metric the no-allocation rule in <c>CLAUDE.md</c> ("No allocations in hot paths")
/// otherwise has no instrument for.
///
/// <para><b>Why bytes and not time.</b> Every duration measured on this host is a lower
/// bound of unknown tightness: tick p99 swings 3.3× with co-tenant load (ADR-7), so a
/// timing benchmark cannot prove an allocation fix did anything. Allocated bytes are
/// deterministic — <see cref="GC.GetAllocatedBytesForCurrentThread"/> counts exactly what
/// this thread allocated, and the same code over the same world allocates the same bytes
/// on a quiet box and a thrashing one. This is the same reasoning that made bandwidth the
/// quotable number in BENCHMARK.md Parts II–IV.</para>
///
/// <para><b>What is measured.</b> The same rig and the same entry points as
/// <see cref="TickBreakdownBench"/>: <c>TickOnce</c> whole, then its phases separately
/// (gather, input drain, input apply, enemy AI, structural drain, viewer copy), each
/// wrapped in a per-iteration allocation delta so a once-per-wave spike is
/// distinguishable from a steady per-tick cost — the report carries mean bytes/iteration,
/// the number of zero-allocation iterations, and the largest single iteration. A final
/// arm walks the write task's encode path (claim → <c>SnapshotDeltaState.Encode</c> →
/// <c>SnapshotFrameWriter.WriteFrame</c>) on the measuring thread, because that path runs
/// off-tick in production and would otherwise be invisible to a tick-thread counter.</para>
///
/// <para><b>Inputs carry combat.</b> Every viewer sends a movement vector per tick and
/// every fourth also targets another player, so the attack branch — compose, validate,
/// reject-or-damage — is on the measured path. <c>TickBreakdownBench</c> pushes movement
/// only; an allocation audit that skipped the combat branch would miss exactly the class
/// of defect #249 fixed there (interpolated rejection strings, unguarded debug logs).</para>
///
/// <para><b>Caveats.</b> Thread-local counting means work that production moves to other
/// threads (write tasks, the death drain, Redis publish) is only seen where an arm runs
/// it inline deliberately. <c>metrics</c> is null, matching <see cref="TickBreakdownBench"/>;
/// <see cref="GameServer.Observability.GameMetrics"/> records through pre-built
/// <c>TagList</c>s and is designed alloc-free, but that is asserted here, not measured.
/// Gated like the other benches so it never runs in CI:
/// <code>BENCH_TICK=1 dotnet test --filter FullyQualifiedName~TickAllocationBench \
///   --logger "console;verbosity=detailed"</code></para>
/// </summary>
public sealed class TickAllocationBench
{
    private readonly ITestOutputHelper _out;

    public TickAllocationBench(ITestOutputHelper output) => _out = output;

    private const string EnvVar = "BENCH_TICK";

    private const int TickRate = GameConstants.DefaultTickRate;
    private const float AoiRadius = GameConstants.DefaultAoiRadius;

    /// <summary>Measured iterations per arm. Enough to cover many enemy-AI wave spawns
    /// (one wave per 1.5s of simulated time = one per 22-23 ticks at 15Hz) so amortized
    /// structural allocation is represented, not aliased.</summary>
    private const int Iterations = 2000;

    /// <summary>
    /// Warm-up ticks before anything is measured, overridable with
    /// <c>BENCH_ALLOC_WARMUP</c>. The default covers JIT promotion and buffer growth; a
    /// long override (e.g. 10000) is how "ramp allocation" is told apart from
    /// "steady-state allocation" — a source that disappears under a long warm-up was
    /// high-water growth (AOI buffers, dictionaries), not a per-tick cost.
    /// </summary>
    private static readonly int WarmupTicks =
        int.TryParse(Environment.GetEnvironmentVariable("BENCH_ALLOC_WARMUP"), out int w) && w > 0
            ? w : 600;

    private static readonly int[] ViewerCounts = { 50, 200, 500 };

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

        /// <summary>One frame writer per connection, mirroring the per-connection
        /// <c>Connection._frameWriter</c> the write task owns (private there; the bench
        /// keeps its own so no production seam is added for measurement).</summary>
        public readonly GameServer.Snapshot.SnapshotFrameWriter[] FrameWriters;

        /// <summary>Attack target per viewer, precomputed so pushing inputs allocates
        /// nothing in the measured window's setup. Every 4th viewer attacks its
        /// index-neighbour; the rest carry null.</summary>
        private readonly string?[] _attackTargets;

        /// <summary>The gather callback, built once — the same discipline as
        /// <c>TickLoop._gatherViews</c>. An inline lambda at the call site would charge
        /// the arm ~88 B/call of the bench's own closure, which is exactly the
        /// misattribution this harness exists to avoid.</summary>
        public readonly Action<GameServer.World.WorldReader> GatherCallback;

        private ulong _syntheticTick = 1;

        /// <summary>Per-frame attribution inside the encode arm: keyframes and deltas
        /// allocate for different reasons, and an aggregate per-iteration figure hides
        /// which one is paying.</summary>
        public long KeyframeBytes, KeyframeFrames, KeyframeMax;
        public long DeltaBytes, DeltaFrames, DeltaMax;

        public Rig(int players)
        {
            Handler = new InputHandler(World, NullLogger.Instance, null, TickRate, MapBounds.Default);
            Phase = new EnemySpawner(World, TickRate, NullLogger.Instance);
            Loop = new TickLoop(World, Handler, Connections, TickRate, AoiRadius,
                NullLogger.Instance, metrics: null,
                keyframeInterval: GameConstants.DefaultKeyframeInterval, simulationPhase: Phase);

            // Same deterministic disc placement as TickBreakdownBench, so the AOI
            // occupancy — and with it the encode arm's per-viewer entity count — matches
            // the numbers Part VII and the breakdown bench already report.
            var rng = new Random(20260819);
            Conns = new Connection[players];
            FrameWriters = new GameServer.Snapshot.SnapshotFrameWriter[players];
            _attackTargets = new string?[players];
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
                FrameWriters[i] = new GameServer.Snapshot.SnapshotFrameWriter();

                _attackTargets[i] = i % 4 == 0 ? $"p{(i + 1) % players}" : null;
            }

            ViewerScratch = new Connection[players * 2];
            GatherCallback = GatherAll;
        }

        private void GatherAll(GameServer.World.WorldReader reader)
        {
            ulong tick = Loop.CurrentTick;
            for (int i = 0; i < Conns.Length; i++)
            {
                Conns[i].GatherSnapshotView(reader, AoiRadius, tick, GameConstants.DefaultKeyframeInterval);
            }
        }

        /// <summary>The input-apply scope body, entered through the state overload with a
        /// static lambda — byte-for-byte the shape <c>TickLoop.TickOnce</c> uses, so the
        /// arm measures the production path and not a bench closure (a capturing lambda
        /// here measures 104 B/call, the figure TickLoop's own comment records).</summary>
        public void ApplyInputBatch()
        {
            List<PendingInput> inputs = DrainScratch;
            if (inputs.Count == 0) return;

            World.RebindStale(inputs);

            NewestInputIndex.Clear();
            for (int i = 0; i < inputs.Count; i++)
            {
                EntityHandle handle = inputs[i].Handle;
                if (!handle.IsValid) continue;
                if (!NewestInputIndex.TryGetValue(handle, out int best) ||
                    inputs[i].Input.Tick >= inputs[best].Input.Tick)
                {
                    NewestInputIndex[handle] = i;
                }
            }

            World.UpdateComponents(this, static (self, writer) =>
            {
                List<PendingInput> inputs = self.DrainScratch;
                ulong tick = self.Loop.CurrentTick;
                for (int i = 0; i < inputs.Count; i++)
                {
                    PendingInput pi = inputs[i];
                    if (!pi.Handle.IsValid) continue;
                    bool applyMovement = self.NewestInputIndex[pi.Handle] == i;
                    self.Handler.ProcessInput(writer, in pi, tick, applyMovement);
                }
                self.Handler.ApplyHeldMovement(writer, tick);
            });
        }

        /// <summary>One input per viewer: movement for all, an attack for every 4th.</summary>
        public void PushInputs()
        {
            ulong t = Loop.CurrentTick + 1;
            for (int i = 0; i < Conns.Length; i++)
            {
                World.PushInput(Conns[i].UserId, new InputData(t, 1f, 0f, _attackTargets[i]));
            }
        }

        public ulong NextSyntheticTick() => _syntheticTick++;

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
        }
    }

    /// <summary>Per-iteration allocation deltas for one arm.</summary>
    private sealed class AllocSamples
    {
        public string Name { get; }
        public long TotalBytes;
        public long MaxBytes;
        public int ZeroIterations;
        public int Iterations;

        public AllocSamples(string name) => Name = name;

        public void Add(long bytes)
        {
            Iterations++;
            TotalBytes += bytes;
            if (bytes > MaxBytes) MaxBytes = bytes;
            if (bytes == 0) ZeroIterations++;
        }

        public double MeanBytes => Iterations == 0 ? 0 : TotalBytes / (double)Iterations;
    }

    [SkippableFact]
    public void TickAllocationBreakdown()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run the tick-allocation benchmark.");

        _out.WriteLine($"counter: GC.GetAllocatedBytesForCurrentThread (thread-exact), " +
                       $"rate: uniform {TickRate}Hz, AOI radius {AoiRadius}, " +
                       $"warmup {WarmupTicks} ticks, {Iterations} measured iterations per arm, " +
                       $"GC {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}");
        _out.WriteLine("");

        foreach (int viewers in ViewerCounts) RunBreakdown(viewers);
    }

    private void RunBreakdown(int viewers)
    {
        using var rig = new Rig(viewers);

        // Warm up: enemy population to steady state, AOI/frame buffers to final size,
        // dictionaries past their growth, tiered JIT promoted (a tier-up recompilation
        // allocates on this thread and would be misattributed to the arm it landed in).
        for (int i = 0; i < WarmupTicks; i++)
        {
            rig.PushInputs();
            rig.Loop.TickOnce();
            DrainStagedSnapshots(rig);
        }

        double occupancy = rig.MeanAoiOccupancy();
        int entities = rig.World.EntityCount;

        var arms = new (string Name, Action Setup, Action Run)[]
        {
            ("TickOnce (whole)",          rig.PushInputs,      () => rig.Loop.TickOnce()),
            ("AOI gather (serial)",       () => { },           () => GatherSerial(rig)),
            ("Input drain",               rig.PushInputs,      () => rig.World.DrainInputs(rig.DrainScratch)),
            ("Input apply (write scope)", () => DrainFor(rig), () => rig.ApplyInputBatch()),
            ("Enemy AI (RunDue)",         () => { },           () => rig.Phase.Tick(rig.NextSyntheticTick())),
            ("ApplyStructuralChanges",    () => { },           () => rig.World.ApplyStructuralChanges()),
            ("ConnectionManager.CopyTo",  () => { },           () => rig.Connections.CopyTo(rig.ViewerScratch)),
            // The write task's share, run inline so the thread-local counter sees it.
            // Setup stages fresh jobs by ticking once (unmeasured).
            ("Encode+frame (write path)", () => { rig.PushInputs(); rig.Loop.TickOnce(); },
                                          () => DrainStagedSnapshots(rig)),
        };

        var samples = new AllocSamples[arms.Length];
        for (int a = 0; a < arms.Length; a++) samples[a] = new AllocSamples(arms[a].Name);

        // Untimed warm pass over every arm, so an arm-specific lazy path (first delta
        // after a keyframe, first structural drain) is paid before measurement.
        for (int a = 0; a < arms.Length; a++)
        {
            for (int k = 0; k < 30; k++) { arms[a].Setup(); arms[a].Run(); }
        }

        // Per-iteration series for the whole-tick arm, so a spike can be located and
        // its cadence read off (wave spawns land every WaveIntervalSec of world time).
        var wholeTickSeries = new long[Iterations];

        for (int i = 0; i < Iterations; i++)
        {
            for (int a = 0; a < arms.Length; a++)
            {
                arms[a].Setup();
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                arms[a].Run();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - b0;
                samples[a].Add(bytes);
                if (a == 0) wholeTickSeries[i] = bytes;
            }
        }

        _out.WriteLine($"-- {viewers} viewers -- entities in world: {entities}, " +
                       $"mean AOI occupancy: {occupancy:F1} entities/viewer");
        _out.WriteLine($"{"phase",-30}{"mean B/iter",14}{"max B/iter",14}{"zero iters",14}{"total B",14}");
        for (int a = 0; a < samples.Length; a++)
        {
            var s = samples[a];
            _out.WriteLine($"{s.Name,-30}{s.MeanBytes,14:F1}{s.MaxBytes,14}" +
                           $"{s.ZeroIterations + "/" + s.Iterations,14}{s.TotalBytes,14}");
        }

        if (rig.KeyframeFrames + rig.DeltaFrames > 0)
        {
            _out.WriteLine(
                $"encode arm per frame: keyframe {rig.KeyframeBytes / (double)Math.Max(rig.KeyframeFrames, 1):F1} B " +
                $"(n={rig.KeyframeFrames}, max {rig.KeyframeMax}), " +
                $"delta {rig.DeltaBytes / (double)Math.Max(rig.DeltaFrames, 1):F1} B " +
                $"(n={rig.DeltaFrames}, max {rig.DeltaMax})");
        }

        // The largest whole-tick iterations, with their indices: reading the spacing
        // between them is how a periodic source (a wave spawn, a buffer regrow tier)
        // is told apart from a per-tick one.
        var spikes = new List<(long Bytes, int Index)>();
        for (int i = 0; i < Iterations; i++)
        {
            if (wholeTickSeries[i] > 0) spikes.Add((wholeTickSeries[i], i));
        }
        spikes.Sort((x, y) => y.Bytes.CompareTo(x.Bytes));
        int top = Math.Min(10, spikes.Count);
        if (top > 0)
        {
            _out.WriteLine($"TickOnce spikes (top {top} of {spikes.Count} nonzero iterations):");
            for (int i = 0; i < top; i++)
            {
                _out.WriteLine($"  iter {spikes[i].Index,5}: {spikes[i].Bytes} B");
            }
        }
        _out.WriteLine("");
    }

    /// <summary>
    /// Per-region allocation of the parallel read/write dispatch, in isolation. This is
    /// the path the serial breakdown above cannot see: the tick loop only enters
    /// <see cref="EcsWorld.ReadAllParallel"/> at 500+ viewers with <c>--gather-workers</c>
    /// configured, and in that configuration it runs once per broadcast tick — so a
    /// per-region allocation here is a per-tick allocation in exactly the configuration
    /// that exists to protect the tick budget.
    /// </summary>
    [SkippableFact]
    public void ParallelRegionAllocationMicro()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run the tick-allocation benchmark.");

        const int Calls = 2000;

        using var world = new EcsWorld(maxWorkerSlots: 4);
        for (int i = 0; i < 64; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", x: i, y: 0f, speed: 4f));
        }

        var buffer = new GameServer.World.EntityView[128];
        Action<GameServer.World.WorldReader, int> readBody = (reader, slot) =>
        {
            if (slot == 0) _ = reader.GetEntitiesInRange(default, AoiRadius, buffer);
        };
        Action<GameServer.World.WorldWriter, int> writeBody = static (_, _) => { };

        // Warm both paths: pool threads started, JIT promoted.
        for (int i = 0; i < 100; i++)
        {
            world.ReadAllParallel(2, readBody);
            world.UpdateComponentsParallel(2, writeBody);
        }

        long readTotal = 0, readMax = 0, writeTotal = 0, writeMax = 0;
        for (int i = 0; i < Calls; i++)
        {
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            world.ReadAllParallel(2, readBody);
            long d = GC.GetAllocatedBytesForCurrentThread() - b0;
            readTotal += d;
            if (d > readMax) readMax = d;

            b0 = GC.GetAllocatedBytesForCurrentThread();
            world.UpdateComponentsParallel(2, writeBody);
            d = GC.GetAllocatedBytesForCurrentThread() - b0;
            writeTotal += d;
            if (d > writeMax) writeMax = d;
        }

        // Control arms: the one-worker forms run inline with no pool dispatch, so the
        // difference against the two-worker arms is the dispatch's own allocation.
        long read1Total = 0, read1Max = 0, write1Total = 0, write1Max = 0;
        for (int i = 0; i < Calls; i++)
        {
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            world.ReadAllParallel(1, readBody);
            long d = GC.GetAllocatedBytesForCurrentThread() - b0;
            read1Total += d;
            if (d > read1Max) read1Max = d;

            b0 = GC.GetAllocatedBytesForCurrentThread();
            world.UpdateComponentsParallel(1, writeBody);
            d = GC.GetAllocatedBytesForCurrentThread() - b0;
            write1Total += d;
            if (d > write1Max) write1Max = d;
        }

        _out.WriteLine($"counter: GC.GetAllocatedBytesForCurrentThread, {Calls} regions per arm");
        _out.WriteLine($"{"arm",-36}{"mean B/region",16}{"max B/region",16}");
        _out.WriteLine($"{"ReadAllParallel (2 workers)",-36}{readTotal / (double)Calls,16:F1}{readMax,16}");
        _out.WriteLine($"{"UpdateComponentsParallel (2 workers)",-36}{writeTotal / (double)Calls,16:F1}{writeMax,16}");
        _out.WriteLine($"{"ReadAllParallel (1, inline)",-36}{read1Total / (double)Calls,16:F1}{read1Max,16}");
        _out.WriteLine($"{"UpdateComponentsParallel (1, inline)",-36}{write1Total / (double)Calls,16:F1}{write1Max,16}");
    }

    /// <summary>
    /// Per-call allocation of the write path's two pieces in isolation, over a synthetic
    /// 16-entity view: a delta with nothing changed, a delta with every position changed,
    /// a keyframe, and the frame serialization. This is the arm that attributes the
    /// aggregate "Encode+frame" figure above to its source.
    /// </summary>
    [SkippableFact]
    public void EncodeFramePathMicro()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run the tick-allocation benchmark.");

        const int Entities = 16;
        const int Calls = 2000;

        var ids = new string[Entities];
        for (int i = 0; i < Entities; i++) ids[i] = $"p{i}";

        var views = new GameServer.World.EntityView[Entities];
        void Fill(float dx)
        {
            for (int i = 0; i < Entities; i++)
            {
                views[i] = new GameServer.World.EntityView(
                    i + 1, ids[i], "player", new Vec2(i * 10f + dx, 0f), 100, 100, 4f);
            }
        }

        var state = new GameServer.Snapshot.SnapshotDeltaState(0);
        var writer = new GameServer.Snapshot.SnapshotFrameWriter();

        // Warm: first keyframe, pool growth, buffer growth, JIT.
        Fill(0f);
        for (int i = 0; i < 50; i++)
        {
            state.RequestFull();
            var m = state.Encode((ulong)i + 1, (ulong)i + 1, views, keyframeInterval: 0, intern: true);
            _ = writer.WriteFrame((byte)RpgMmo.Wire.V1.MsgType.Snapshot, m);
        }

        var results = new List<(string Name, long Total, long Max)>();
        ulong tick = 1000;

        void Measure(string name, Func<RpgMmo.Wire.V1.SnapshotMessage?> call)
        {
            long total = 0, max = 0;
            for (int i = 0; i < Calls; i++)
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                _ = call();
                long d = GC.GetAllocatedBytesForCurrentThread() - b0;
                total += d;
                if (d > max) max = d;
            }
            results.Add((name, total, max));
        }

        // Delta, nothing changed since last send.
        Measure("Encode delta (unchanged)", () =>
            state.Encode(++tick, tick, views, keyframeInterval: int.MaxValue, intern: true));

        // Delta, every entity's position changed each call.
        Measure("Encode delta (all changed)", () =>
        {
            Fill(tick * 0.25f);
            return state.Encode(++tick, tick, views, keyframeInterval: int.MaxValue, intern: true);
        });

        // Keyframe every call.
        Measure("Encode keyframe", () =>
        {
            state.RequestFull();
            return state.Encode(++tick, tick, views, keyframeInterval: int.MaxValue, intern: true);
        });

        // Frame serialization of the last message alone.
        var last = state.Encode(++tick, tick, views, keyframeInterval: int.MaxValue, intern: true);
        Measure("WriteFrame", () =>
        {
            _ = writer.WriteFrame((byte)RpgMmo.Wire.V1.MsgType.Snapshot, last);
            return null;
        });

        _out.WriteLine($"counter: GC.GetAllocatedBytesForCurrentThread, {Entities} entities, " +
                       $"{Calls} calls per arm");
        _out.WriteLine($"{"arm",-28}{"mean B/call",14}{"max B/call",14}");
        foreach (var r in results)
        {
            _out.WriteLine($"{r.Name,-28}{r.Total / (double)Calls,14:F1}{r.Max,14}");
        }
    }

    /// <summary>
    /// The write task's snapshot path, transcribed from <c>Connection.WriteLoopAsync</c>'s
    /// marker branch minus the socket write: claim the staged job, delta-encode it,
    /// serialize it into the connection's reused frame buffers. Run inline so
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> can see it; in production each
    /// connection does exactly this on its own write task.
    /// </summary>
    private static void DrainStagedSnapshots(Rig rig)
    {
        for (int i = 0; i < rig.Conns.Length; i++)
        {
            Connection conn = rig.Conns[i];
            if (!conn.TakePendingSnapshot(out var buffer, out int count, out ulong tick,
                                          out ulong ackTick, out int keyframeInterval))
            {
                continue;
            }

            long b0 = GC.GetAllocatedBytesForCurrentThread();
            var snapshot = conn.DeltaState.Encode(
                tick, ackTick, buffer.AsSpan(0, count), keyframeInterval, intern: true);
            _ = rig.FrameWriters[i].WriteFrame((byte)RpgMmo.Wire.V1.MsgType.Snapshot, snapshot);
            long d = GC.GetAllocatedBytesForCurrentThread() - b0;

            if (snapshot.Full)
            {
                rig.KeyframeBytes += d;
                rig.KeyframeFrames++;
                if (d > rig.KeyframeMax) rig.KeyframeMax = d;
            }
            else
            {
                rig.DeltaBytes += d;
                rig.DeltaFrames++;
                if (d > rig.DeltaMax) rig.DeltaMax = d;
            }
        }
    }

    private static void DrainFor(Rig rig)
    {
        rig.PushInputs();
        rig.World.DrainInputs(rig.DrainScratch);
    }

    private static void GatherSerial(Rig rig) => rig.World.ReadAll(rig.GatherCallback);
}
