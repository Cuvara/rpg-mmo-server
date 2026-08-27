using System.Diagnostics;
using GameServer.World;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Bench;

/// <summary>
/// Paired in-process A/B for issue #237's two changes, run at ~200 viewers over 200
/// entities at realistic AOI density:
///
/// <list type="number">
/// <item><description><b>Trimmed compose</b> — the AOI gather composing the 7-field
/// <see cref="EntityView"/> against the full 11-field <see cref="EntityState"/>.
/// </description></item>
/// <item><description><b>Int-keyed delta state</b> — the shipped
/// <see cref="GameServer.Snapshot.SnapshotDeltaState"/> (maps keyed on the stable int)
/// against a verbatim replica of the string-keyed encoder it replaced.</description></item>
/// </list>
///
/// <para><b>Discipline</b> (BENCHMARK.md Part V): the box is shared and absolute
/// timings swing ±50%, so both arms of each pair run back to back inside every round —
/// only the within-run ratio is quotable. <see cref="Stopwatch"/> only (#153). Not a
/// test: no timing assertion, output is the deliverable, skipped unless
/// <c>BENCH_TICK=1</c>:
/// <code>BENCH_TICK=1 dotnet test --filter FullyQualifiedName~AoiComposeBench \
///   --logger "console;verbosity=detailed"</code></para>
///
/// <para><b>The legacy replica.</b> <see cref="LegacyStringKeyedDeltaState"/> is the
/// pre-#237 <c>SnapshotDeltaState</c> hot path copied verbatim (string-keyed
/// <c>_lastSent</c>/<c>_seen</c>/<c>_handles</c>, <see cref="EntityState"/> input) so
/// the A arm is the code that actually ran, not a reconstruction. It exists only in
/// this bench; the byte-identity tests pin the shipped encoder to it semantically.</para>
/// </summary>
public sealed class AoiComposeBench
{
    private readonly ITestOutputHelper _out;

    public AoiComposeBench(ITestOutputHelper output) => _out = output;

    private const string EnvVar = "BENCH_TICK";

    private const int Entities = 200;
    private const int Viewers = 200;
    private const float Radius = GameConstants.DefaultAoiRadius;
    private const int KeyframeInterval = GameConstants.DefaultKeyframeInterval;
    private const int Rounds = 600;
    private const int Warmup = 60;

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
    }

    [SkippableFact]
    public void Bench_TrimmedComposeAndIntKeyedDelta()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Set {EnvVar}=1 to run this benchmark.");

        // Deterministic placement on a disc of radius 175 around the origin — the
        // TickBreakdownBench density, where an AOI of radius 50 actually holds
        // neighbours and the compose half of the scan runs.
        using var world = new EcsWorld();
        var rng = new Random(20260827);
        var ids = new string[Entities];
        var angles = new double[Entities];
        var radii = new double[Entities];
        for (int i = 0; i < Entities; i++)
        {
            ids[i] = $"p{i}";
            angles[i] = rng.NextDouble() * Math.PI * 2;
            radii[i] = 175.0 * Math.Sqrt(rng.NextDouble());
            world.AddEntity(TestHelpers.CreatePlayer(ids[i],
                x: (float)(Math.Cos(angles[i]) * radii[i]),
                y: (float)(Math.Sin(angles[i]) * radii[i]), speed: 4f));
        }

        var anchors = new Vec2[Viewers];
        var stateBuffers = new EntityState[Viewers][];
        var viewBuffers = new EntityView[Viewers][];
        var counts = new int[Viewers];
        for (int v = 0; v < Viewers; v++)
        {
            stateBuffers[v] = new EntityState[Entities];
            viewBuffers[v] = new EntityView[Entities];
        }

        var legacy = new LegacyStringKeyedDeltaState[Viewers];
        var current = new GameServer.Snapshot.SnapshotDeltaState[Viewers];
        for (int v = 0; v < Viewers; v++)
        {
            int phase = GameServer.Snapshot.SnapshotDeltaState.PhaseFor($"p{v}");
            legacy[v] = new LegacyStringKeyedDeltaState(phase);
            current[v] = new GameServer.Snapshot.SnapshotDeltaState(phase);
        }

        var gatherFull = new List<long>(Rounds);
        var gatherTrim = new List<long>(Rounds);
        var encodeStr = new List<long>(Rounds);
        var encodeInt = new List<long>(Rounds);

        long totalMatches = 0;
        var sw = new Stopwatch();

        for (int r = 0; r < Warmup + Rounds; r++)
        {
            bool measured = r >= Warmup;

            // Mutate the world OUTSIDE every timed region: everybody drifts on a
            // deterministic orbit, so deltas carry real position changes each round.
            world.Update((get, set) =>
            {
                for (int i = 0; i < Entities; i++)
                {
                    double a = angles[i] + r * 0.01;
                    EntityState? e = get(ids[i]);
                    if (e is { } s)
                    {
                        s.Position = new Vec2(
                            (float)(Math.Cos(a) * radii[i]), (float)(Math.Sin(a) * radii[i]));
                        set(ids[i], s);
                    }
                    if (i < Viewers) anchors[i] = new Vec2(
                        (float)(Math.Cos(a) * radii[i]), (float)(Math.Sin(a) * radii[i]));
                }
            });

            // ── Pair 1: gather, full compose vs trimmed compose ──────────────────
            sw.Restart();
            world.ReadAll(reader =>
            {
                for (int v = 0; v < Viewers; v++)
                    reader.GetEntitiesInRange(anchors[v], Radius, stateBuffers[v]);
            });
            sw.Stop();
            if (measured) gatherFull.Add(sw.ElapsedTicks);

            sw.Restart();
            world.ReadAll(reader =>
            {
                for (int v = 0; v < Viewers; v++)
                    counts[v] = reader.GetEntitiesInRange(anchors[v], Radius, viewBuffers[v]);
            });
            sw.Stop();
            if (measured)
            {
                gatherTrim.Add(sw.ElapsedTicks);
                for (int v = 0; v < Viewers; v++) totalMatches += counts[v];
            }

            // ── Pair 2: delta encode, string-keyed replica vs shipped int-keyed ──
            ulong tick = (ulong)(r + 1);
            sw.Restart();
            for (int v = 0; v < Viewers; v++)
            {
                legacy[v].Encode(tick, tick, stateBuffers[v].AsSpan(0, counts[v]),
                    KeyframeInterval, intern: true);
            }
            sw.Stop();
            if (measured) encodeStr.Add(sw.ElapsedTicks);

            sw.Restart();
            for (int v = 0; v < Viewers; v++)
            {
                current[v].Encode(tick, tick, viewBuffers[v].AsSpan(0, counts[v]),
                    KeyframeInterval, intern: true);
            }
            sw.Stop();
            if (measured) encodeInt.Add(sw.ElapsedTicks);
        }

        double occupancy = totalMatches / (double)(Rounds * Viewers);
        Line($"== AOI compose + delta-key A/B: {Entities} entities, {Viewers} viewers, " +
             $"radius {Radius}, mean AOI occupancy {occupancy:F1}, n={Rounds} rounds, " +
             $"microseconds per {Viewers}-viewer pass, arms interleaved within each round ==");
        Line($"{"arm",-22} {"median",10} {"p99",10} {"min",10} {"max",10} {"ratio",8}");

        Report("gather/full-compose", gatherFull, gatherFull);
        Report("gather/trimmed", gatherTrim, gatherFull);
        Report("encode/string-keyed", encodeStr, encodeStr);
        Report("encode/int-keyed", encodeInt, encodeStr);

        void Report(string name, List<long> samples, List<long> baseline)
        {
            var st = new Stats(new List<long>(samples));
            var bl = new Stats(new List<long>(baseline));
            Line($"{name,-22} {st.Median,10:F1} {st.P99,10:F1} {st.Min,10:F1} {st.Max,10:F1} " +
                 $"{bl.Median / st.Median,7:F2}x");
        }
    }

    /// <summary>
    /// Verbatim replica of the pre-#237 <c>SnapshotDeltaState</c> hot path: maps keyed
    /// on entity-id strings, <see cref="EntityState"/> input. The A arm of the encode
    /// pair. Measurement fixture only — never referenced by production code.
    /// </summary>
    private sealed class LegacyStringKeyedDeltaState
    {
        private readonly struct SentView : IEquatable<SentView>
        {
            public readonly string Type;
            public readonly float X;
            public readonly float Y;
            public readonly int Hp;
            public readonly int MaxHp;
            public readonly float Speed;

            public SentView(in EntityState e)
            {
                Type = e.Type;
                X = e.Position.X;
                Y = e.Position.Y;
                Hp = e.Hp;
                MaxHp = e.MaxHp;
                Speed = e.Speed;
            }

            public bool Equals(SentView other) =>
                Hp == other.Hp &&
                MaxHp == other.MaxHp &&
                X.Equals(other.X) &&
                Y.Equals(other.Y) &&
                Speed.Equals(other.Speed) &&
                string.Equals(Type, other.Type, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is SentView v && Equals(v);
            public override int GetHashCode() => HashCode.Combine(Type, X, Y, Hp, MaxHp, Speed);
        }

        private readonly Dictionary<string, uint> _handles = new(StringComparer.Ordinal);
        private uint _nextHandle = 1;
        private bool _intern;

        private readonly SnapshotMessage _message = new();
        private readonly List<EntitySnapshot> _pool = new();
        private int _poolUsed;

        private readonly Dictionary<string, SentView> _lastSent = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private int _sinceKeyframe;
        private int _forceFull = 1;

        private readonly int _phaseSeed;
        private bool _phaseApplied;

        public LegacyStringKeyedDeltaState(int phaseSeed) =>
            _phaseSeed = phaseSeed < 0 ? 0 : phaseSeed;

        public SnapshotMessage Encode(ulong tick, ulong ackTick, ReadOnlySpan<EntityState> nearby,
            int keyframeInterval, bool intern = false)
        {
            _intern = intern;
            bool full = Interlocked.Exchange(ref _forceFull, 0) != 0
                        || keyframeInterval <= 0
                        || _sinceKeyframe >= keyframeInterval;

            if (full)
            {
                if (!_phaseApplied && keyframeInterval > 1)
                {
                    _phaseApplied = true;
                    _sinceKeyframe = _phaseSeed % keyframeInterval;
                }
                else
                {
                    _sinceKeyframe = 0;
                }
                return EncodeFull(tick, ackTick, nearby);
            }

            _sinceKeyframe++;
            return EncodeDelta(tick, ackTick, nearby);
        }

        private SnapshotMessage BeginMessage(ulong tick, ulong ackTick, bool full)
        {
            _message.Entities.Clear();
            _message.Removed.Clear();
            _message.Tick = tick;
            _message.AckTick = ackTick;
            _message.Full = full;
            _poolUsed = 0;
            return _message;
        }

        private EntitySnapshot Rent()
        {
            EntitySnapshot e;
            if (_poolUsed < _pool.Count)
            {
                e = _pool[_poolUsed];
            }
            else
            {
                e = new EntitySnapshot();
                _pool.Add(e);
            }
            _poolUsed++;

            e.Id = "";
            e.TypeName = "";
            e.Type = EntityType.Unspecified;
            e.Handle = 0;
            e.X = 0f;
            e.Y = 0f;
            e.Hp = 0;
            e.MaxHp = 0;
            e.Speed = 0f;
            return e;
        }

        private SnapshotMessage EncodeFull(ulong tick, ulong ackTick, ReadOnlySpan<EntityState> nearby)
        {
            var msg = BeginMessage(tick, ackTick, full: true);
            _lastSent.Clear();
            _handles.Clear();
            _nextHandle = 1;

            for (int i = 0; i < nearby.Length; i++)
            {
                var e = nearby[i];
                msg.Entities.Add(ToMsg(in e));
                _lastSent[e.Id] = new SentView(in e);
            }

            return msg;
        }

        private SnapshotMessage EncodeDelta(ulong tick, ulong ackTick, ReadOnlySpan<EntityState> nearby)
        {
            _seen.Clear();
            var msg = BeginMessage(tick, ackTick, full: false);

            for (int i = 0; i < nearby.Length; i++)
            {
                var e = nearby[i];
                _seen.Add(e.Id);

                var view = new SentView(in e);
                if (_lastSent.TryGetValue(e.Id, out var prev) && prev.Equals(view))
                    continue;

                msg.Entities.Add(ToMsg(in e));
                _lastSent[e.Id] = view;
            }

            if (_lastSent.Count != _seen.Count)
            {
                foreach (var id in _lastSent.Keys)
                {
                    if (!_seen.Contains(id)) msg.Removed.Add(id);
                }
                for (int i = 0; i < msg.Removed.Count; i++)
                {
                    _lastSent.Remove(msg.Removed[i]);
                    _handles.Remove(msg.Removed[i]);
                }
            }

            return msg;
        }

        private EntitySnapshot ToMsg(in EntityState e)
        {
            var msg = Rent();
            msg.X = e.Position.X;
            msg.Y = e.Position.Y;
            msg.Hp = e.Hp;
            msg.MaxHp = e.MaxHp;
            msg.Speed = e.Speed;
            GameServer.Net.EntityTypes.SetType(msg, e.Type);

            if (!_intern)
            {
                msg.Id = e.Id;
                return msg;
            }

            if (_handles.TryGetValue(e.Id, out uint handle))
            {
                msg.Handle = handle;
            }
            else
            {
                handle = _nextHandle++;
                _handles[e.Id] = handle;
                msg.Handle = handle;
                msg.Id = e.Id;
            }
            return msg;
        }
    }
}
