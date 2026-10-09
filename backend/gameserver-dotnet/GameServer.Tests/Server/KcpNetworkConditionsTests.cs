using System.Diagnostics;
using System.Net;
using GameServer.Net;
using GameServer.Observability;
using GameServer.Tests.Infrastructure;
using RpgMmo.Wire.V1;
using Xunit.Abstractions;

namespace GameServer.Tests.Server;

/// <summary>
/// The gameplay hop over a bad UDP link: loss, delay with jitter, reordering and
/// duplication, injected per datagram by <see cref="AdversityProxy"/> between a real
/// <see cref="KcpTestClient"/> and a real server. Every arm must still apply the player's
/// movement input and keep snapshots arriving — that is KCP's ARQ doing its job on both
/// ends with the production profile, which no TCP-era test could exercise.
/// </summary>
/// <remarks>
/// Each arm also asserts that the impairment actually happened (the proxy's own counters),
/// so a proxy that silently passed everything through would fail rather than pass. Runs in
/// the adversity collection, alone, for the reason given there: these are wall-clock
/// measurements.
/// </remarks>
[Collection(AdversityCollection.Name)]
public class KcpNetworkConditionsTests(ITestOutputHelper output)
{
    private const int Seed = 20261008;
    private const double MoveSeconds = 3.0;

    public static TheoryData<string> Conditions() => new() { "loss", "delay_jitter", "reorder", "duplicate", "everything" };

    private static AdversityProxy.Link LinkFor(string condition) => condition switch
    {
        "loss" => new AdversityProxy.Link(15, 5, LossRate: 0.08),
        "delay_jitter" => new AdversityProxy.Link(60, 40),
        "reorder" => new AdversityProxy.Link(30, 25, Reorder: true),
        "duplicate" => new AdversityProxy.Link(10, 5, DuplicateRate: 0.10),
        "everything" => new AdversityProxy.Link(40, 20, LossRate: 0.05, DuplicateRate: 0.05, Reorder: true),
        _ => throw new ArgumentOutOfRangeException(nameof(condition)),
    };

    /// <summary>Everything the reader saw, written by the reader task only.</summary>
    private sealed class Observed
    {
        public int Snapshots;
        public float LastX = float.NaN;
        public float Speed;
        public volatile bool KeyframeAfterResync;
        public volatile bool ResyncSent;
        public float KeyframeX = float.NaN;
    }

    [Theory]
    [MemberData(nameof(Conditions))]
    public async Task MovementIsApplied_AndSnapshotsKeepArriving(string condition)
    {
        using var metrics = new GameMetrics(HardeningHarness.MapId, $"test.{Guid.NewGuid():N}");
        await using var h = await HardeningHarness.StartAsync(metrics, hold: TimeSpan.FromSeconds(5));
        var link = LinkFor(condition);
        await using var proxy = AdversityProxy.Start(h.Port, AdversityProxy.Profile.Symmetric(link, Seed));

        string userId = $"net-{condition}";
        using var client = new KcpTestClient { ConnectTimeout = TimeSpan.FromSeconds(10) };
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var join = await HardeningHarness.SendJoinAsync(client, userId, TimeSpan.FromSeconds(20));
        Assert.True(join.Ok, join.Error);

        var stream = client.GetStream();
        var seen = new Observed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var reader = Task.Run(() => ReadAsync(stream, userId, seen, cts.Token));

        // Spawn position: the first snapshot carrying the player.
        var sw = Stopwatch.StartNew();
        while (float.IsNaN(Volatile.Read(ref seen.LastX)))
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"[{condition}] the player never appeared. {proxy.Stats}");
            await Task.Delay(10);
        }
        float startX = Volatile.Read(ref seen.LastX);
        int snapshotsAtStart = Volatile.Read(ref seen.Snapshots);

        // Hold +X at 15Hz, then release explicitly.
        int packets = (int)(MoveSeconds * 15);
        sw.Restart();
        for (int p = 1; p <= packets; p++)
        {
            await HardeningHarness.SendInputAsync(stream, (ulong)p, 1f, 0f);
            int wait = p * (1000 / 15) - (int)sw.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait);
        }
        await HardeningHarness.SendInputAsync(stream, (ulong)(packets + 1), 0f, 0f);
        await Task.Delay(600);

        // Final position from a keyframe, not from whichever delta arrived last.
        seen.ResyncSent = true;
        await stream.WriteAsync(WireProtocol.Encode(new GameServer.Net.Envelope
        {
            Type = (byte)MsgType.Resync, Payload = Array.Empty<byte>(), Encoding = WireEncoding.Json,
        }));
        sw.Restart();
        while (!seen.KeyframeAfterResync)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"[{condition}] no keyframe after the resync. {proxy.Stats}");
            await Task.Delay(10);
        }

        int snapshots = Volatile.Read(ref seen.Snapshots) - snapshotsAtStart;
        float travelled = seen.KeyframeX - startX;
        float owed = (float)(seen.Speed * MoveSeconds);
        output.WriteLine($"[{condition}] link={link} travelled={travelled:F3} owed={owed:F3} snapshots={snapshots} proxy={proxy.Stats}");

        cts.Cancel();
        try { await reader; } catch { /* cancelled */ }

        // The impairment really happened.
        switch (condition)
        {
            case "loss": Assert.True(proxy.Stats.Lost > 0, proxy.Stats.ToString()); break;
            case "duplicate": Assert.True(proxy.Stats.Duplicated > 0, proxy.Stats.ToString()); break;
            case "reorder": Assert.True(proxy.Stats.Reordered > 0, proxy.Stats.ToString()); break;
            case "everything":
                Assert.True(proxy.Stats.Lost > 0 && proxy.Stats.Duplicated > 0 && proxy.Stats.Reordered > 0, proxy.Stats.ToString());
                break;
        }

        // Movement input reached the simulation. Loose on purpose — retransmission delays
        // inputs past the held-movement budget now and then — but a link that lost the
        // input stream would leave the player near spawn.
        Assert.True(travelled >= owed * 0.5f,
            $"[{condition}] the player moved {travelled:F3} of {owed:F3} owed over the impaired link. {proxy.Stats}");
        Assert.True(travelled <= owed * 1.25f,
            $"[{condition}] the player moved {travelled:F3}, more than {owed:F3} owed: duplicated input applied twice? {proxy.Stats}");

        // Snapshots kept arriving across the window (the server ticks at 20Hz).
        Assert.True(snapshots >= 20, $"[{condition}] only {snapshots} snapshots in {MoveSeconds + 0.6}s. {proxy.Stats}");

        // The session survived: still one player, still one session.
        Assert.Equal(1, metrics.PlayersOnline);
        Assert.Equal(1, h.Server.TransportStats!.SessionsLive);
    }

    private static async Task ReadAsync(Stream stream, string userId, Observed seen, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var env = await WireProtocol.DecodeAsync(stream, ct);
                if (env == null) return;
                if ((MsgType)env.Type != MsgType.Snapshot) continue;
                var snap = WireProtocol.GetPayload<SnapshotMessage>(env);
                Interlocked.Increment(ref seen.Snapshots);
                foreach (var e in snap.Entities)
                {
                    if (e.Id != userId) continue;
                    if (e.Speed > 0) seen.Speed = e.Speed;
                    Volatile.Write(ref seen.LastX, e.X);
                    if (snap.Full && seen.ResyncSent && !seen.KeyframeAfterResync)
                    {
                        seen.KeyframeX = e.X;
                        seen.KeyframeAfterResync = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
