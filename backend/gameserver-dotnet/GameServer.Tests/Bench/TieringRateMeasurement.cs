using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Server;
using GameServer.Tests.Infrastructure;
using GameServer.Tests.Server;
using Microsoft.Extensions.Logging.Abstractions;
using RpgMmo.Wire.V1;
using Xunit.Abstractions;

namespace GameServer.Tests.Bench;

/// <summary>
/// Does <c>tiered</c> work at a world rate fast enough to separate its bands, what does that
/// rate cost in bytes, and what happens when the byte budget defers on top of the schedule
/// (#420, #421).
///
/// <para><b>The two questions are measured in one matrix on purpose.</b> The schedule and the
/// byte budget are independent deferral sources that do not know about each other (#421), and
/// the client-facing consequence — how long one client waits for news of one entity — is their
/// sum. Measuring the schedule alone at 30Hz would produce consistent numbers about the wrong
/// thing, so every schedule arm is run with and without byte pressure, and each alone.</para>
///
/// <para><b>What is measured.</b> The per-entity arrival gap — the time between consecutive
/// snapshots <b>carrying a given entity</b>, timed with a <see cref="Stopwatch"/> where the bytes
/// arrive — not snapshot cadence, and not the client's staleness estimator (whose two-anchor
/// fit is in its known-bad regime on this host; see <c>backend/docs/MEASUREMENT.md</c>). Beside
/// it: the observer's downlink in bytes per second, counted under the decoder, because raising
/// <c>SIM_WORLD_HZ</c> moves both; and the server's two deferral gauges
/// (<c>max_state_age</c> for the schedule, <c>max_shed_age</c> for the budget).</para>
///
/// <para><b>The instrument guard.</b> Every arm carries an idle entity. Unmutated, its gap runs
/// to the keyframe interval while snapshots keep arriving at the world rate, so an arm whose
/// idle gap does not exceed three cadence p99s is an instrument timing the stream, and is
/// reported as <c>GUARD FAIL</c> instead of producing a number.</para>
///
/// <para><b>What "real link" means here, and what it does not.</b> The observer's downlink runs
/// through <see cref="AdversityProxy"/> — a TCP relay on loopback with seeded one-way delay and
/// jitter, the same instrument #413's figures came from. It is not a radio, it does not drop,
/// and it cannot reorder within one TCP stream. What it proves is the scheduling arithmetic
/// against a spread link; what a mobile network does beyond spread is outside it.</para>
///
/// <para>Not a test: the output is the deliverable and it takes several minutes. Skipped unless
/// <c>MEASURE_TIERING=1</c>:
/// <code>MEASURE_TIERING=1 dotnet test --filter FullyQualifiedName~TieringRateMeasurement \
///   --logger "console;verbosity=detailed"</code>
/// Results and their reading: <c>backend/docs/BENCHMARK.md</c> Part XX.</para>
/// </summary>
[Collection(AdversityCollection.Name)]
public sealed class TieringRateMeasurement
{
    private const string EnvVar = "MEASURE_TIERING";
    private const string JwtSecret = "tiering-measure-secret-32-bytes!";
    private const string ServerId = "gs-tiermeasure";
    private const int Seed = 20260924;

    /// <summary>
    /// Enough entities in one AOI that a small budget has something to choose between. At
    /// three (the #413 figure) there is no ordering for the budget to apply.
    /// </summary>
    private const int Movers = 10;

    private const double WarmupSeconds = 2.0;
    private const double WindowSeconds = 8.0;

    /// <summary>
    /// Byte-pressure arm. Chosen from the calibration row (the unbudgeted arms' mean snapshot)
    /// so the budget sheds on most snapshots rather than on keyframes only: it is a pressure
    /// setting, not a recommended value. The shipped default, 8192, does not bite at this
    /// population and is the "no pressure" arm.
    /// </summary>
    internal const int PressureBytes = 120;

    private readonly ITestOutputHelper _out;
    public TieringRateMeasurement(ITestOutputHelper o) => _out = o;

    [SkippableFact]
    public async Task Matrix()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable(EnvVar) == "1",
            $"set {EnvVar}=1 to run the tiering measurement (several minutes)");

        var arms = new List<(int worldHz, string profile)>
        {
            (15, "off"),
            (20, "off"), (20, "tiered"),
            (30, "off"), (30, "tiered"),
        };
        int[] jitters = { 0, 25, 60 };
        int[] budgets = { global::GameServer.Snapshot.SnapshotDeltaState.DefaultMaxSnapshotBytes, PressureBytes };

        _out.WriteLine($"seed={Seed} movers={Movers} window={WindowSeconds}s " +
                       $"budget(ms)={ReplicationSchedule.ClientInterpolationBudgetMs} " +
                       $"ceiling={ReplicationSchedule.MaxIntervalMs} link={ReplicationSchedule.LinkSpreadAllowanceMs}");
        _out.WriteLine(Reading.Header);
        foreach (var (hz, profile) in arms)
        foreach (int budget in budgets)
        foreach (int jitter in jitters)
        {
            var r = await MeasureAsync(hz, profile, jitter, budget);
            _out.WriteLine(r.ToString());
        }
    }

    internal readonly record struct Reading(
        int WorldHz, string Profile, int JitterMs, int BudgetBytes,
        int Samples, double MedianMs, double P99Ms, double MaxMs, double OverBudgetPct,
        double CadenceP99Ms, double DownlinkBytesPerSec, double MeanSnapshotBytes,
        long Shed, int MaxShedAgeSnapshots, long DeferredByInterval, int MaxStateAgeBaseTicks,
        int MaxUpdateGapMs, double IdleMaxMs)
    {
        public const string Header =
            "hz  profile budget jitter | perEntity n  med   p99   max  >150ms | cadP99 | KB/s  B/snap | " +
            "shed shedAge(ms) | deferred stateAge(ms) | updateGap(ms) | idleMax guard";

        public bool GuardOk => IdleMaxMs > CadenceP99Ms * 3;

        // Each gauge in its own unit, converted: shed age counts snapshots (world ticks),
        // state age counts base ticks (60Hz). Reading either as the other is off by the rate
        // ratio — BENCHMARK.md §48.
        public double ShedAgeMs => MaxShedAgeSnapshots * 1000.0 / WorldHz;
        public double StateAgeMs => MaxStateAgeBaseTicks * 1000.0 / 60;

        public override string ToString() =>
            $"{WorldHz,2} {Profile,-7} {BudgetBytes,6} ±{JitterMs,-3} | " +
            $"{Samples,5} {MedianMs,5:F1} {P99Ms,5:F1} {MaxMs,5:F1} {OverBudgetPct,5:F2}% | " +
            $"{CadenceP99Ms,5:F1} | {DownlinkBytesPerSec / 1024.0,5:F2} {MeanSnapshotBytes,5:F0} | " +
            $"{Shed,5} {ShedAgeMs,4:F0} | {DeferredByInterval,6} {StateAgeMs,4:F0} | " +
            $"{MaxUpdateGapMs,5} | {IdleMaxMs,6:F0} {(GuardOk ? "ok" : "GUARD FAIL")}";
    }

    private async Task<Reading> MeasureAsync(int worldHz, string profile, int jitterMs, int budgetBytes)
    {
        Assert.True(SimulationRates.TryCreate(60, worldHz, 5, out var rates, out string? rateErr), rateErr);
        Assert.True(ReplicationSchedule.TryCreate(profile, importanceEnabled: true, 60, worldHz,
            out var schedule, out string? schedErr), schedErr);

        using var metrics = new GameMetrics("map_tiermeasure", $"test.{Guid.NewGuid():N}");
        var options = new ServerOptions
        {
            ServerAddr = ":0", ServerId = ServerId, MapId = "map_tiermeasure", Mode = "map",
            Transport = TransportKind.Tcp, TickRate = rates!.CriticalHz, SimulationRates = rates,
            Capacity = 32, JwtSecret = JwtSecret, JoinTokenSecret = JwtSecret,
            SaveInterval = TimeSpan.FromHours(1), PlayerStore = new MemoryPlayerStore(),
            Importance = ImportanceSettings.Balanced,
            ReplicationSchedule = schedule!,
            MaxSnapshotBytes = budgetBytes,
            Metrics = metrics, LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

        var sockets = new List<TcpClient>();
        var moverTasks = new List<Task>();
        var moverIds = new HashSet<string>();

        // Only the observer's downlink is degraded: "what one client on a bad link sees of a
        // healthy world", as in EntityIntervalUnderAdversityTests.
        await using var proxy = AdversityProxy.Start(
            port,
            new AdversityProxy.Profile(
                AdversityProxy.Link.Clean,
                new AdversityProxy.Link(jitterMs > 0 ? 80 : 0, jitterMs),
                Seed));

        try
        {
            for (int i = 0; i < Movers; i++)
            {
                string id = $"mv{i}-{Guid.NewGuid():N}"[..14];
                moverIds.Add(id);
                var c = new TcpClient { NoDelay = true };
                sockets.Add(c);
                await ConnectWithRetryAsync(c, port);
                var s = c.GetStream();
                await JoinAsync(s, id, WireEncoding.Json, cts.Token);
                int phase = i;
                moverTasks.Add(Task.Run(() => MoveForeverAsync(s, phase, cts.Token), cts.Token));
            }

            string idlerId = $"id-{Guid.NewGuid():N}"[..14];
            var idler = new TcpClient { NoDelay = true };
            sockets.Add(idler);
            await ConnectWithRetryAsync(idler, port);
            await JoinAsync(idler.GetStream(), idlerId, WireEncoding.Json, cts.Token);

            // Protobuf, because the byte budget only runs on interned connections — a JSON
            // observer would make every budget arm identical to the unbudgeted one.
            string observerId = $"ob-{Guid.NewGuid():N}"[..14];
            using var observer = new TcpClient { NoDelay = true };
            await ConnectWithRetryAsync(observer, proxy.Port);
            var counting = new CountingStream(observer.GetStream());
            await JoinAsync(counting, observerId, WireEncoding.Proto, cts.Token);

            var arrivals = new Dictionary<string, List<double>>();
            var idleArrivals = new List<double>();
            var cadence = new List<double>();
            var handles = new Dictionary<uint, string>();
            long snapshots = 0, bytesAtWindowStart = -1;
            double prevSnapshotMs = -1, windowStartMs = 0;
            long shed0 = 0, deferred0 = 0;
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed.TotalSeconds < WarmupSeconds + WindowSeconds)
            {
                var env = await WireProtocol.DecodeAsync(counting, cts.Token);
                if (env == null) break;
                if ((MsgType)env.Type != MsgType.Snapshot) continue;

                double now = clock.Elapsed.TotalMilliseconds;
                var msg = WireProtocol.GetPayload<SnapshotMessage>(env);

                // Handles reset at every keyframe and bind on first sight with an id.
                if (msg.Full) handles.Clear();
                bool measuring = clock.Elapsed.TotalSeconds >= WarmupSeconds;
                if (measuring && bytesAtWindowStart < 0)
                {
                    bytesAtWindowStart = counting.BytesRead;
                    windowStartMs = now;
                    shed0 = metrics.SnapshotEntitiesShed;
                    deferred0 = metrics.SnapshotDeferredByInterval;
                }

                if (measuring)
                {
                    snapshots++;
                    if (prevSnapshotMs >= 0) cadence.Add(now - prevSnapshotMs);
                }
                prevSnapshotMs = now;

                foreach (var e in msg.Entities)
                {
                    string? id = e.Id;
                    if (!string.IsNullOrEmpty(id)) { if (e.Handle != 0) handles[e.Handle] = id; }
                    else if (e.Handle != 0) handles.TryGetValue(e.Handle, out id);
                    if (!measuring || string.IsNullOrEmpty(id)) continue;

                    if (id == idlerId) { idleArrivals.Add(now); continue; }
                    if (!moverIds.Contains(id)) continue;
                    if (!arrivals.TryGetValue(id, out var list)) arrivals[id] = list = new List<double>();
                    list.Add(now);
                }
            }

            double windowMs = clock.Elapsed.TotalMilliseconds - windowStartMs;
            long windowBytes = counting.BytesRead - Math.Max(0, bytesAtWindowStart);

            var gaps = new List<double>();
            foreach (var list in arrivals.Values)
                for (int i = 1; i < list.Count; i++) gaps.Add(list[i] - list[i - 1]);
            var idleGaps = new List<double>();
            for (int i = 1; i < idleArrivals.Count; i++) idleGaps.Add(idleArrivals[i] - idleArrivals[i - 1]);

            gaps.Sort();
            cadence.Sort();
            double over = gaps.Count == 0 ? 0
                : 100.0 * gaps.Count(g => g > ReplicationSchedule.ClientInterpolationBudgetMs) / gaps.Count;

            // An idler that never appeared twice has no gap: report the whole window, which
            // still exceeds the cadence and is the honest reading of "never re-sent".
            double idleMax = idleGaps.Count > 0 ? idleGaps.Max() : (idleArrivals.Count > 0 ? windowMs : 0);

            return new Reading(
                worldHz, profile, jitterMs, budgetBytes,
                gaps.Count, Pick(gaps, 0.50), Pick(gaps, 0.99), gaps.Count > 0 ? gaps[^1] : 0, over,
                Pick(cadence, 0.99),
                windowMs > 0 ? windowBytes * 1000.0 / windowMs : 0,
                snapshots > 0 ? (double)windowBytes / snapshots : 0,
                metrics.SnapshotEntitiesShed - shed0, metrics.MaxShedAge,
                metrics.SnapshotDeferredByInterval - deferred0, metrics.MaxStateAge,
                metrics.MaxUpdateGapMs, idleMax);
        }
        finally
        {
            cts.Cancel();
            foreach (var c in sockets) { try { c.Close(); } catch { } }
            foreach (var t in moverTasks) { try { await t; } catch { } }
            await server.ShutdownAsync();
            try { await runTask; } catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }

    private static double Pick(List<double> sorted, double q) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];

    /// <summary>
    /// Walks in a circle. Position change alone does not raise the importance score, so a
    /// walking non-combat player lands in the schedule's bottom band — the deferred case.
    /// </summary>
    private static async Task MoveForeverAsync(NetworkStream stream, int phase, CancellationToken ct)
    {
        ulong t = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                double a = t * 0.05 + phase;
                await WriteFrameAsync(stream, WireProtocol.NewEnvelope(MsgType.Input,
                    new InputMessage { Tick = ++t, MoveX = (float)Math.Cos(a), MoveY = (float)Math.Sin(a) },
                    WireEncoding.Json), ct);
                await Task.Delay(33, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task JoinAsync(Stream stream, string userId, WireEncoding encoding, CancellationToken ct)
    {
        await WriteFrameAsync(stream, WireProtocol.NewEnvelope(MsgType.JoinToken,
            new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
            encoding), ct);
        var env = await WireProtocol.DecodeAsync(stream, ct);
        Assert.NotNull(env);
        var resp = WireProtocol.GetPayload<JoinTokenResponse>(env!);
        Assert.True(resp.Ok, resp.Error);
    }

    private static async Task WriteFrameAsync(Stream stream, GameServer.Net.Envelope env, CancellationToken ct)
    {
        byte[] frame = WireProtocol.Encode(env);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try { await client.ConnectAsync(IPAddress.Loopback, port); return; }
            catch (SocketException) { await Task.Delay(100); }
        }
        throw new TimeoutException($"nothing was listening on :{port}");
    }

    /// <summary>Counts bytes read under the decoder: frame, length prefix and all.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long BytesRead;
        public CountingStream(Stream inner) => _inner = inner;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await _inner.ReadAsync(buffer, ct);
            BytesRead += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            _inner.WriteAsync(buffer, ct);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
