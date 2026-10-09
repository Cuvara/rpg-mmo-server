using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Server;
using GameServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using RpgMmo.Wire.V1;

namespace GameServer.Tests.Server;

/// <summary>
/// The gameplay hop end to end over KCP/UDP, the only transport it has: a real
/// <see cref="GameServerHost"/> on a UDP port and a real <see cref="KcpTestClient"/>. These
/// cover what the TCP-era suite took from the socket for free — connect, EOF, a peer that
/// vanishes, a peer that restarts, a server that restarts — and what KCP adds: datagrams
/// that are not KCP, sessions nobody authenticates, and flood caps.
/// </summary>
public class KcpGameplayTests
{
    private static GameMetrics NewMetrics() => new(HardeningHarness.MapId, $"test.{Guid.NewGuid():N}");

    private static async Task<SnapshotMessage> ReadSnapshotWithAsync(Stream stream, string userId, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var env = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(env);
            if ((MsgType)env!.Type != MsgType.Snapshot) continue;
            var snap = WireProtocol.GetPayload<SnapshotMessage>(env);
            if (snap.Entities.Any(e => e.Id == userId)) return snap;
        }
    }

    private static async Task<JoinTokenResponse> SendTokenAsync(KcpTestClient client, string token)
    {
        var stream = client.GetStream();
        var join = WireProtocol.NewEnvelope(MsgType.JoinToken,
            new JoinTokenRequest { Token = token, ProtocolVersion = WireProtocol.ProtocolVersion }, WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(join));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env);
        Assert.Equal((uint)MsgType.JoinTokenResp, env!.Type);
        return WireProtocol.GetPayload<JoinTokenResponse>(env);
    }

    private static async Task AssertPingAnsweredAsync(KcpTestClient client)
    {
        var stream = client.GetStream();
        var ping = WireProtocol.NewEnvelope(MsgType.Ping, new PingMessage { Timestamp = 4242 }, WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(ping));
        var pong = await HardeningHarness.ReadUntilAsync(stream, MsgType.Pong, TimeSpan.FromSeconds(10));
        Assert.Equal(4242, WireProtocol.GetPayload<PongMessage>(pong).Timestamp);
    }

    // ── Join ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Join_OverKcp_EntersTheWorld_AndSnapshotsFlow()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);

        using var client = await h.JoinAsync("kcp-joiner");
        var snap = await ReadSnapshotWithAsync(client.GetStream(), "kcp-joiner");

        Assert.Contains(snap.Entities, e => e.Id == "kcp-joiner");
        await h.WaitForAsync(() => metrics.PlayersOnline == 1, what: "players_online = 1");
        Assert.NotNull(client.ServerSession);
        Assert.Equal(1, h.Server.TransportStats!.SessionsCreated);
        Assert.Equal(1, h.Server.TransportStats.SessionsLive);
    }

    [Fact]
    public async Task InvalidJoinToken_IsRefused_AndTheSessionLeavesTheListener()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);

        using var client = await h.ConnectAsync();
        var resp = await HardeningHarness.SendJoinBadTokenAsync(client, WireProtocol.ProtocolVersion);

        Assert.False(resp.Ok);
        Assert.True(await HardeningHarness.ObservesEofAsync(client, TimeSpan.FromSeconds(5)), "refused session was not closed");
        await h.WaitForAsync(() => h.Server.TransportStats!.SessionsLive == 0, what: "listener table empty");
        Assert.Null(client.ServerSession);
        Assert.Equal(0, metrics.PlayersOnline);
    }

    [Fact]
    public async Task ExpiredJoinToken_IsRefused()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);

        using var client = await h.ConnectAsync();
        var resp = await SendTokenAsync(client,
            TestHelpers.CreateExpiredJwt("kcp-expired", HardeningHarness.ServerId, HardeningHarness.JwtSecret));

        Assert.False(resp.Ok);
        Assert.True(await HardeningHarness.ObservesEofAsync(client, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, metrics.PlayersOnline);
    }

    [Fact]
    public async Task JoinTokenForAnotherServer_IsRefused()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);

        using var client = await h.ConnectAsync();
        var resp = await SendTokenAsync(client,
            TestHelpers.CreateTestJwt("kcp-wrong-server", "gs-somewhere-else", HardeningHarness.JwtSecret));

        Assert.False(resp.Ok);
        Assert.True(await HardeningHarness.ObservesEofAsync(client, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, metrics.PlayersOnline);
    }

    /// <summary>
    /// A KCP session costs nothing to open — one datagram — so one that never authenticates
    /// must be bounded by the handshake deadline and must leave the listener's table, not
    /// just the pending-handshake pool.
    /// </summary>
    [Fact]
    public async Task SessionThatNeverSendsAJoinToken_IsClosedAndLeavesTheListenerTable()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics, handshakeTimeout: TimeSpan.FromMilliseconds(300));

        using var silent = await h.ConnectAsync();
        await h.WaitForAsync(() => h.Server.PendingHandshakes == 1 && h.Server.TransportStats!.SessionsLive == 1,
            what: "one pending session");

        Assert.True(await HardeningHarness.ObservesEofAsync(silent, TimeSpan.FromSeconds(5)));
        await h.WaitForAsync(() => h.Server.TransportStats!.SessionsLive == 0 && h.Server.PendingHandshakes == 0,
            what: "session removed from the listener");
        Assert.Null(silent.ServerSession);
        Assert.Equal(1, metrics.HandshakesRejectedTimeout);
    }

    // ── Malformed input on a live session ────────────────────────────────────

    [Fact]
    public async Task UnknownMessageType_AfterJoin_IsIgnored_AndTheSessionStaysUp()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);
        using var client = await h.JoinAsync("kcp-unknown-type");

        // A well-formed envelope (valid JSON payload) whose type this server does not know.
        var odd = new GameServer.Net.Envelope { Type = 0xEE, Payload = "{}"u8.ToArray(), Encoding = WireEncoding.Json };
        await client.GetStream().WriteAsync(WireProtocol.Encode(odd));

        await AssertPingAnsweredAsync(client);
        Assert.Equal(1, metrics.PlayersOnline);
    }

    [Fact]
    public async Task NonKcpDatagrams_FromAJoinedPeer_AreDroppedAndCounted_AndTheSessionSurvives()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);
        using var client = await h.JoinAsync("kcp-garbage");
        var stats = h.Server.TransportStats!;

        client.SendRaw(new byte[10]);                 // shorter than a KCP header
        client.SendRaw(new byte[2000]);               // above kcp-go's 1500-byte mtuLimit
        var foreign = new byte[Kcp.Overhead];         // another conversation, mid-stream
        BinaryPrimitives.WriteUInt32LittleEndian(foreign, client.Conv ^ 0x5A5A5A5A);
        foreign[4] = 81;
        BinaryPrimitives.WriteUInt32LittleEndian(foreign.AsSpan(12), 9);
        client.SendRaw(foreign);

        await h.WaitForAsync(() => stats.DatagramsDroppedUndersize == 1 && stats.DatagramsDroppedOversize == 1
                                   && stats.DatagramsDroppedConvMismatch == 1, what: "three drops counted");
        await AssertPingAnsweredAsync(client);
        Assert.Equal(1, stats.SessionsLive);
    }

    [Fact]
    public async Task OversizedLengthPrefix_ClosesTheConnection()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics);
        using var client = await h.JoinAsync("kcp-huge-frame");

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, 64 * 1024 * 1024); // far past WireProtocol.MaxMessageSize
        await client.GetStream().WriteAsync(prefix);

        Assert.True(await HardeningHarness.ObservesEofAsync(client, TimeSpan.FromSeconds(10)),
            "a frame announcing 64 MiB did not close the connection");
        await h.WaitForAsync(() => metrics.PlayersOnline == 0, what: "player gone");
    }

    // ── Disconnect, reconnect, restart ───────────────────────────────────────

    /// <summary>
    /// KCP has no FIN and no RST. A client that simply stops is found by silence — the idle
    /// timeout, or the dead-link limit on the snapshots it no longer acknowledges — and its
    /// player must then leave exactly as a closed socket's did.
    /// </summary>
    [Fact]
    public async Task VanishedClient_IsDetectedBySilence_AndThePlayerLeaves()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics,
            kcpLimits: KcpListenerOptions.Default with { IdleTimeoutMs = 800 });

        var client = await h.JoinAsync("kcp-vanisher");
        await h.WaitForAsync(() => metrics.PlayersOnline == 1, what: "joined");

        client.Abort();

        await h.WaitForAsync(() => metrics.PlayersOnline == 0 && h.Server.TransportStats!.SessionsLive == 0,
            TimeSpan.FromSeconds(10), "vanished client detected");
        var stats = h.Server.TransportStats!;
        Assert.True(stats.SessionsClosedIdle + stats.SessionsClosedDeadLink >= 1, stats.ToString());
    }

    [Fact]
    public async Task RestartedClient_FromTheSameEndpoint_ReplacesItsSession_AndRejoins()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics, hold: TimeSpan.FromSeconds(5));

        var first = await h.JoinAsync("kcp-restarter");
        int localPort = first.LocalEndPoint.Port;
        uint firstConv = first.Conv;
        first.Abort(); // the process died; its address and port come back with a new conv

        using var second = new KcpTestClient(localPort: localPort);
        Assert.NotEqual(firstConv, second.Conv);
        await second.ConnectAsync(IPAddress.Loopback, h.Port);
        var resp = await HardeningHarness.SendJoinAsync(second, "kcp-restarter");

        Assert.True(resp.Ok, resp.Error);
        Assert.Equal(1, h.Server.TransportStats!.SessionsReplaced);
        await h.WaitForAsync(() => metrics.PlayersOnline == 1 && h.Server.TransportStats.SessionsLive == 1,
            what: "exactly one live player and session");
        await AssertPingAnsweredAsync(second);
    }

    /// <summary>
    /// Shutdown must reach clients over KCP: every session shares the listener's one UDP
    /// socket, so the listener is closed only after the shutdown notice has been sent. A
    /// new server on the same UDP port then serves a fresh client.
    /// </summary>
    [Fact]
    public async Task ServerRestart_DeliversTheShutdownNotice_AndANewServerOnTheSamePortServes()
    {
        using var metrics = NewMetrics();
        var h = await HardeningHarness.StartAsync(metrics);
        int port = h.Port;

        using (var client = await h.JoinAsync("kcp-before-restart"))
        {
            var shutdown = h.DisposeAsync().AsTask();
            var notice = await HardeningHarness.ReadUntilAsync(client.GetStream(), MsgType.Disconnect, TimeSpan.FromSeconds(10));
            Assert.Contains("server_shutdown", System.Text.Encoding.UTF8.GetString(notice.Payload));
            await shutdown;
            Assert.True(await HardeningHarness.ObservesEofAsync(client, TimeSpan.FromSeconds(5)));
        }

        var server = new GameServerHost(new ServerOptions
        {
            ServerAddr = $":{port}",
            ServerId = HardeningHarness.ServerId,
            MapId = HardeningHarness.MapId,
            TickRate = 20,
            Capacity = 8,
            JwtSecret = HardeningHarness.JwtSecret,
            JoinTokenSecret = HardeningHarness.JwtSecret,
            SaveInterval = TimeSpan.FromHours(1),
            PlayerStore = new MemoryPlayerStore(),
            LoggerFactory = NullLoggerFactory.Instance,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = server.RunAsync($":{port}", cts.Token);
        try
        {
            await server.ListeningAddressAsync.WaitAsync(TimeSpan.FromSeconds(10));
            using var fresh = new KcpTestClient();
            await fresh.ConnectAsync(IPAddress.Loopback, port);
            var resp = await HardeningHarness.SendJoinAsync(fresh, "kcp-after-restart");
            Assert.True(resp.Ok, resp.Error);
        }
        finally
        {
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            await server.DisposeAsync();
        }
    }

    // ── Flood caps through the real server ───────────────────────────────────

    [Fact]
    public async Task SessionCap_RefusesExtraSessions_AndIsReportedOnMetrics()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics,
            kcpLimits: KcpListenerOptions.Default with { MaxSessions = 3 });

        var admitted = new List<KcpTestClient>();
        try
        {
            for (int i = 0; i < 3; i++) admitted.Add(await h.ConnectAsync());

            using var refused = new KcpTestClient { ConnectTimeout = TimeSpan.FromMilliseconds(400) };
            await Assert.ThrowsAsync<SocketException>(() => refused.ConnectAsync(IPAddress.Loopback, h.Port));

            var stats = h.Server.TransportStats!;
            Assert.Equal(3, stats.SessionsLive);
            Assert.True(stats.SessionsRejectedGlobalCap >= 1, stats.ToString());

            // And the same numbers on the Prometheus instruments, not only on the object.
            long rejectedGlobal = 0;
            int live = -1;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == metrics.MeterName && inst.Name.StartsWith("gameserver.kcp.", StringComparison.Ordinal))
                    l.EnableMeasurementEvents(inst);
            };
            listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
            {
                if (inst.Name != "gameserver.kcp.sessions.rejected") return;
                foreach (var t in tags)
                    if (t.Key == "reason" && (string?)t.Value == "global_cap") rejectedGlobal = value;
            });
            listener.SetMeasurementEventCallback<int>((inst, value, _, _) =>
            {
                if (inst.Name == "gameserver.kcp.sessions") live = value;
            });
            listener.Start();
            listener.RecordObservableInstruments();

            Assert.Equal(3, live);
            Assert.Equal(stats.SessionsRejectedGlobalCap, rejectedGlobal);
        }
        finally
        {
            foreach (var c in admitted) c.Dispose();
        }
    }
}
