using GameServer.Net;
using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Bench;

/// <summary>
/// What would importance-driven replication intervals actually save, measured through
/// the REAL <see cref="SnapshotDeltaState"/>, before any of it is built.
///
/// <para><b>Why this has to exist before the feature.</b> The baseline sweep
/// (BENCHMARK.md Part XIII) established that bytes per entity per snapshot are flat at
/// ~24.9 across a 4x population range, so a client's downlink is exactly
/// <c>AOI population x 24.9 x SIM_WORLD_HZ</c>. Every proposed saving therefore has to
/// come out of the population term — and the population the published ceiling was
/// measured on is 200 players standing on top of each other, where a distance- or
/// type-tiered policy has nothing to demote. A feature justified by a number it cannot
/// move is a feature that ships and then measures 0%.</para>
///
/// <para><b>Why it runs the real encoder rather than a model of one.</b> The thing being
/// measured IS the selection logic: which entities are emitted, and what the delta
/// encoder does with the ones that are not. A reimplementation would be measuring the
/// reimplementation. Both arms are real <see cref="SnapshotDeltaState"/> instances, and
/// no production code is changed — the candidate policy lives here.</para>
///
/// <para><b>How the B arm defers without lying to the encoder.</b> A deferred entity is
/// NOT removed from the gathered span. Removing it would make the encoder conclude it
/// left the AOI and emit a despawn, which is both wrong and more expensive than the
/// update it was trying to skip. Instead the B arm substitutes the entity's
/// last-sent values back into the span, so <c>SentView.Equals</c> reports it unchanged
/// and the encoder omits it exactly as it omits a genuinely idle entity. That is the
/// same wire outcome the real implementation would produce by not adding it to
/// <c>_candidates</c>.</para>
///
/// <para><b>The saving is reported next to its cost.</b> A policy that halves bytes by
/// letting an entity go two seconds stale has not bought anything, so staleness is
/// measured, not assumed — max and p99, in world ticks.</para>
///
/// <para>Not a test: no assertion on the numbers, the output is the deliverable, skipped
/// unless <c>BENCH_TICK=1</c>:
/// <code>BENCH_TICK=1 dotnet test --filter FullyQualifiedName~ImportanceIntervalBench \
///   --logger "console;verbosity=detailed"</code></para>
/// </summary>
public sealed class ImportanceIntervalBench
{
    private const string EnvVar = "BENCH_TICK";

    private const float Radius = GameConstants.DefaultAoiRadius;
    private const int KeyframeInterval = GameConstants.DefaultKeyframeInterval;
    private const int WorldTicks = 300;          // 20 s at 15 Hz
    private const int WarmupTicks = 30;

    private readonly ITestOutputHelper _out;

    public ImportanceIntervalBench(ITestOutputHelper output) => _out = output;

    private void Line(string s)
    {
        _out.WriteLine(s);
        Console.WriteLine(s);
    }

    // ── The candidate policy, in one place ───────────────────────────────────────

    /// <summary>
    /// The SHIPPED policy: <see cref="ReplicationImportance.Score"/> under the
    /// <c>balanced</c> weights, banded by <see cref="ReplicationSchedule.Tiered"/>.
    /// </summary>
    /// <remarks>
    /// <b>This was a hand-written policy until 2026-09-18, and the two disagreed by 47
    /// percentage points.</b> The hand-written one gave a near player interval 1, so on a
    /// `cluster` population -- where every entity is a near player -- it demoted nothing and
    /// this bench reported 0.0%. The shipped policy scores a merely-moving player at
    /// distance 2 + type 3 = 5, below the 8 threshold, so it lands in the 133ms band and
    /// `cluster` halves. A live sweep measured -47.3%, and this bench said 0.0%, about the
    /// same server.
    ///
    /// <para>Two measurements of one mechanism disagreeing by that much means at least one
    /// is measuring something else, and here it was this one: a bench that models a policy
    /// nobody runs answers a question nobody asked. It now calls the same two production
    /// types the encoder calls, so the only way it can drift again is if those change.</para>
    ///
    /// <para>Intervals come back in BASE ticks, because that is the unit the encoder
    /// compares against -- see <c>SnapshotDeltaState.TickHz</c>, where getting this wrong
    /// made the whole schedule inert on a live server while every unit test passed.</para>
    /// </remarks>
    private static int IntervalFor(in EntityView e, in Vec2 observer, bool isSelf)
    {
        if (isSelf) return 1;

        float dx = e.Position.X - observer.X;
        float dy = e.Position.Y - observer.Y;

        // hp/action "changed" are false here: this bench moves entities and nothing else,
        // so the change factor never fires and the score is distance + type, exactly as it
        // is for a walking player on the live server.
        var inputs = new ReplicationImportance.Inputs(
            (dx * dx) + (dy * dy), Radius,
            isPlayer: EntityTypes.IsPlayer(e.Type),
            action: e.Action,
            hpChanged: false, actionChanged: false);

        float score = ReplicationImportance.Score(in inputs, ImportanceSettings.Balanced.Weights);

        // NOT modelled here: the encoder exempts the viewer's OWN entity outright
        // (ADR-27 decision 9), so on the live path one entity per viewer is always due
        // regardless of score. This bench has no notion of which population member is the
        // viewer — the observer is a position, not an entity — so it counts that one as
        // banded like any other. At 200-720 entities per viewer the difference is under
        // half a percent of the saving, which is why it is recorded rather than fixed; what
        // must not happen is someone reading this number as the encoder's and concluding
        // self is deferred. It is not.
        return ReplicationSchedule.Tiered.IntervalTicksFor(score, SimulationRates.DefaultCriticalHz)
               / WorldEveryAtDefault;
    }

    /// <summary>Base ticks per world tick at the 60/15 default, so the base-tick intervals
    /// the schedule returns can be compared against this bench's world-tick clock.</summary>
    private const int WorldEveryAtDefault =
        SimulationRates.DefaultCriticalHz / SimulationRates.DefaultWorldHz;

    // ── Population shapes ────────────────────────────────────────────────────────

    private enum Shape
    {
        /// <summary>Everyone on top of everyone: the shape the published ceiling was measured on.</summary>
        Cluster,
        /// <summary>Players spread over the map, still all players, still all moving.</summary>
        Spread,
        /// <summary>Spread players plus a mob population, most of it idle. The shape a game has.</summary>
        Realistic,
    }

    private sealed class World : IDisposable
    {
        public EcsWorld Ecs = null!;
        public string[] Viewers = null!;
        public string[] Movers = null!;
        public void Dispose() => Ecs.Dispose();
    }

    private static World Build(Shape shape, int players, int mobs, int seed)
    {
        var rng = new Random(seed);
        var w = new World { Ecs = new EcsWorld() };
        var viewers = new List<string>();
        var movers = new List<string>();

        float spread = shape == Shape.Cluster ? 8f : 260f;

        for (int i = 0; i < players; i++)
        {
            string id = $"p{i}";
            float x = (float)((rng.NextDouble() - 0.5) * spread);
            float y = (float)((rng.NextDouble() - 0.5) * spread);
            var e = TestHelpers.CreatePlayer(id, x, y, speed: 5f);
            e.Action = EntityAction.Moving;
            e.FacingBrad = (uint)(1 + rng.Next(65535));
            w.Ecs.AddEntity(e);
            viewers.Add(id);
            movers.Add(id);   // every player moves, as the loadtest drives them
        }

        if (shape == Shape.Realistic)
        {
            for (int i = 0; i < mobs; i++)
            {
                string id = $"m{i}";
                float x = (float)((rng.NextDouble() - 0.5) * spread);
                float y = (float)((rng.NextDouble() - 0.5) * spread);
                var e = TestHelpers.CreateMob(id, x, y, speed: 2.5f);
                // Two mobs in three stand still. Idle entities are ALREADY free -- the
                // delta encoder omits an unchanged entity -- so a population that moves
                // everything would overstate what an interval policy can add.
                bool moving = rng.NextDouble() < 0.34;
                e.Action = moving ? EntityAction.Moving : EntityAction.Idle;
                e.FacingBrad = (uint)(1 + rng.Next(65535));
                w.Ecs.AddEntity(e);
                if (moving) movers.Add(id);
            }
        }

        w.Viewers = viewers.ToArray();
        w.Movers = movers.ToArray();
        return w;
    }

    // ── One arm's accumulated result ─────────────────────────────────────────────

    private sealed class Arm
    {
        public long Bytes;
        public long EntitiesEmitted;
        public long Observations;
        public readonly Dictionary<int, long> TierCounts = new();
        public readonly List<int> Staleness = new();
    }

    [SkippableFact]
    public void MeasureWhatIntervalsWouldSave()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            "Set BENCH_TICK=1 to run the importance-interval measurement.");

        Line("importance intervals vs today's encoder — real SnapshotDeltaState, both arms");
        Line($"world ticks {WorldTicks} (after {WarmupTicks} warmup), AOI radius {Radius}, keyframe {KeyframeInterval}");
        Line("");
        Line($"{"shape",-10} {"pop",5} {"viewers",8} {"B/ent/snap A",13} {"B/ent/snap B",13} {"saving",8} " +
             $"{"stale max",10} {"stale p99",10}  tier mix (1/2/4)");
        Line(new string('-', 118));

        Measure(Shape.Cluster, players: 200, mobs: 0);
        Measure(Shape.Spread, players: 200, mobs: 0);
        Measure(Shape.Realistic, players: 60, mobs: 300);
        Measure(Shape.Realistic, players: 120, mobs: 600);
    }

    private void Measure(Shape shape, int players, int mobs)
    {
        using World w = Build(shape, players, mobs, seed: 1234);

        int viewerCount = Math.Min(w.Viewers.Length, 60);   // encode cost is O(viewers x AOI)
        var a = new Arm();
        var b = new Arm();

        var stateA = new SnapshotDeltaState[viewerCount];
        var stateB = new SnapshotDeltaState[viewerCount];
        // Per viewer, per entity key: the values arm B last actually sent, and the world
        // tick it sent them on.
        var lastSentB = new Dictionary<int, EntityView>[viewerCount];
        var lastTickB = new Dictionary<int, int>[viewerCount];

        for (int v = 0; v < viewerCount; v++)
        {
            stateA[v] = new SnapshotDeltaState(SnapshotDeltaState.PhaseFor(w.Viewers[v])) { SelfId = w.Viewers[v] };
            stateB[v] = new SnapshotDeltaState(SnapshotDeltaState.PhaseFor(w.Viewers[v])) { SelfId = w.Viewers[v] };
            lastSentB[v] = new Dictionary<int, EntityView>();
            lastTickB[v] = new Dictionary<int, int>();
        }

        var gathered = new EntityView[w.Viewers.Length + mobs + 16];
        var substituted = new EntityView[gathered.Length];
        var rng = new Random(99);

        for (int t = 1; t <= WorldTicks; t++)
        {
            StepMovers(w, rng);
            bool counting = t > WarmupTicks;

            for (int v = 0; v < viewerCount; v++)
            {
                string self = w.Viewers[v];
                Vec2 anchor = default;
                int n = 0;
                w.Ecs.ReadAll(r =>
                {
                    r.TryGetSnapshotAnchor(self, out anchor, out _);
                    n = r.GetEntitiesInRange(anchor, Radius, gathered.AsSpan());
                });
                if (n > gathered.Length) n = gathered.Length;

                // ── Arm A: today ──────────────────────────────────────────────
                var msgA = stateA[v].Encode((ulong)t, (ulong)t, gathered.AsSpan(0, n),
                    KeyframeInterval, intern: true, observer: anchor);
                if (counting)
                {
                    a.Bytes += msgA.CalculateSize();
                    a.EntitiesEmitted += msgA.Entities.Count;
                    a.Observations += n;
                }

                // ── Arm B: same encoder, interval-filtered candidates ─────────
                for (int i = 0; i < n; i++)
                {
                    ref readonly EntityView e = ref gathered[i];
                    bool isSelf = string.Equals(e.Id, self, StringComparison.Ordinal);
                    int interval = IntervalFor(in e, in anchor, isSelf);
                    if (counting)
                    {
                        b.TierCounts.TryGetValue(interval, out long c);
                        b.TierCounts[interval] = c + 1;
                    }

                    bool known = lastSentB[v].TryGetValue(e.Key, out EntityView prev);
                    bool due = !known
                               || interval <= 1
                               || t - lastTickB[v][e.Key] >= interval;

                    if (due)
                    {
                        substituted[i] = e;
                        lastSentB[v][e.Key] = e;
                        lastTickB[v][e.Key] = t;
                    }
                    else
                    {
                        // Substitute, never remove: removing it would read as a despawn.
                        substituted[i] = prev;
                        if (counting) b.Staleness.Add(t - lastTickB[v][e.Key]);
                    }
                }

                var msgB = stateB[v].Encode((ulong)t, (ulong)t, substituted.AsSpan(0, n),
                    KeyframeInterval, intern: true, observer: anchor);
                if (counting)
                {
                    b.Bytes += msgB.CalculateSize();
                    b.EntitiesEmitted += msgB.Entities.Count;
                    b.Observations += n;
                }
            }
        }

        double perA = a.Observations > 0 ? (double)a.Bytes / a.Observations : 0;
        double perB = b.Observations > 0 ? (double)b.Bytes / b.Observations : 0;
        double saving = perA > 0 ? 100.0 * (perA - perB) / perA : 0;

        b.Staleness.Sort();
        int staleMax = b.Staleness.Count > 0 ? b.Staleness[^1] : 0;
        int staleP99 = b.Staleness.Count > 0 ? b.Staleness[(int)(b.Staleness.Count * 0.99)] : 0;

        long t1 = b.TierCounts.GetValueOrDefault(1);
        long t2 = b.TierCounts.GetValueOrDefault(2);
        long t4 = b.TierCounts.GetValueOrDefault(4);
        long tt = Math.Max(1, t1 + t2 + t4);

        Line($"{shape,-10} {players + mobs,5} {viewerCount,8} {perA,13:F2} {perB,13:F2} {saving,7:F1}% " +
             $"{staleMax,10} {staleP99,10}  {100.0 * t1 / tt,4:F0}/{100.0 * t2 / tt,3:F0}/{100.0 * t4 / tt,3:F0}");
    }

    /// <summary>Advance every moving entity one world tick. Idle ones do not move at all,
    /// which is what makes them free in arm A already.</summary>
    private static void StepMovers(World w, Random rng)
    {
        w.Ecs.UpdateComponents(writer =>
        {
            for (int i = 0; i < w.Movers.Length; i++)
            {
                EntityHandle h = writer.Resolve(w.Movers[i]);
                if (!h.IsValid) continue;
                ref var p = ref writer.PositionOf(h);
                float ang = (float)(rng.NextDouble() * Math.Tau);
                p.Value = new Vec2(
                    p.Value.X + (float)Math.Cos(ang) * 0.33f,
                    p.Value.Y + (float)Math.Sin(ang) * 0.33f);
            }
        });
    }
}
