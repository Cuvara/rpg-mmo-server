using GameServer.Net;
using GameServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using RpgMmo.Wire.V1;
using Envelope = GameServer.Net.Envelope;

namespace GameServer.Tests.Net;

/// <summary>
/// The control lane (<see cref="Connection.SendControl"/>): replies a client is owed exactly
/// once - CommandResult, ServerPush, TransferMapResp, Kick, Disconnect - must survive a data
/// lane that is full and dropping its oldest items, and must go out ahead of it.
/// </summary>
public class ControlLaneTests
{
    /// <summary>A Connection over a real loopback KCP session, and the client on the other end.</summary>
    private static async Task<(Connection conn, KcpTestClient client, KcpPair pair, KcpPair listener)> PairAsync()
    {
        var pair = await KcpPair.CreateAsync();
        var conn = new Connection("lane-user", pair.ServerSide, NullLogger.Instance, WireEncoding.Proto)
        {
            PeerProtocolVersion = 3,
        };
        return (conn, pair.Client, pair, pair);
    }

    private static Envelope Ping(long ts) =>
        WireProtocol.NewEnvelope(MsgType.Ping, new PingMessage { Timestamp = ts }, WireEncoding.Proto);

    [Fact]
    public async Task ControlMessages_SurviveAFullDataLane_AndGoOutFirst()
    {
        var (conn, client, _, listener) = await PairAsync();
        try
        {
            // Overfill the 64-slot drop-oldest data lane before the write task runs: 200 items,
            // so 136 of them are discarded - exactly the pressure that used to eat a reply.
            for (int i = 0; i < 200; i++) conn.Send(Ping(i));

            Assert.True(conn.SendControl(WireProtocol.NewEnvelope(MsgType.Kick,
                new KickMessage { Reason = "duplicate_login" }, WireEncoding.Proto)));
            Assert.True(conn.SendControl(WireProtocol.NewEnvelope(MsgType.CommandResult,
                new CommandResult { Seq = 7, Ok = true }, WireEncoding.Proto)));

            Task writer = conn.WriteLoopAsync();
            var stream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            Envelope first = (await WireProtocol.DecodeAsync(stream, cts.Token))!;
            Envelope second = (await WireProtocol.DecodeAsync(stream, cts.Token))!;
            Assert.Equal((uint)MsgType.Kick, first.Type);
            Assert.Equal("duplicate_login", WireProtocol.GetPayload<KickMessage>(first).Reason);
            Assert.Equal((uint)MsgType.CommandResult, second.Type);
            Assert.Equal(7u, WireProtocol.GetPayload<CommandResult>(second).Seq);

            // The data lane still delivers what it kept, after the control messages.
            Envelope third = (await WireProtocol.DecodeAsync(stream, cts.Token))!;
            Assert.Equal((uint)MsgType.Ping, third.Type);
            Assert.Equal(0, conn.ControlOverflows);

            conn.Close();
            await writer;
        }
        finally
        {
            conn.Dispose();
            client.Dispose();
            listener.Dispose();
        }
    }

    [Fact]
    public async Task ControlMessage_SentWhileTheWriterIsRunning_OvertakesQueuedData()
    {
        var (conn, client, _, listener) = await PairAsync();
        try
        {
            Task writer = conn.WriteLoopAsync();
            for (int i = 0; i < 64; i++) conn.Send(Ping(i));
            Assert.True(conn.SendControl(WireProtocol.NewEnvelope(MsgType.TransferMapResp,
                new TransferMapResponse { Ok = false, Error = "transfer already in progress" }, WireEncoding.Proto)));

            var stream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool seen = false;
            for (int i = 0; i < 70 && !seen; i++)
            {
                Envelope env = (await WireProtocol.DecodeAsync(stream, cts.Token))!;
                seen = env.Type == (uint)MsgType.TransferMapResp;
            }
            Assert.True(seen, "the transfer reply never arrived");

            conn.Close();
            await writer;
        }
        finally
        {
            conn.Dispose();
            client.Dispose();
            listener.Dispose();
        }
    }

    /// <summary>
    /// A peer that lets the control lane fill is not reading; the connection is closed rather
    /// than a control message being dropped silently.
    /// </summary>
    [Fact]
    public async Task AFullControlLane_ClosesTheConnection_InsteadOfDropping()
    {
        var (conn, client, _, listener) = await PairAsync();
        try
        {
            Envelope result = WireProtocol.NewEnvelope(MsgType.CommandResult,
                new CommandResult { Seq = 1, Ok = false, Error = "rate_limited" }, WireEncoding.Proto);
            for (int i = 0; i < Connection.ControlLaneCapacity; i++) Assert.True(conn.SendControl(result));

            Assert.False(conn.SendControl(result));
            Assert.Equal(1, conn.ControlOverflows);
            Assert.True(conn.Closing.IsCancellationRequested);
            Assert.False(conn.SendControl(result)); // closed: refused, not counted again
            Assert.Equal(1, conn.ControlOverflows);
        }
        finally
        {
            conn.Dispose();
            client.Dispose();
            listener.Dispose();
        }
    }
}
