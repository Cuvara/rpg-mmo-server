using System.Buffers.Binary;
using System.Net.Sockets;
using GameServer.Net;
using GameServer.Observability;
using RpgMmo.Wire.V1;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// Workspace audit F03: pre-join connections used to have no deadline and no bound. A peer
/// that connected and sent nothing — or half a frame — held a socket and a task for ever,
/// outside <c>GAMESERVER_CAPACITY</c> (authenticated players only) and outside the
/// heartbeat (which starts after the join). Every test here goes through the socket, so
/// what is asserted is what a peer actually observes: its connection being closed.
/// </summary>
public class HandshakeHardeningTests
{
    private static GameMetrics NewMetrics() => new(HardeningHarness.MapId, $"test.{Guid.NewGuid():N}");

    /// <summary>An idle socket is closed at the deadline and counted as a timeout.</summary>
    [Fact]
    public async Task IdleSocket_IsClosedAtTheDeadline()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, handshakeTimeout: TimeSpan.FromMilliseconds(300));

        using var idle = await h.ConnectAsync();
        await h.WaitForAsync(() => h.Server.PendingHandshakes == 1, what: "pending gauge = 1");

        // Expected: EOF within ~300ms of connecting; 5s is the failure budget, not the
        // expectation.
        Assert.True(await HardeningHarness.ObservesEofAsync(idle, TimeSpan.FromSeconds(5)),
            "idle handshake was not closed at the deadline");

        await h.WaitForAsync(() => h.Server.PendingHandshakes == 0, what: "pending gauge back to 0");
        Assert.Equal(1, metrics.HandshakesRejectedTimeout);
        Assert.Equal(1, metrics.HandshakesRejected);
        Assert.Equal(0, metrics.PlayersOnline);
    }

    /// <summary>
    /// Beyond <c>GAMESERVER_MAX_PENDING_HANDSHAKES</c> an accepted socket is closed on the
    /// spot and counted as <c>pool_full</c>; the ones inside the pool still time out.
    /// </summary>
    [Fact]
    public async Task PendingPool_CapIsEnforcedAndCounted()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, maxPendingHandshakes: 2, handshakeTimeout: TimeSpan.FromSeconds(1));

        var sockets = new List<TcpClient>();
        try
        {
            for (int i = 0; i < 5; i++) sockets.Add(await h.ConnectAsync());

            // Expected: 2 in the pool, 3 refused at accept.
            await h.WaitForAsync(() => metrics.HandshakesRejectedPoolFull == 3, what: "3 pool_full rejects");
            Assert.Equal(2, h.Server.PendingHandshakes);

            // The refused ones are closed immediately; the pooled ones at the deadline.
            // Either way every socket sees EOF.
            foreach (var s in sockets)
            {
                Assert.True(await HardeningHarness.ObservesEofAsync(s, TimeSpan.FromSeconds(5)),
                    "a pre-join socket was never closed");
            }

            await h.WaitForAsync(() => h.Server.PendingHandshakes == 0, what: "pool drained");
            Assert.Equal(2, metrics.HandshakesRejectedTimeout);
            Assert.Equal(3, metrics.HandshakesRejectedPoolFull);
            Assert.Equal(5, metrics.HandshakesRejected);

            // The pool is a bound on concurrency, not a ban: once it drains, a real join
            // goes through.
            using var joined = await h.JoinAsync("user-after-flood");
            Assert.Equal(1, metrics.PlayersOnline);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
    }

    /// <summary>Two bytes of a four-byte length prefix, then silence: closed at the deadline.</summary>
    [Fact]
    public async Task PartialLengthPrefix_IsClosedAtTheDeadline()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, handshakeTimeout: TimeSpan.FromMilliseconds(300));

        using var client = await h.ConnectAsync();
        await client.GetStream().WriteAsync(new byte[] { 0x00, 0x00 });

        Assert.True(await HardeningHarness.ObservesEofAsync(client, TimeSpan.FromSeconds(5)));
        await h.WaitForAsync(() => h.Server.PendingHandshakes == 0);
        Assert.Equal(1, metrics.HandshakesRejectedTimeout);
    }

    /// <summary>A length prefix promising 100 bytes, 10 delivered, then silence: closed at the deadline.</summary>
    [Fact]
    public async Task PartialBody_IsClosedAtTheDeadline()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, handshakeTimeout: TimeSpan.FromMilliseconds(300));

        using var client = await h.ConnectAsync();
        var frame = new byte[4 + 10];
        BinaryPrimitives.WriteInt32BigEndian(frame, 100);
        await client.GetStream().WriteAsync(frame);

        Assert.True(await HardeningHarness.ObservesEofAsync(client, TimeSpan.FromSeconds(5)));
        await h.WaitForAsync(() => h.Server.PendingHandshakes == 0);
        Assert.Equal(1, metrics.HandshakesRejectedTimeout);
    }

    /// <summary>
    /// A complete frame whose body is not an envelope: closed immediately — well inside a
    /// deadline long enough that "closed at all" and "closed at the deadline" cannot be
    /// confused — and counted as malformed.
    /// </summary>
    [Fact]
    public async Task MalformedFrame_IsClosedImmediatelyAndCounted()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, handshakeTimeout: TimeSpan.FromSeconds(20));

        using var client = await h.ConnectAsync();
        var frame = new byte[4 + 8];
        BinaryPrimitives.WriteInt32BigEndian(frame, 8);
        frame.AsSpan(4).Fill(0xFF);
        await client.GetStream().WriteAsync(frame);

        // Expected: EOF within milliseconds. A 5s budget is a quarter of the deadline, so
        // passing here proves the close did not come from the timer.
        Assert.True(await HardeningHarness.ObservesEofAsync(client, TimeSpan.FromSeconds(5)),
            "malformed frame did not close the transport promptly");
        await h.WaitForAsync(() => h.Server.PendingHandshakes == 0);
        Assert.Equal(1, metrics.HandshakesRejectedMalformed);
        Assert.Equal(0, metrics.HandshakesRejectedTimeout);
    }

    /// <summary>A well-formed frame of the wrong type gets the error reply, then the close.</summary>
    [Fact]
    public async Task WrongFirstMessage_IsRefusedWithReplyAndClosed()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(
            metrics, handshakeTimeout: TimeSpan.FromSeconds(20));

        using var client = await h.ConnectAsync();
        var stream = client.GetStream();
        var ping = WireProtocol.NewEnvelope(MsgType.Ping, new PingMessage { Timestamp = 1 }, WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(ping));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env);
        var resp = WireProtocol.GetPayload<JoinTokenResponse>(env!);
        Assert.False(resp.Ok);
        Assert.Equal("Expected JoinToken message", resp.Error);

        Assert.True(await HardeningHarness.ObservesEofAsync(client, TimeSpan.FromSeconds(5)));
        await h.WaitForAsync(() => h.Server.PendingHandshakes == 0);
        Assert.Equal(1, metrics.HandshakesRejectedMalformed);
    }

    /// <summary>
    /// Host shutdown cancels a pending handshake read: the idle peer is closed promptly
    /// even with a deadline far in the future, and it is not counted as a rejection.
    /// </summary>
    [Fact]
    public async Task Shutdown_CancelsPendingHandshakes()
    {
        using var metrics = NewMetrics();
        var h = await HardeningHarness.StartAsync(metrics, handshakeTimeout: TimeSpan.FromSeconds(60));

        using var idle = await h.ConnectAsync();
        await h.WaitForAsync(() => h.Server.PendingHandshakes == 1);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.DisposeAsync();   // Cancel + ShutdownAsync + await RunAsync
        // Expected: the shutdown's own 2s client-drain grace plus a little; the 60s
        // deadline must play no part.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"shutdown took {sw.Elapsed}");

        Assert.True(await HardeningHarness.ObservesEofAsync(idle, TimeSpan.FromSeconds(5)),
            "pending handshake outlived shutdown");
        Assert.Equal(0, h.Server.PendingHandshakes);
        Assert.Equal(0, metrics.HandshakesRejected);
    }
}
