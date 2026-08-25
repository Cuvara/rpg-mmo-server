using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Persistence;
using GameServer.Server;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Tests.Infrastructure;
using RpgMmo.Wire.V1;
using Xunit.Abstractions;

namespace GameServer.Tests.Server;

/// <summary>
/// What a client actually receives, measured at a socket: the snapshot cadence, the tick
/// gaps and the arrival spacing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A Unity client was measured decoding <b>13.6–13.8 snapshots per
/// second</b> from a server whose own counters said 15, and nothing in either repo could say
/// which side had lost them. `snapshots_sent_total` counts stagings, `frames_written_total`
/// counts socket writes, and neither is a statement about what came out the other end — so
/// the question could only be settled by a client that is not the client under suspicion.
/// </para>
/// <para>
/// This is that client: a plain socket, no Unity, no frame loop, reading in a tight loop. It
/// measured <b>15.000/s and 60.00 base ticks/s with every gap exactly 4</b>, at one client
/// and at two, which exonerated the server and moved the investigation to the Unity read
/// path where the loss actually was.
/// </para>
/// <para>
/// Kept as a test rather than deleted as a probe because the next person to see a client
/// reporting a low rate will otherwise repeat all of it. It is the reference the server is
/// measured against, and it fails if the server ever stops emitting on the world tick.
/// </para>
/// </remarks>
public class SnapshotCadenceTests
{
    private const string JwtSecret = "cadence-probe-secret-32-bytes-aaa";
    private const string ServerId = "gs-cadence-probe";

    /// <summary>Seconds discarded before measuring: the join keyframe and the phase-in.</summary>
    private const double WarmupSeconds = 2.0;

    /// <summary>
    /// Seconds measured. Long enough that one late arrival cannot move the rate by more than
    /// the tolerance, short enough that the suite does not pay twenty seconds for it.
    /// </summary>
    private const double WindowSeconds = 6.0;
    private readonly ITestOutputHelper _out;
    public SnapshotCadenceTests(ITestOutputHelper o) => _out = o;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SnapshotCadenceOverASocket(int clients)
    {
        Assert.True(SimulationRates.TryCreate(60, 15, 5, out var rates, out string? err), err);
        var options = new ServerOptions
        {
            ServerAddr = ":0", ServerId = ServerId, MapId = "map_cadence", Mode = "map",
            Transport = TransportKind.Tcp, TickRate = rates!.CriticalHz, SimulationRates = rates,
            Capacity = 8, JwtSecret = JwtSecret, JoinTokenSecret = JwtSecret,
            SaveInterval = TimeSpan.FromSeconds(300), PlayerStore = new MemoryPlayerStore(),
            LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

        var socks = new List<TcpClient>();
        var tasks = new List<Task<Reading>>();
        try
        {
            for (int i = 0; i < clients; i++)
            {
                string userId = $"cad{i}-{Guid.NewGuid():N}"[..14];
                var c = new TcpClient(); socks.Add(c);
                await ConnectWithRetryAsync(c, port);
                var stream = c.GetStream();
                await WriteFrameAsync(stream, WireProtocol.NewEnvelope(MsgType.JoinToken,
                    new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
                    WireEncoding.Json));
                var joinEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
                Assert.NotNull(joinEnv);
                Assert.True(WireProtocol.GetPayload<JoinTokenResponse>(joinEnv!).Ok);
                int idx = i;
                tasks.Add(Task.Run(() => ProbeAsync(stream, idx, cts.Token), cts.Token));
            }

            await Task.Delay(TimeSpan.FromSeconds(WarmupSeconds + WindowSeconds + 1), cts.Token);

            foreach (var r in await Task.WhenAll(tasks))
            {
                _out.WriteLine(r.ToString());

                // The world group runs every 4th base tick at 60/15/5, so a client that is
                // being served correctly sees one snapshot per world tick and nothing else.
                // The tolerance is one snapshot in the window, not a fraction of a rate:
                // this is a wall-clock test and the last arrival can land either side of the
                // boundary, but a client that is genuinely missing frames misses many.
                Assert.InRange(r.Rate, 15.0 - 1.0 / WindowSeconds, 15.0 + 1.0 / WindowSeconds);

                // The tick STREAM is the part a client's clock is steered by, and it is the
                // one that has to be right even when a frame is late: ticks are stamped by
                // the server, so a gap of 8 means a snapshot never arrived rather than that
                // one arrived late.
                Assert.InRange(r.TickHz, 60.0 - 4.0 / WindowSeconds, 60.0 + 4.0 / WindowSeconds);

                Assert.True(r.GapsAllFour,
                    $"snapshot tick gaps were not all 4: {r.Gaps}. A gap of 8 is a snapshot " +
                    "that never reached the socket; a gap of 4 that arrives late is jitter " +
                    "and shows up in the inter-arrival spread instead.");
            }
        }
        finally
        {
            cts.Cancel();
            foreach (var s in socks) { try { s.Close(); } catch { } }
            await server.ShutdownAsync();
            try { await runTask; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>What one socket saw over the measurement window.</summary>
    private readonly record struct Reading(
        int Index, double WindowSeconds, long Snapshots, double Rate, ulong Ticks, double TickHz,
        long Frames, double MedianInterArrivalMs, double P99InterArrivalMs, string Gaps, bool GapsAllFour)
    {
        public override string ToString() =>
            $"[probe{Index}] window={WindowSeconds:F2}s snapshots={Snapshots} rate={Rate:F3}/s " +
            $"ticks={Ticks} tickHz={TickHz:F2} totalFrames={Frames} " +
            $"medIA={MedianInterArrivalMs:F1}ms p99IA={P99InterArrivalMs:F1}ms gaps=[{Gaps}]";
    }

    private static async Task<Reading> ProbeAsync(NetworkStream stream, int idx, CancellationToken ct)
    {
        // Warm up: skip the first 3 seconds (join keyframe / phase-in).
        var sw = Stopwatch.StartNew();
        long frames = 0, snaps = 0;
        ulong firstTick = 0, lastTick = 0;
        double windowStart = -1, windowEnd = 0;
        long snapsInWindow = 0;
        var gaps = new Dictionary<ulong, int>();
        var interArrival = new List<double>();
        double prevArr = -1;
        ulong prevTick = 0;
        ulong inputTick = 0;
        var lastInput = Stopwatch.StartNew();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // pump input at ~15Hz from this same task between reads (cheap: server
                // side movement is not what we measure, but keeps the client realistic)
                if (lastInput.ElapsedMilliseconds >= 66)
                {
                    lastInput.Restart();
                    _ = WriteFrameAsync(stream, WireProtocol.NewEnvelope(MsgType.Input,
                        new InputMessage { Tick = ++inputTick, MoveX = 1f, MoveY = 0f }, WireEncoding.Json));
                }

                var env = await WireProtocol.DecodeAsync(stream, ct);
                if (env == null) break;
                frames++;
                if ((MsgType)env.Type != MsgType.Snapshot) continue;
                var msg = WireProtocol.GetPayload<SnapshotMessage>(env);
                snaps++;
                double now = sw.Elapsed.TotalSeconds;
                if (now < WarmupSeconds) { prevTick = msg.Tick; prevArr = now; continue; }
                if (windowStart < 0) { windowStart = now; firstTick = msg.Tick; prevTick = msg.Tick; prevArr = now; continue; }
                snapsInWindow++;
                windowEnd = now; lastTick = msg.Tick;

                // The probe closes its own window rather than waiting to be cancelled. It
                // used to read for as long as the caller's delay plus however long the
                // shutdown took, which made the measured window 28 s for a constant that
                // said 6 -- the reading was right and the cost was four times what the test
                // claimed to spend.
                if (now - windowStart >= WindowSeconds) break;
                ulong g = msg.Tick - prevTick;
                gaps[g] = gaps.TryGetValue(g, out int n) ? n + 1 : 1;
                interArrival.Add(now - prevArr);
                prevTick = msg.Tick; prevArr = now;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        double win = windowEnd - windowStart;
        var gapStr = string.Join(" ", gaps.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}"));
        interArrival.Sort();
        double med = interArrival.Count > 0 ? interArrival[interArrival.Count / 2] : 0;
        double p99 = interArrival.Count > 0 ? interArrival[(int)(interArrival.Count * 0.99)] : 0;
        Assert.True(win > 0, "the probe never completed a measurement window");

        return new Reading(
            idx, win, snapsInWindow, snapsInWindow / win,
            lastTick - firstTick, (lastTick - firstTick) / win,
            frames, med * 1000, p99 * 1000, gapStr,
            gaps.Count > 0 && gaps.Keys.All(k => k == 4));
    }

    private static async Task WriteFrameAsync(Stream stream, GameServer.Net.Envelope env)
    {
        byte[] frame = WireProtocol.Encode(env);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        for (int a = 0; a < 50; a++)
        {
            try { await client.ConnectAsync(IPAddress.Loopback, port); return; }
            catch (SocketException) { await Task.Delay(100); }
        }
        throw new TimeoutException("no listener");
    }
}
