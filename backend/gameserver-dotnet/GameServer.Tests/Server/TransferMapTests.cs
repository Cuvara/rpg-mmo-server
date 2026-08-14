using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Server;
using RpgMmo.Wire.V1;

namespace GameServer.Tests.Server;

/// <summary>
/// Map transfer: a connected player requests transfer to a different map.
/// The server saves state, replies OK, removes the entity (no hold), and closes
/// the connection. The client then goes through the existing MsgEnterWorld flow
/// with the gateway.
/// </summary>
public class TransferMapTests
{
    private const string JwtSecret = "transfer-test-secret";
    private const string ServerId = "gs-transfer";
    private const string MapId = "map_01";

    /// <summary>A valid transfer to a different map succeeds and removes the entity.</summary>
    [Fact]
    public async Task TransferMap_DifferentMap_SucceedsAndRemovesEntity()
    {
        using var metrics = new GameMetrics("map_transfer", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        // Join as a player
        using var client = await h.JoinAsync("user-transfer");
        Assert.Equal(1, h.Server.EntityCount);
        Assert.Equal(1, metrics.PlayersOnline);

        // Send transfer request to a different map
        var stream = client.GetStream();
        var transferReq = WireProtocol.NewEnvelope(
            MsgType.TransferMap,
            new TransferMapRequest { MapId = "map_02" },
            WireEncoding.Json);
        byte[] frame = WireProtocol.Encode(transferReq);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();

        // Read the response, skipping any snapshot that beat it to the wire.
        var env = await ReadUntilAsync(stream, MsgType.TransferMapResp);
        var resp = WireProtocol.GetPayload<TransferMapResponse>(env);
        Assert.True(resp.Ok, resp.Error);

        // Entity should be removed (no hold) and player count decremented.
        //
        // Wait on BOTH, not just the entity. The server removes the entity and only then
        // decrements the online gauge (HandleTransferMap: RemoveEntity, then PlayerLeft),
        // so "EntityCount == 0" is reached strictly before "PlayersOnline == 0". Waiting on
        // the first and immediately asserting the second is a race the test loses whenever
        // the runner preempts the server thread in that window — the CI failure this
        // replaces read "Expected: 0 / Actual: 1" on the gauge, not on the entity count.
        await h.WaitForAsync(() => h.Server.EntityCount == 0 && metrics.PlayersOnline == 0);
        Assert.Equal(0, h.Server.EntityCount);
        Assert.Equal(0, metrics.PlayersOnline);
    }

    /// <summary>Transfer to the same map is rejected.</summary>
    [Fact]
    public async Task TransferMap_SameMap_ReturnsError()
    {
        using var metrics = new GameMetrics("map_transfer", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        using var client = await h.JoinAsync("user-same");
        var stream = client.GetStream();

        // Try to transfer to the same map
        var transferReq = WireProtocol.NewEnvelope(
            MsgType.TransferMap,
            new TransferMapRequest { MapId = MapId },
            WireEncoding.Json);
        byte[] frame = WireProtocol.Encode(transferReq);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();

        var env = await ReadUntilAsync(stream, MsgType.TransferMapResp);
        var resp = WireProtocol.GetPayload<TransferMapResponse>(env);
        Assert.False(resp.Ok);
        Assert.Contains("already on this map", resp.Error);

        // Entity should still be present (transfer was rejected)
        Assert.Equal(1, h.Server.EntityCount);
    }

    /// <summary>Transfer with empty map_id is rejected.</summary>
    [Fact]
    public async Task TransferMap_EmptyMapId_ReturnsError()
    {
        using var metrics = new GameMetrics("map_transfer", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        using var client = await h.JoinAsync("user-empty");
        var stream = client.GetStream();

        var transferReq = WireProtocol.NewEnvelope(
            MsgType.TransferMap,
            new TransferMapRequest { MapId = "" },
            WireEncoding.Json);
        byte[] frame = WireProtocol.Encode(transferReq);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();

        var env = await ReadUntilAsync(stream, MsgType.TransferMapResp);
        var resp = WireProtocol.GetPayload<TransferMapResponse>(env);
        Assert.False(resp.Ok);
        Assert.Contains("map_id is required", resp.Error);
    }

    // ─────────────────────── test harness ───────────────────────

    /// <summary>
    /// Reads frames until one of <paramref name="want"/> arrives, discarding the traffic the
    /// server pushes on its own schedule.
    /// <para>
    /// A reply is <b>not</b> guaranteed to be the next frame on the wire: the tick loop
    /// broadcasts snapshots at <c>TickRate</c> (20 Hz here), so a snapshot emitted between
    /// the request and the reply arrives first. Reading exactly one frame and asserting its
    /// type made the test lose that race whenever the machine was loaded — observed as
    /// "Assert.Equal() Failure: Expected: 14 / Actual: 8", i.e. a snapshot where the
    /// transfer response was expected. Anything other than a known unsolicited type is
    /// still a hard failure, so this skips noise without hiding a wrong reply.
    /// </para>
    /// </summary>
    private static async Task<GameServer.Net.Envelope> ReadUntilAsync(Stream stream, MsgType want, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            GameServer.Net.Envelope? env;
            try
            {
                env = await WireProtocol.DecodeAsync(stream, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"no {want} within {(timeout ?? TimeSpan.FromSeconds(15)).TotalSeconds:F0}s");
                throw; // unreachable, keeps the compiler happy
            }

            Assert.NotNull(env);
            if (env!.Type == (byte)want) return env;

            Assert.True(
                env.Type is (byte)MsgType.Snapshot or (byte)MsgType.Pong or (byte)MsgType.Resync,
                $"unexpected message type {env.Type} while waiting for {want}");
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required GameServerHost Server { get; init; }
        public required GameMetrics Metrics { get; init; }
        public required int Port { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task RunTask { get; init; }

        public static async Task<Harness> StartAsync(GameMetrics metrics)
        {
            // Bind an ephemeral port and ask the server which one it got, rather than
            // picking one ourselves. The old FreeTcpPort() helper bound port 0, read the
            // number, closed its listener and handed the number to the server — between the
            // close and the server's bind the port is free for anyone, and a sibling test
            // taking it made the server's TcpListener.Start() throw
            // "SocketException : Address already in use". There is no gap to lose here: the
            // socket the server reports is the socket the server is listening on.
            var options = new ServerOptions
            {
                ServerAddr = ":0",
                ServerId = ServerId,
                MapId = MapId,
                Mode = "map",
                Transport = TransportKind.Tcp,
                TickRate = 20,
                Capacity = 100,
                JwtSecret = JwtSecret,
                JoinTokenSecret = JwtSecret,
                HoldTtl = TimeSpan.FromMilliseconds(300),
                SaveInterval = TimeSpan.FromHours(1),
                Metrics = metrics,
                LoggerFactory = NullLoggerFactory.Instance
            };

            var server = new GameServerHost(options);
            var cts = new CancellationTokenSource();
            var runTask = server.RunAsync(":0", cts.Token);

            // Completes the moment the listener is bound, so no probe-and-retry loop is
            // needed to discover when the server is up.
            string boundAddr = await server.ListeningAddressAsync.WaitAsync(TimeSpan.FromSeconds(30));
            int port = TransportFactory.ParseAddr(boundAddr).Port;

            return new Harness { Server = server, Metrics = metrics, Port = port, Cts = cts, RunTask = runTask };
        }

        public async Task<TcpClient> JoinAsync(string userId)
        {
            var client = new TcpClient();
            // No retry loop: StartAsync only returns once the listener is bound, so a
            // connection now either lands in the accept backlog or fails for a real reason.
            await client.ConnectAsync(IPAddress.Loopback, Port);
            var stream = client.GetStream();

            var env = WireProtocol.NewEnvelope(
                MsgType.JoinToken,
                new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
                WireEncoding.Json);
            byte[] frame = WireProtocol.Encode(env);
            await stream.WriteAsync(frame);
            await stream.FlushAsync();

            var resp = await ReadUntilAsync(stream, MsgType.JoinTokenResp);
            var joinResp = WireProtocol.GetPayload<JoinTokenResponse>(resp);
            Assert.True(joinResp.Ok, joinResp.Error);
            return client;
        }

        public async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
        {
            var limit = timeout ?? TimeSpan.FromSeconds(15);
            var deadline = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate()) return;
                await Task.Delay(25);
            }
            // Report the gauge too: the condition being waited on spans both, and
            // "entities=0" alone reads like a passing state.
            Assert.Fail($"condition not met within {limit.TotalSeconds:F0}s " +
                        $"(entities={Server.EntityCount}, playersOnline={Metrics.PlayersOnline})");
        }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Server.ShutdownAsync();
            try { await RunTask; } catch (OperationCanceledException) { /* expected */ }
            Cts.Dispose();
        }
    }

}
