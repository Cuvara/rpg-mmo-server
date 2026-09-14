using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GameServer.World;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.World;

/// <summary>
/// Deterministic reproducer for issue #176 — the <see cref="NullReferenceException"/> that
/// came out of <c>Arch.Core.QueryArchetypeEnumerator.MoveNext()</c> inside
/// <c>EcsWorld.ScanRangeLocked</c> while the read lock was held, and that twice corrupted
/// the heap badly enough to take the test host down with an
/// <see cref="AccessViolationException"/> several tests later.
///
/// <para><b>Why a separate harness.</b> The failure never reproduced from
/// <c>EcsWorldTests.ConcurrentAccess_NoDeadlock</c> on demand: that test needs the whole
/// suite's incidental scheduler pressure to interleave its ten threads, so it fired at
/// roughly 3/18 full-suite runs and 0/6 in isolation. A fix validated against a rate like
/// that is indistinguishable from luck. This harness drives the same code path directly,
/// hard enough that the defect fires in seconds rather than in one run out of six.</para>
///
/// <para><b>What it actually attacks.</b> Not a data race on <c>EcsWorld</c>'s own state —
/// the reader/writer lock covers that. It attacks the fact that Arch's <i>read</i> path
/// mutates the world: <c>Arch.Core.World.Query(in QueryDescription)</c> memoises into a
/// shared <c>Dictionary&lt;QueryDescription, Query&gt;</c>, and the <c>Query</c> object it
/// returns lazily rebuilds its own matching-archetype list the first time it is iterated
/// after the archetype set changed. Both are writes, both happen with only the <i>read</i>
/// lock held, and both are shared between every concurrent reader. See
/// <see cref="EcsWorld"/>'s query-cache remarks and issue #176.</para>
///
/// <para><b>Gating.</b> Skipped unless <c>ECS_STRESS=1</c>, like the <c>BENCH_*</c>
/// harnesses, so CI wall time is unaffected. Run it with:</para>
/// <code>
/// ECS_STRESS=1 dotnet test --filter FullyQualifiedName~EcsWorldConcurrencyStress
/// </code>
///
/// <para><b>Clock.</b> <see cref="Stopwatch"/> only — this host's <c>CLOCK_REALTIME</c>
/// runs 10-17% fast with unstable skew (issue #153).</para>
/// </summary>
public class EcsWorldConcurrencyStress
{
    private readonly ITestOutputHelper _out;

    public EcsWorldConcurrencyStress(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("ECS_STRESS") == "1";

    /// <summary>How many independent attempts each scenario makes before it gives up.</summary>
    private static int Rounds =>
        int.TryParse(Environment.GetEnvironmentVariable("ECS_STRESS_ROUNDS"), out int r) && r > 0 ? r : 2000;

    /// <summary>
    /// How long one round runs. Short and many rather than long and few, deliberately: the
    /// window is a <b>young</b> world — cold query cache, archetype set still growing — and
    /// each round gets a fresh one, so rounds, not seconds, are the unit of exposure.
    /// Making a round longer buys almost nothing; making rounds more numerous buys linearly.
    /// </summary>
    private static int MillisPerRound =>
        int.TryParse(Environment.GetEnvironmentVariable("ECS_STRESS_MS"), out int m) && m > 0 ? m : 3;

    private void Line(string s)
    {
        _out.WriteLine(s);
        Console.WriteLine(s);
    }

    // ------------------------------------------------------------------ scenarios

    /// <summary>
    /// The shape <c>ConcurrentAccess_NoDeadlock</c> has, concentrated: readers running the
    /// two <i>different</i> query descriptions (<c>AllEntities</c> via the AOI scan and
    /// <c>Players</c> via <see cref="EcsWorld.PlayerStates"/>) while writers churn the
    /// archetype set underneath them.
    ///
    /// <para>Two distinct descriptions matter. One description alone races only on the
    /// lazy rebuild of a single cached <c>Query</c>; two race on that <i>and</i> on the
    /// insert into the world's shared query-cache dictionary.</para>
    ///
    /// <para>This is also the real production pairing, not a test-only one:
    /// <c>AsyncSaver.SaveAllAsync</c> calls <see cref="EcsWorld.PlayerStates"/> from the
    /// save timer while the tick thread is inside its AOI gather.</para>
    /// </summary>
    [SkippableFact]
    public void MixedReaders_WithWriters_DoNotFaultInsideArch()
    {
        Skip.IfNot(Enabled, "Set ECS_STRESS=1 to run.");
        RunAndReport("mixed readers + writers", () => MixedRound(readers: 8, writers: 3, millis: MillisPerRound));
    }

    /// <summary>
    /// The parallel AOI gather on its own: one read region, several workers, every worker
    /// scanning. No writer runs at all, and it still faults — which is the point. If the
    /// hazard were a reader/writer race the read lock would be the wrong shape but the
    /// primitive would be right; that readers alone corrupt each other says the read path
    /// is not a read.
    /// </summary>
    [SkippableFact]
    public void ParallelReadRegion_Alone_DoesNotFaultInsideArch()
    {
        Skip.IfNot(Enabled, "Set ECS_STRESS=1 to run.");
        RunAndReport("parallel read region, no writers", () => ParallelGatherRound(workers: 8, millis: MillisPerRound));
    }

    // ------------------------------------------------------------------ driver

    private void RunAndReport(string label, Func<Exception?> round)
    {
        int rounds = Rounds;
        var failures = new List<Exception>();
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < rounds; i++)
        {
            if (round() is { } ex) failures.Add(ex);
        }

        sw.Stop();
        Line($"[{label}] {failures.Count}/{rounds} rounds faulted in {sw.ElapsedMilliseconds} ms");

        var seen = new HashSet<string>();
        foreach (var ex in failures)
        {
            string key = ex.GetType().FullName + "|" + FirstFrame(ex);
            if (seen.Add(key)) Line($"  {ex.GetType().Name} at {FirstFrame(ex)}");
        }

        Assert.Empty(failures);
    }

    private static string FirstFrame(Exception ex)
    {
        string? trace = ex.StackTrace;
        if (string.IsNullOrEmpty(trace)) return "<no stack>";
        int nl = trace.IndexOf('\n');
        return (nl < 0 ? trace : trace.Substring(0, nl)).Trim();
    }

    // ------------------------------------------------------------------ rounds

    /// <summary>
    /// One attempt at the mixed shape. Returns the first exception any thread saw, or null.
    ///
    /// <para>A fresh world per round, and the archetypes introduced <b>after</b> the
    /// readers are already spinning. Both matter. The query cache is only written on a
    /// miss, and a query's memoised archetype list is only invalidated when a
    /// <i>new archetype</i> appears — measured: adding and removing entities of an
    /// archetype that already exists never invalidates it. So the window is not "while
    /// writers run", it is "at the moments the world's archetype set grows", and this
    /// server's set grows to three (player, mob, enemy) and then stops. Seeding all three
    /// up front and then churning entities — what the first version of this harness did —
    /// leaves only the cold-cache window: it fired 6 times in 1500 rounds. Introducing the
    /// archetypes under load raised that to roughly 1-2% of rounds, which is 10-38 faults
    /// per 2000-round run and, more usefully, a fault in every run.</para>
    ///
    /// <para>That is also the honest production shape. The window is world start-up and
    /// first-of-a-kind spawns — which is exactly when players are joining.</para>
    /// </summary>
    private static Exception? MixedRound(int readers, int writers, int millis)
    {
        using var world = new EcsWorld(Math.Max(1, readers));

        // Players only: the mob and enemy archetypes are created by the writer below,
        // while the readers are mid-scan.
        for (int i = 0; i < 40; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", x: i % 100, y: i % 100));
        }

        return RunThreads(readers + writers, millis, (index, deadline, token) =>
        {
            if (index < readers)
            {
                var buffer = new EntityState[256];
                while (Stopwatch.GetTimestamp() < deadline && !token.IsFaulted)
                {
                    // Two different descriptions: the AOI scan uses AllEntities and
                    // PlayerStates uses Players. They share one query-cache dictionary.
                    world.GetEntitiesInRange(new Vec2(0, 0), 500f, buffer);
                    world.PlayerStates();
                }
            }
            else
            {
                int w = index - readers;
                long n = 0;

                // The archetype-creating writes, first thing after the barrier.
                world.AddEntity(TestHelpers.CreateMob($"mob{w}", x: 1, y: 1));
                world.Spawn(TestHelpers.CreateMob($"foe{w}", x: 2, y: 2), EntityTags.EnemyAi);

                while (Stopwatch.GetTimestamp() < deadline && !token.IsFaulted)
                {
                    string id = $"w{w}_{n}";
                    world.AddEntity((n & 1) == 0
                        ? TestHelpers.CreatePlayer(id, x: n % 100, y: n % 100)
                        : TestHelpers.CreateMob(id, x: n % 100, y: n % 100));
                    world.RemoveEntity(id);
                    n++;
                }
            }
        });
    }

    /// <summary>
    /// One attempt at the parallel-gather shape: <see cref="EcsWorld.ReadAllParallel"/>
    /// with every worker scanning, called in a loop, with nothing writing.
    /// </summary>
    private static Exception? ParallelGatherRound(int workers, int millis)
    {
        using var world = new EcsWorld(workers);
        for (int i = 0; i < 200; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", x: i % 100, y: i % 100));
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (millis / 1000.0));
        var buffers = new EntityState[workers][];
        for (int i = 0; i < workers; i++) buffers[i] = new EntityState[256];

        // The archetype set grows twice, between regions, so the region that follows each
        // growth finds every read query's memo stale and has N workers rebuild it at once.
        int step = 0;

        try
        {
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (step == 1) world.AddEntity(TestHelpers.CreateMob("mob", x: 1, y: 1));
                if (step == 2) world.Spawn(TestHelpers.CreateMob("foe", x: 2, y: 2), EntityTags.EnemyAi);
                step++;

                world.ReadAllParallel(workers, (reader, slot) =>
                {
                    for (int k = 0; k < 8; k++)
                    {
                        reader.GetEntitiesInRange(new Vec2(slot, slot), 500f, buffers[slot]);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            return ex;
        }

        return null;
    }

    // ------------------------------------------------------------------ plumbing

    private sealed class FaultToken
    {
        private Exception? _first;
        public bool IsFaulted => Volatile.Read(ref _first) is not null;
        public Exception? First => Volatile.Read(ref _first);
        public void Record(Exception ex) => Interlocked.CompareExchange(ref _first, ex, null);
    }

    /// <summary>
    /// Start <paramref name="count"/> dedicated threads on <paramref name="body"/> and join
    /// them. Dedicated threads rather than the pool: the pool would throttle the ramp-up
    /// and the whole point is that every thread is inside the world at the same instant.
    /// </summary>
    private static Exception? RunThreads(int count, int millis, Action<int, long, FaultToken> body)
    {
        var token = new FaultToken();
        long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (millis / 1000.0));
        var threads = new Thread[count];
        var barrier = new Barrier(count);

        for (int i = 0; i < count; i++)
        {
            int index = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    body(index, deadline, token);
                }
                catch (Exception ex) { token.Record(ex); }
            })
            { IsBackground = true, Name = $"ecs-stress-{index}" };
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        return token.First;
    }

}
