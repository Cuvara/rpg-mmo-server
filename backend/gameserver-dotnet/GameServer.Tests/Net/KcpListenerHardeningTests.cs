using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GameServer.Net.Transport;
using GameServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Net;

/// <summary>
/// The KCP listener's defences, driven with hand-built datagrams and real clients: what a
/// hostile or broken peer can make it do, and what every refusal costs it. Each case asserts
/// the counter that makes the refusal visible, because a drop nobody can see is the defect
/// these replace (a 1500-byte receive buffer that silently truncated larger datagrams, an
/// ignored <c>Kcp.Send</c> return code, unbounded queues, and a session for any datagram).
/// </summary>
public class KcpListenerHardeningTests
{
    private const string TestKeyHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const byte CmdPush = 81, CmdAck = 82;

    private static KcpListener Listen(KcpListenerOptions? limits = null, string key = "") =>
        new(new IPEndPoint(IPAddress.Loopback, 0), key, NullLogger.Instance, limits ?? KcpListenerOptions.Default);

    /// <summary>One KCP segment, little-endian, as kcp-go encodes it.</summary>
    private static byte[] Segment(uint conv, byte cmd, uint sn, int payload = 0, int declaredLength = -1, int totalSize = -1)
    {
        int size = totalSize >= 0 ? totalSize : Kcp.Overhead + payload;
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, conv);
        b[4] = cmd;
        b[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), sn);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), (uint)(declaredLength >= 0 ? declaredLength : payload));
        return b;
    }

    private static Socket RawSocket(IPAddress? bind = null)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            try { s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch (SocketException) { }
        }
        s.Bind(new IPEndPoint(bind ?? IPAddress.Loopback, 0));
        return s;
    }

    private static void Send(Socket s, KcpListener l, byte[] datagram) =>
        s.SendTo(datagram, new IPEndPoint(IPAddress.Loopback, l.LocalEndPoint.Port));

    private static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"{what} not reached within {timeoutMs}ms");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Give the receive loop time to process what was sent, then assert a stable state. A
    /// marker datagram from a second socket (a valid opener) is sent last and waited for, so
    /// "nothing happened" is asserted after the loop provably got past the earlier datagrams.
    /// </summary>
    private static async Task Drain(KcpListener l)
    {
        using var marker = RawSocket();
        long before = l.Stats.SessionsCreated;
        Send(marker, l, Segment(0x7EAD7EAD, CmdPush, 0));
        await Eventually(() => l.Stats.SessionsCreated > before, "marker session");
    }

    // ── Malformed and oversized datagrams ───────────────────────────────────

    [Fact]
    public async Task OversizedDatagram_IsDroppedUnparsed_AndCounted()
    {
        using var l = Listen();
        using var s = RawSocket();

        // A well-formed opener header followed by enough payload to exceed kcp-go's 1500-byte
        // mtuLimit. The old 1500-byte receive buffer truncated this to its first 1500 bytes
        // and parsed the prefix as a session opener; now it must not create anything.
        Send(s, l, Segment(0x1001, CmdPush, 0, payload: 2000));
        await Drain(l);

        Assert.Equal(1, l.Stats.DatagramsDroppedOversize);
        Assert.False(l.HasSession((IPEndPoint)s.LocalEndPoint!));
        Assert.Equal(1, l.Stats.SessionsCreated); // the marker only
    }

    /// <summary>
    /// The receive thread's own accounting: every datagram taken off the socket is counted,
    /// dropped ones included, and the time spent handling them accumulates. Its rate is the
    /// thread utilisation exported as <c>gameserver_kcp_receive_busy_seconds_total</c>.
    /// </summary>
    [Fact]
    public async Task ReceiveThread_CountsDatagramsAndBusyTime()
    {
        using var l = Listen();
        using var s = RawSocket();
        long before = l.Stats.DatagramsReceived;
        Send(s, l, new byte[Kcp.Overhead - 1]);
        Send(s, l, new byte[1]);
        await Drain(l);

        Assert.True(l.Stats.DatagramsReceived - before >= 2,
            $"received {l.Stats.DatagramsReceived - before}, want at least the 2 datagrams sent");
        Assert.True(l.Stats.ReceiveBusyTicks > 0);
    }

    [Fact]
    public async Task UndersizedDatagram_IsDroppedAndCounted()
    {
        using var l = Listen();
        using var s = RawSocket();
        Send(s, l, new byte[Kcp.Overhead - 1]);
        Send(s, l, new byte[1]);
        await Drain(l);

        Assert.Equal(2, l.Stats.DatagramsDroppedUndersize);
        Assert.False(l.HasSession((IPEndPoint)s.LocalEndPoint!));
    }

    [Fact]
    public async Task Garbage_FromAnUnknownEndpoint_OpensNothing()
    {
        using var l = Listen();
        using var s = RawSocket();
        var rng = new Random(20261008);

        for (int i = 0; i < 50; i++)
        {
            var junk = new byte[rng.Next(Kcp.Overhead, 1400)];
            rng.NextBytes(junk);
            junk[4] = 0x11; // never a PUSH: random bytes that happen to open a session are the next test
            Send(s, l, junk);
        }
        // An ACK and a retransmission for a conversation the listener never saw: the shape
        // of a peer whose session already ended. Neither may resurrect it.
        Send(s, l, Segment(0x2002, CmdAck, 3));
        Send(s, l, Segment(0x2002, CmdPush, 7, payload: 10));
        await Drain(l);

        Assert.False(l.HasSession((IPEndPoint)s.LocalEndPoint!));
        Assert.Equal(52, l.Stats.DatagramsDroppedNoSession);
    }

    [Fact]
    public async Task MalformedOpener_IsCountedAndCreatesNoSession()
    {
        using var l = Listen();
        using var s = RawSocket();

        // PUSH sn=0, but the header claims 500 payload bytes and only 10 follow.
        Send(s, l, Segment(0x3003, CmdPush, 0, payload: 10, declaredLength: 500));
        await Drain(l);

        Assert.Equal(1, l.Stats.DatagramsDroppedMalformed);
        Assert.False(l.HasSession((IPEndPoint)s.LocalEndPoint!));
    }

    [Fact]
    public async Task ForeignConversation_MidStream_IsDropped_AndTheSessionSurvives()
    {
        using var l = Listen();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = l.AcceptAsync(cts.Token);
        using var client = new KcpTestClient();
        await client.ConnectAsync(IPAddress.Loopback, l.LocalEndPoint.Port, cts.Token);
        var session = await accept;

        // Same endpoint, another conversation id, not an opener (sn != 0): a stray.
        client.SendRaw(Segment(client.Conv ^ 0xFFFF, CmdPush, 5, payload: 4));
        await Eventually(() => l.Stats.DatagramsDroppedConvMismatch == 1, "conv mismatch counted");

        Assert.False(session.IsClosed);
        await client.GetStream().WriteAsync("still-here"u8.ToArray(), cts.Token);
        var chunk = await session.ReadChunkAsync(cts.Token);
        Assert.Equal("still-here", System.Text.Encoding.UTF8.GetString(chunk!));
    }

    [Fact]
    public async Task WrongTransportKey_IsDroppedAsBadCrypto()
    {
        using var l = Listen(key: TestKeyHex);
        using var plain = new KcpTestClient();          // no key
        using var wrong = new KcpTestClient("other");   // a different key
        plain.ConnectTimeout = TimeSpan.FromMilliseconds(300);
        wrong.ConnectTimeout = TimeSpan.FromMilliseconds(300);

        await Assert.ThrowsAsync<SocketException>(() => plain.ConnectAsync(IPAddress.Loopback, l.LocalEndPoint.Port));
        await Assert.ThrowsAsync<SocketException>(() => wrong.ConnectAsync(IPAddress.Loopback, l.LocalEndPoint.Port));

        Assert.True(l.Stats.DatagramsDroppedBadCrypto >= 2, l.Stats.ToString());
        Assert.Equal(0, l.SessionCount);

        using var right = new KcpTestClient(TestKeyHex);
        await right.ConnectAsync(IPAddress.Loopback, l.LocalEndPoint.Port);
        Assert.Equal(1, l.SessionCount);
    }

    // ── Session replacement ──────────────────────────────────────────────────

    [Fact]
    public async Task NewConversation_FromTheSameEndpoint_ReplacesTheSession()
    {
        using var l = Listen();
        using var s = RawSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Send(s, l, Segment(0xA1, CmdPush, 0));
        var first = await l.AcceptAsync(cts.Token);
        Assert.Equal(0xA1u, first.Conv);

        // The peer restarted: same address and port, fresh conversation, sn back at 0.
        Send(s, l, Segment(0xB2, CmdPush, 0));
        var second = await l.AcceptAsync(cts.Token);

        Assert.Equal(0xB2u, second.Conv);
        Assert.True(first.IsClosed);
        Assert.Equal(KcpCloseReason.Replaced, first.CloseReason);
        Assert.False(second.IsClosed);
        Assert.Equal(1, l.Stats.SessionsReplaced);
        Assert.Equal(1, l.SessionCount);
    }

    // ── Flood caps ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PerIpCap_RefusesSessionsBeyondTheCap()
    {
        using var l = Listen(KcpListenerOptions.Default with { MaxSessionsPerIp = 3, ExemptLoopbackFromPerIpCap = false });
        var sockets = Enumerable.Range(0, 5).Select(_ => RawSocket()).ToList();
        try
        {
            for (int i = 0; i < sockets.Count; i++) Send(sockets[i], l, Segment((uint)(0x100 + i), CmdPush, 0));
            await Eventually(() => l.Stats.SessionsRejectedPerIpCap == 2, "two per-IP refusals");

            Assert.Equal(3, l.SessionCount);
            Assert.Equal(3, l.Stats.SessionsCreated);

            // The cap is on LIVE sessions: closing one admits the next.
            var victim = await l.AcceptAsync(CancellationToken.None);
            victim.Close();
            Send(sockets[4], l, Segment(0x104, CmdPush, 0));
            await Eventually(() => l.Stats.SessionsCreated == 4, "a session after one closed");
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
    }

    [Fact]
    public async Task LoopbackPeers_AreExemptFromThePerIpCap_ByDefault()
    {
        using var l = Listen(KcpListenerOptions.Default with { MaxSessionsPerIp = 2 });
        var sockets = Enumerable.Range(0, 4).Select(_ => RawSocket()).ToList();
        try
        {
            for (int i = 0; i < sockets.Count; i++) Send(sockets[i], l, Segment((uint)(0x200 + i), CmdPush, 0));
            await Eventually(() => l.SessionCount == 4, "four loopback sessions");
            Assert.Equal(0, l.Stats.SessionsRejectedPerIpCap);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
    }

    [Fact]
    public async Task GlobalCap_RefusesSessionsBeyondTheCap()
    {
        using var l = Listen(KcpListenerOptions.Default with { MaxSessions = 4 });
        var sockets = Enumerable.Range(0, 7).Select(_ => RawSocket()).ToList();
        try
        {
            for (int i = 0; i < sockets.Count; i++) Send(sockets[i], l, Segment((uint)(0x300 + i), CmdPush, 0));
            await Eventually(() => l.Stats.SessionsRejectedGlobalCap == 3, "three global-cap refusals");
            Assert.Equal(4, l.SessionCount);
            Assert.Equal(4, l.Stats.SessionsLive);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
    }

    [Fact]
    public async Task NewSessionRate_IsLimitedByATokenBucket()
    {
        using var l = Listen(KcpListenerOptions.Default with { NewSessionsPerSecond = 0.5, NewSessionBurst = 3 });
        var sockets = Enumerable.Range(0, 8).Select(_ => RawSocket()).ToList();
        try
        {
            for (int i = 0; i < sockets.Count; i++) Send(sockets[i], l, Segment((uint)(0x400 + i), CmdPush, 0));
            await Eventually(() => l.Stats.SessionsCreated + l.Stats.SessionsRejectedRate == 8, "all eight decided");

            // The burst admits three; at 0.5/s nothing refills inside the test.
            Assert.Equal(3, l.Stats.SessionsCreated);
            Assert.Equal(5, l.Stats.SessionsRejectedRate);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
    }

    [Fact]
    public async Task InboundDatagramRate_DropsTheExcess_WithoutClosingTheSession()
    {
        using var l = Listen(KcpListenerOptions.Default with { DatagramsPerSecondPerSession = 1, DatagramBurstPerSession = 10 });
        using var s = RawSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Send(s, l, Segment(0x500, CmdPush, 0));        // opener: token 1 of 10
        var session = await l.AcceptAsync(cts.Token);
        for (int i = 0; i < 40; i++) Send(s, l, Segment(0x500, CmdAck, 0));
        await Eventually(() => l.Stats.DatagramsDroppedRate >= 25, "rate drops counted");

        Assert.InRange(l.Stats.DatagramsDroppedRate, 25, 31);
        Assert.False(session.IsClosed);
    }

    // ── Session lifetime ─────────────────────────────────────────────────────

    [Fact]
    public async Task SilentSession_IsClosedByTheIdleTimeout_AndLeavesTheTable()
    {
        using var l = Listen(KcpListenerOptions.Default with { IdleTimeoutMs = 300 });
        using var s = RawSocket();
        Send(s, l, Segment(0x600, CmdPush, 0));
        var session = await l.AcceptAsync(CancellationToken.None);

        await Eventually(() => session.IsClosed, "idle close", timeoutMs: 3000);
        Assert.Equal(KcpCloseReason.Idle, session.CloseReason);
        Assert.Equal(1, l.Stats.SessionsClosedIdle);
        Assert.Equal(0, l.SessionCount);
    }

    [Fact]
    public async Task PeerThatStopsAcknowledging_IsClosedAsADeadLink()
    {
        // Idle timeout out of the way so only the retransmission limit can fire.
        using var pair = await KcpPair.CreateAsync(KcpListenerOptions.Default with { IdleTimeoutMs = 120_000 });
        var session = pair.Client.ServerSession!;

        // One acknowledged round trip first, so the server has an RTT sample: without one
        // the RTO starts at KCP's 200ms default and 20 transmissions take ~23s instead of
        // the ~3.5s a live link that then goes silent takes.
        await pair.ServerSide.Stream.WriteAsync(new byte[16]);
        var buf = new byte[16];
        Assert.Equal(16, await pair.ClientStream.ReadAsync(buf));

        pair.Client.Abort(); // silent from now on: no ACKs, no datagrams
        session.Write(new byte[64]);
        await Eventually(() => session.IsClosed, "dead link", timeoutMs: 15_000);

        Assert.Equal(KcpCloseReason.DeadLink, session.CloseReason);
        Assert.Equal(1, pair.Listener.Stats.SessionsClosedDeadLink);
        Assert.Equal(0, pair.Listener.SessionCount);
    }

    // ── Write path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task OversizedWrite_ClosesTheSessionVisibly_InsteadOfVanishing()
    {
        using var l = Listen();
        using var s = RawSocket();
        Send(s, l, Segment(0x800, CmdPush, 0));
        var session = await l.AcceptAsync(CancellationToken.None);

        var ex = Assert.Throws<IOException>(() => session.Write(new byte[KcpTuning.MaxWriteBytes + 1]));
        Assert.Contains("exceeds the maximum frame", ex.Message);

        Assert.True(session.IsClosed);
        Assert.Equal(KcpCloseReason.WriteRejected, session.CloseReason);
        Assert.Equal(1, l.Stats.WritesRejected);
        Assert.Equal(0, l.SessionCount);
        Assert.Throws<IOException>(() => session.Write(new byte[1]));
    }

    [Fact]
    public async Task MaximumFrame_IsSplitAcrossSends_AndArrivesIntact()
    {
        // 1 MiB + 4 bytes is ~790 segments: three times the 255-fragment limit of a single
        // Kcp.Send. The old Write handed it to Send whole and ignored the -2.
        using var pair = await KcpPair.CreateAsync();
        var payload = new byte[KcpTuning.MaxWriteBytes];
        new Random(7).NextBytes(payload);

        var write = pair.ServerSide.Stream.WriteAsync(payload).AsTask();
        var received = new byte[payload.Length];
        int got = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stream = pair.ClientStream;
        while (got < received.Length)
        {
            int n = await stream.ReadAsync(received.AsMemory(got), cts.Token);
            Assert.True(n > 0, "EOF before the frame arrived");
            got += n;
        }
        await write;
        Assert.Equal(payload, received);
        Assert.Equal(0, pair.Listener.Stats.WritesRejected);
    }

    [Fact]
    public async Task WriterWaitsAtTheSoftLimit_UntilThePeerAcknowledges()
    {
        var limits = KcpListenerOptions.Default with { SendQueueSoftLimit = 8 };
        using var pair = await KcpPair.CreateAsync(limits);
        pair.Client.ReceiveBufferBytes = 4 * 1024; // the client stops draining almost at once

        // 400 KiB against a reader that is not reading: the client's window closes, the
        // server's queue reaches its soft limit, and the writer must wait — not queue
        // without bound and not drop.
        var payload = new byte[400 * 1024];
        new Random(11).NextBytes(payload);
        var write = Task.Run(async () =>
        {
            for (int off = 0; off < payload.Length; off += 16 * 1024)
                await pair.ServerSide.Stream.WriteAsync(payload.AsMemory(off, 16 * 1024));
        });

        await Task.Delay(800);
        Assert.False(write.IsCompleted, "the writer finished against a peer that is not reading");
        var session = pair.Client.ServerSession!;
        Assert.InRange(session.WaitSnd, 1, limits.SendQueueSoftLimit + (16 * 1024 / 1300) + 2);

        // Now read everything: the writer is released and every byte arrives in order.
        var received = new byte[payload.Length];
        int got = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (got < received.Length)
        {
            int n = await pair.ClientStream.ReadAsync(received.AsMemory(got), cts.Token);
            Assert.True(n > 0);
            got += n;
        }
        await write.WaitAsync(cts.Token);
        Assert.Equal(payload, received);
        Assert.Equal(0, pair.Listener.Stats.SessionsClosedSlowConsumer);
    }

    [Fact]
    public async Task WriterThatIgnoresTheSoftLimit_IsClosedAsASlowConsumer()
    {
        using var l = Listen(KcpListenerOptions.Default with { SendQueueSoftLimit = 4, SendQueueHardLimit = 32 });
        using var s = RawSocket();
        Send(s, l, Segment(0x900, CmdPush, 0));
        var session = await l.AcceptAsync(CancellationToken.None);

        // Synchronous Write never waits; the peer (a raw socket) never ACKs.
        IOException? failure = null;
        for (int i = 0; i < 100 && failure == null; i++)
        {
            try { session.Write(new byte[1300]); }
            catch (IOException ex) { failure = ex; }
        }

        Assert.NotNull(failure);
        Assert.Contains("slow consumer", failure!.Message);
        Assert.Equal(KcpCloseReason.SlowConsumer, session.CloseReason);
        Assert.Equal(1, l.Stats.SessionsClosedSlowConsumer);
        Assert.Equal(0, l.SessionCount);
    }

    // ── Receive backpressure ─────────────────────────────────────────────────

    [Fact]
    public async Task SlowReader_ClosesTheWindow_AndLosesNothing()
    {
        var limits = KcpListenerOptions.Default with { MaxPendingReceiveBytes = 8 * 1024 };
        using var pair = await KcpPair.CreateAsync(limits);
        var session = pair.Client.ServerSession!;

        var payload = new byte[300 * 1024];
        new Random(3).NextBytes(payload);
        await pair.ClientStream.WriteAsync(payload);

        // The server is not reading. Its buffered input must stay near the cap — the rest
        // waits in the peer's queue behind a closed window — instead of growing to 300 KiB.
        await Task.Delay(1000);
        Assert.InRange(session.PendingReceiveBytes, 1, limits.MaxPendingReceiveBytes + 2 * KcpTuning.Mtu);
        Assert.True(pair.Client.WaitSnd > 0, "the client's data all left although the server is not reading");

        // Read it all: every byte, in order. Backpressure, never a drop.
        var received = new byte[payload.Length];
        int got = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (got < received.Length)
        {
            int n = await pair.ServerSide.Stream.ReadAsync(received.AsMemory(got), cts.Token);
            Assert.True(n > 0, "EOF before the payload arrived");
            got += n;
        }
        Assert.Equal(payload, received);
    }

    // ── Restart ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListenerRestart_OnTheSamePort_AcceptsAFreshClient()
    {
        int port;
        using (var first = Listen())
        {
            port = first.LocalEndPoint.Port;
            using var c1 = new KcpTestClient();
            await c1.ConnectAsync(IPAddress.Loopback, port);
            Assert.Equal(1, first.SessionCount);
        } // disposed: socket closed, every session closed

        using var second = new KcpListener(new IPEndPoint(IPAddress.Loopback, port), "", NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accept = second.AcceptAsync(cts.Token);
        using var c2 = new KcpTestClient();
        await c2.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var session = await accept;
        await c2.GetStream().WriteAsync("after-restart"u8.ToArray(), cts.Token);
        Assert.Equal("after-restart", System.Text.Encoding.UTF8.GetString((await session.ReadChunkAsync(cts.Token))!));
    }
}
