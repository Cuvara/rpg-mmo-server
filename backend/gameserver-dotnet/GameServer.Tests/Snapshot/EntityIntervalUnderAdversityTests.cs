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

namespace GameServer.Tests.Snapshot;

/// <summary>
/// The interval a client actually waits for news of one entity, over a real link (#413).
///
/// <para><b>The object being measured, and the one it is easy to measure instead.</b> This
/// is the gap between consecutive snapshots <b>carrying a given entity</b>, not the snapshot
/// cadence. Snapshots keep arriving at a flawless 15Hz while a deferred entity is absent
/// from them, so every cadence number this project has — including the adversity suite's —
/// is blind to exactly the thing that makes a mob step across the screen. The two are
/// measured side by side here, and
/// <see cref="PerEntityIntervalIsNotTheSnapshotCadence"/> asserts they can diverge, so a
/// run that had silently fallen back to measuring cadence would fail rather than agree.
/// </para>
///
/// <para><b>Why it is timed at arrival and not read off the client.</b> The netcode
/// package's <c>SnapshotStalenessEstimator</c> reports a staleness in ticks, which is the
/// obvious source for this number and is not usable: on this host a starved frame loop
/// raises its delay floor and its two-anchor fit reports the result as clock skew
/// (25,448 ppm against a <c>clockRatio</c> of 1.0000 — two readings that cannot both be
/// true). Staleness comes out of that same fit. What is asserted here is a subtraction of
/// two <see cref="Stopwatch"/> readings taken where the bytes arrive, with no fit in it.
/// </para>
///
/// <para><b>What this found.</b> The shipped <c>tiered</c> profile reached a per-entity p99
/// of <b>148–150ms on loopback</b> against the client's 150ms of cover — the budget spent
/// before a single millisecond of network — and 176–178ms at ±25ms one-way jitter. The
/// ceiling now reserves <see cref="ReplicationSchedule.LinkSpreadAllowanceMs"/> for the
/// wire, and these cases are what hold that.</para>
/// </summary>
[Collection(AdversityCollection.Name)]
public class EntityIntervalUnderAdversityTests
{
    private const string JwtSecret = "interval-test-secret-32-bytes-aa";
    private const string ServerId = "gs-interval";

    /// <summary>Seeded, and named in every failure message.</summary>
    private const int Seed = 20260923;

    /// <summary>Movers whose entities the observer measures.</summary>
    private const int Movers = 3;

    private const double WindowSeconds = 8.0;
    private const double WarmupSeconds = 2.0;

    /// <summary>
    /// The one-way jitter this schedule claims to serve, and the figure
    /// <see cref="ReplicationSchedule.LinkSpreadAllowanceMs"/> is derived from.
    /// </summary>
    private const int ServedJitterMs = 25;

    /// <summary>
    /// A link past what the design can serve, used as the positive control. ±60ms was
    /// measured at a p99 of 161–162ms on the shipped profile — over the budget, but by only
    /// 11ms, which is too thin to assert on. ±100ms is unambiguous.
    /// </summary>
    private const int UnservableJitterMs = 100;

    private readonly ITestOutputHelper _out;
    public EntityIntervalUnderAdversityTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// The whole point of the ceiling: what one client waits for one entity, scheduler and
    /// link together, must fit inside what the client can render through.
    ///
    /// <para><b>The number.</b> p99 of the per-entity gap, against
    /// <see cref="ReplicationSchedule.ClientInterpolationBudgetMs"/>. Past that a remote
    /// entity holds its last position and then jumps; three people play-testing saw exactly
    /// that when the ceiling was 500ms.</para>
    ///
    /// <para><b>And a lower bound.</b> A p99 below one world interval is not a very good
    /// result, it is an instrument reporting on nothing — an entity nobody moved, a window
    /// that closed early, an id that never matched. Asserted, because the failure mode of
    /// an upper bound is that everything satisfies it.</para>
    /// </summary>
    [Theory]
    [InlineData("off", 0)]
    [InlineData("off", ServedJitterMs)]
    [InlineData("tiered", 0)]
    [InlineData("tiered", ServedJitterMs)]
    public async Task CombinedPerEntityIntervalFitsTheClientBudget(string profile, int jitterMs)
    {
        var r = await MeasureAsync(profile, jitterMs);
        _out.WriteLine(r.ToString());

        Assert.True(r.Samples > 0, $"no per-entity intervals were sampled at all: {r}");
        Assert.Equal(Movers, r.Entities);

        double worldIntervalMs = 1000.0 / 15.0;
        Assert.True(r.P99Ms >= worldIntervalMs * 0.8,
            $"per-entity p99 was {r.P99Ms:F1}ms, under the {worldIntervalMs:F1}ms a world tick " +
            "takes. Nothing can arrive faster than the world rate, so this is the instrument " +
            $"reporting on an entity that was not moving or a window that did not open. {r}");

        Assert.True(r.P99Ms <= ReplicationSchedule.ClientInterpolationBudgetMs,
            $"a client on a ±{jitterMs}ms link waited {r.P99Ms:F1}ms (p99) for news of one " +
            $"entity under profile '{profile}', past the " +
            $"{ReplicationSchedule.ClientInterpolationBudgetMs}ms it can cover " +
            $"(TargetDelay 100 + MaxExtrapolation 50). Past that the entity holds its last " +
            $"position and then jumps. The scheduler's share is capped at " +
            $"{ReplicationSchedule.MaxIntervalMs}ms and the link is allowed " +
            $"{ReplicationSchedule.LinkSpreadAllowanceMs}ms; one of the two is over. " +
            $"seed={Seed}. {r}");
    }

    /// <summary>
    /// The positive control, and the limit of what this design can serve.
    ///
    /// <para>At ±100ms one-way the budget is exceeded whatever the scheduler does: one world
    /// interval alone is 66.7ms and the link spreads arrivals by more than the remainder. It
    /// is asserted to be exceeded for two reasons — it records where the design stops, and
    /// it proves <see cref="CombinedPerEntityIntervalFitsTheClientBudget"/> is capable of
    /// failing. An upper bound that has never been seen to fail is not evidence.</para>
    ///
    /// <para>Closing this one is not a server change: it means a larger <c>TargetDelay</c>
    /// on the client, which costs input latency for everyone to serve the worst link.</para>
    /// </summary>
    [Fact]
    public async Task BeyondTheServedLink_TheBudgetIsExceeded()
    {
        var r = await MeasureAsync("off", UnservableJitterMs);
        _out.WriteLine(r.ToString());

        Assert.True(r.P99Ms > ReplicationSchedule.ClientInterpolationBudgetMs,
            $"a ±{UnservableJitterMs}ms one-way link produced a per-entity p99 of {r.P99Ms:F1}ms, " +
            $"INSIDE the {ReplicationSchedule.ClientInterpolationBudgetMs}ms budget. Either the " +
            "link is not as bad as configured — in which case the served-link cases above are " +
            "measuring less adversity than they claim — or this measurement cannot detect a " +
            $"budget violation at all, which makes those cases vacuous. seed={Seed}. {r}");
    }

    /// <summary>
    /// The instrument check: the per-entity interval is not the snapshot cadence.
    ///
    /// <para>An entity that stops moving is omitted from deltas until the next keyframe, so
    /// its per-entity gap runs to seconds while snapshots keep arriving every 66ms. If those
    /// two numbers ever agree here, the measurement has fallen back to timing the stream
    /// instead of the entity — and every assertion in this file would then be a cadence
    /// assertion wearing an entity's name, which is precisely the wrong-object failure #413
    /// warns about.</para>
    ///
    /// <para>The idler's own interval is <b>not</b> a budget violation: there is nothing to
    /// send about an entity that has not changed, and the client has nothing to interpolate
    /// towards either. It is here to prove the two instruments can disagree.</para>
    /// </summary>
    [Fact]
    public async Task PerEntityIntervalIsNotTheSnapshotCadence()
    {
        var r = await MeasureAsync("off", 0, withIdler: true);
        _out.WriteLine(r.ToString());
        _out.WriteLine($"[idler] samples={r.IdlerSamples} maxGap={r.IdlerMaxMs:F1}ms");

        Assert.True(r.CadenceP99Ms <= ReplicationSchedule.ClientInterpolationBudgetMs,
            $"the snapshot CADENCE p99 was {r.CadenceP99Ms:F1}ms on a clean link, which is a " +
            $"different defect from the one this file is about: {r}");

        Assert.True(r.IdlerSamples > 0,
            $"the idle entity never appeared in two snapshots, so there is no gap to compare " +
            $"the cadence against. {r}");

        Assert.True(r.IdlerMaxMs > r.CadenceP99Ms * 3,
            $"the idle entity's largest gap was {r.IdlerMaxMs:F1}ms against a cadence p99 of " +
            $"{r.CadenceP99Ms:F1}ms. These are supposed to be different objects — an unchanged " +
            "entity is omitted from deltas while snapshots keep arriving — so numbers this " +
            "close mean the per-entity measurement is really timing the stream, and every " +
            $"other assertion in this file is a cadence assertion in disguise. {r}");
    }

    internal readonly record struct Reading(
        string Profile, int JitterMs, int Entities, int Samples,
        double MedianMs, double P95Ms, double P99Ms, double MaxMs,
        double CadenceMedianMs, double CadenceP99Ms, long Snapshots,
        int IdlerSamples, double IdlerMaxMs)
    {
        public override string ToString() =>
            $"[interval profile={Profile} jitter=±{JitterMs}ms] entities={Entities} samples={Samples} " +
            $"perEntity med={MedianMs:F1} p95={P95Ms:F1} p99={P99Ms:F1} max={MaxMs:F1} | " +
            $"cadence med={CadenceMedianMs:F1} p99={CadenceP99Ms:F1} snapshots={Snapshots} " +
            $"ceiling={ReplicationSchedule.MaxIntervalMs}ms budget={ReplicationSchedule.ClientInterpolationBudgetMs}ms";
    }

    private async Task<Reading> MeasureAsync(string profile, int jitterMs, bool withIdler = false)
    {
        Assert.True(SimulationRates.TryCreate(60, 15, 5, out var rates, out string? rateErr), rateErr);
        Assert.True(ReplicationSchedule.TryCreate(profile, importanceEnabled: true, out var schedule, out string? schedErr), schedErr);

        using var metrics = new GameMetrics("map_interval", $"test.{Guid.NewGuid():N}");
        var options = new ServerOptions
        {
            ServerAddr = ":0", ServerId = ServerId, MapId = "map_interval", Mode = "map",
            Transport = TransportKind.Tcp, TickRate = rates!.CriticalHz, SimulationRates = rates,
            Capacity = 16, JwtSecret = JwtSecret, JoinTokenSecret = JwtSecret,
            SaveInterval = TimeSpan.FromHours(1), PlayerStore = new MemoryPlayerStore(),
            Importance = ImportanceSettings.Balanced,
            ReplicationSchedule = schedule!,
            Metrics = metrics, LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

        var moverSockets = new List<TcpClient>();
        var moverTasks = new List<Task>();
        var moverIds = new List<string>();

        // Only the OBSERVER's link is degraded. The movers connect straight to the server,
        // so the measurement is "what one client on a bad link sees of a healthy world"
        // rather than "what a server full of bad links does", which is a different question
        // and would put the adversity on both sides of the number.
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
                moverSockets.Add(c);
                await ConnectWithRetryAsync(c, port);
                var s = c.GetStream();
                await JoinAsync(s, id, cts.Token);
                moverTasks.Add(Task.Run(() => MoveForeverAsync(s, cts.Token), cts.Token));
            }

            // An entity that joins and never moves. Delta encoding omits it once it stops
            // changing, so its gaps run to the keyframe interval while the cadence stays at
            // the world rate — which is the divergence the instrument check needs.
            string? idlerId = null;
            TcpClient? idler = null;
            if (withIdler)
            {
                idlerId = $"id-{Guid.NewGuid():N}"[..14];
                idler = new TcpClient { NoDelay = true };
                moverSockets.Add(idler);
                await ConnectWithRetryAsync(idler, port);
                await JoinAsync(idler.GetStream(), idlerId, cts.Token);
            }

            string observerId = $"ob-{Guid.NewGuid():N}"[..14];
            using var observer = new TcpClient { NoDelay = true };
            await ConnectWithRetryAsync(observer, proxy.Port);
            var os = observer.GetStream();
            await JoinAsync(os, observerId, cts.Token);

            // Per entity: the arrival instants of snapshots that carried it.
            var arrivals = new Dictionary<string, List<double>>();
            var idlerArrivals = new List<double>();
            var cadence = new List<double>();
            long snapshots = 0;
            double prevSnapshotMs = -1;
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed.TotalSeconds < WarmupSeconds + WindowSeconds)
            {
                var env = await WireProtocol.DecodeAsync(os, cts.Token);
                if (env == null) break;
                if ((MsgType)env.Type != MsgType.Snapshot) continue;

                double now = clock.Elapsed.TotalMilliseconds;
                var msg = WireProtocol.GetPayload<SnapshotMessage>(env);
                if (clock.Elapsed.TotalSeconds < WarmupSeconds) { prevSnapshotMs = now; continue; }

                snapshots++;
                if (prevSnapshotMs >= 0) cadence.Add(now - prevSnapshotMs);
                prevSnapshotMs = now;

                foreach (var e in msg.Entities)
                {
                    if (idlerId != null && e.Id == idlerId) { idlerArrivals.Add(now); continue; }
                    if (!moverIds.Contains(e.Id)) continue;
                    if (!arrivals.TryGetValue(e.Id, out var list)) arrivals[e.Id] = list = new List<double>();
                    list.Add(now);
                }
            }

            var gaps = new List<double>();
            foreach (var list in arrivals.Values)
                for (int i = 1; i < list.Count; i++) gaps.Add(list[i] - list[i - 1]);

            var idlerGaps = new List<double>();
            for (int i = 1; i < idlerArrivals.Count; i++) idlerGaps.Add(idlerArrivals[i] - idlerArrivals[i - 1]);

            gaps.Sort();
            cadence.Sort();
            idlerGaps.Sort();
            return new Reading(
                profile, jitterMs, arrivals.Count, gaps.Count,
                Pick(gaps, 0.50), Pick(gaps, 0.95), Pick(gaps, 0.99), gaps.Count > 0 ? gaps[^1] : 0,
                Pick(cadence, 0.50), Pick(cadence, 0.99), snapshots,
                idlerGaps.Count, idlerGaps.Count > 0 ? idlerGaps[^1] : 0);
        }
        finally
        {
            cts.Cancel();
            foreach (var c in moverSockets) { try { c.Close(); } catch { } }
            foreach (var t in moverTasks) { try { await t; } catch { } }
            await server.ShutdownAsync();
            try { await runTask; } catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }

    private static double Pick(List<double> sorted, double q) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];

    /// <summary>
    /// A mover walks in a circle. Position change alone does NOT raise the importance score
    /// — <c>ReplicationImportance.Score</c> counts `change` only for HP and action — so a
    /// walking, non-combat player scores distance + type and lands in the schedule's bottom
    /// band, which is the ordinary case this measurement is about.
    /// </summary>
    private static async Task MoveForeverAsync(NetworkStream stream, CancellationToken ct)
    {
        ulong t = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                double a = t * 0.05;
                await WriteFrameAsync(stream, WireProtocol.NewEnvelope(MsgType.Input,
                    new InputMessage { Tick = ++t, MoveX = (float)Math.Cos(a), MoveY = (float)Math.Sin(a) },
                    WireEncoding.Json), ct);
                await Task.Delay(66, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task JoinAsync(NetworkStream stream, string userId, CancellationToken ct)
    {
        await WriteFrameAsync(stream, WireProtocol.NewEnvelope(MsgType.JoinToken,
            new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
            WireEncoding.Json), ct);
        var env = await WireProtocol.DecodeAsync(stream, ct);
        Assert.NotNull(env);
        var resp = WireProtocol.GetPayload<JoinTokenResponse>(env!);
        Assert.True(resp.Ok, resp.Error);
    }

    private static async Task WriteFrameAsync(NetworkStream stream, GameServer.Net.Envelope env, CancellationToken ct)
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
}
