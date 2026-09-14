using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GameServer.Net;
using GameServer.Net.Sealed;
using GameServer.Server;
using GameServer.Persistence;
using GameServer.Tests.Infrastructure;
using RpgMmo.Wire.V1;
using Xunit;

namespace GameServer.Tests.Net;

/// <summary>
/// A listener that requires sealing must say WHY it refuses, on both refusal paths.
/// </summary>
/// <remarks>
/// <para>
/// Measured against a live <c>require</c> listener before these tests existed: the client
/// was answered <c>Ok=true</c>, counted as online, reported IN WORLD, and was then closed
/// on its fifth input with a bare <c>broken pipe</c>. A Unity client's reconnect policy
/// rejoined and was closed again — 21 cycles — and nothing in any log on either side named
/// encryption. A refusal nobody can attribute is worse than a refusal that never happens,
/// because it presents as a flaky network.
/// </para>
/// <para>
/// The two paths are refused at different moments, and that asymmetry is deliberate rather
/// than an oversight:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Encoding</b> is decided in the join reply itself. No handshake can change the answer
/// — a JSON frame has no room for the sealed layout — so there is nothing to wait for.
/// </description></item>
/// <item><description>
/// <b>A missing handshake</b> cannot be decided that early: the client will not run the key
/// exchange until it knows the join was accepted. By the time the server knows, the client
/// already believes it is in the world, so the only honest signal left is a kick carrying
/// the reason.
/// </description></item>
/// </list>
/// </remarks>
public class SealedRefusalNamesItselfTests
{
    private const string ServerId = "gs-sealed-refusal";
    private const string JwtSecret = "test-secret-for-sealed-refusal-tests";

    private static ServerOptions RequireSealed() => new()
    {
        ServerAddr = ":0",
        ServerId = ServerId,
        MapId = "map_sealed",
        Mode = "map",
        TickRate = 20,
        Capacity = 8,
        JwtSecret = JwtSecret,
        JoinTokenSecret = JwtSecret,
        SaveInterval = TimeSpan.FromSeconds(30),
        HoldTtl = TimeSpan.FromSeconds(30),
        PlayerStore = new MemoryPlayerStore(),
        SealedTransport = SealedRequirement.Required,
    };

    [Fact]
    public async Task JsonClient_IsRefusedInTheJoinReply_NamingTheEncoding()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = new GameServerHost(RequireSealed());
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, cts.Token);
        await using var stream = client.GetStream();

        var join = WireProtocol.NewEnvelope(
            MsgType.JoinToken,
            new JoinTokenRequest { Token = TestHelpers.CreateTestJwt("u-json", ServerId, JwtSecret) },
            WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(join), cts.Token);
        await stream.FlushAsync(cts.Token);

        var respEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(respEnv);
        Assert.True(respEnv!.Type == (byte)MsgType.JoinTokenResp, $"expected a JoinTokenResp, got {respEnv.Type}");

        var resp = WireProtocol.GetPayload<JoinTokenResponse>(respEnv);

        // The refusal is IN the join reply. Before this, the reply said Ok=true and the
        // connection died later with nothing attached to it.
        Assert.False(resp.Ok);
        Assert.Equal(SealedRefusalReason.EncodingCannotSeal, resp.Error);

        // The refusal also arrives BEFORE the player is counted -- the counter increment
        // now sits after this branch. That ordering is asserted by the live smoke run
        // recorded in the deploy changelog rather than here, because players_online is not
        // reachable from this harness.
        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }
    }

    [Fact]
    public async Task ProtobufClient_ThatNeverSeals_IsKickedWithAReason()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = new GameServerHost(RequireSealed());
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, cts.Token);
        await using var stream = client.GetStream();

        var join = WireProtocol.NewEnvelope(
            MsgType.JoinToken,
            new JoinTokenRequest { Token = TestHelpers.CreateTestJwt("u-proto", ServerId, JwtSecret) },
            WireEncoding.Proto);
        await stream.WriteAsync(WireProtocol.Encode(join), cts.Token);
        await stream.FlushAsync(cts.Token);

        // The join itself succeeds — it has to, or the client would never run the key
        // exchange. This is the moment the old behaviour became unattributable.
        var respEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(respEnv);
        var resp = WireProtocol.GetPayload<JoinTokenResponse>(respEnv!);
        Assert.True(resp.Ok, resp.Error);

        // Now say nothing, as a client with sealing switched off does. The server must
        // tell us why it is going away rather than just going away.
        var kickEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(kickEnv);
        Assert.True(kickEnv!.Type == (byte)MsgType.Kick, $"expected a Kick, got {kickEnv.Type}");

        var kick = WireProtocol.GetPayload<KickMessage>(kickEnv);
        Assert.Equal(SealedRefusalReason.NoSealedSession, kick.Reason);

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }
    }
}
