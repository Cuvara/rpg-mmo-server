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

        // Read the response
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env);
        Assert.Equal((byte)MsgType.TransferMapResp, env!.Type);
        var resp = WireProtocol.GetPayload<TransferMapResponse>(env);
        Assert.True(resp.Ok, resp.Error);

        // Entity should be removed (no hold) and player count decremented
        await h.WaitForAsync(() => h.Server.EntityCount == 0);
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env);
        Assert.Equal((byte)MsgType.TransferMapResp, env!.Type);
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env);
        var resp = WireProtocol.GetPayload<TransferMapResponse>(env);
        Assert.False(resp.Ok);
        Assert.Contains("map_id is required", resp.Error);
    }

    // ─────────────────────── test harness ───────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        public required GameServerHost Server { get; init; }
        public required int Port { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task RunTask { get; init; }

        public static async Task<Harness> StartAsync(GameMetrics metrics)
        {
            int port = FreeTcpPort();
            var options = new ServerOptions
            {
                ServerAddr = $":{port}",
                ServerId = ServerId,
                MapId = MapId,
                Mode = "map",
                Transport = TransportKind.Tcp,
                TickRate = 20,
                Capacity = 100,
                JwtSecret = JwtSecret,
                HoldTtl = TimeSpan.FromMilliseconds(300),
                SaveInterval = TimeSpan.FromHours(1),
                Metrics = metrics,
                LoggerFactory = NullLoggerFactory.Instance
            };

            var server = new GameServerHost(options);
            var cts = new CancellationTokenSource();
            var runTask = server.RunAsync($":{port}", cts.Token);

            using (var probe = new TcpClient())
            {
                await ConnectWithRetryAsync(probe, port);
            }

            return new Harness { Server = server, Port = port, Cts = cts, RunTask = runTask };
        }

        public async Task<TcpClient> JoinAsync(string userId)
        {
            var client = new TcpClient();
            await ConnectWithRetryAsync(client, Port);
            var stream = client.GetStream();

            var env = WireProtocol.NewEnvelope(
                MsgType.JoinToken,
                new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
                WireEncoding.Json);
            byte[] frame = WireProtocol.Encode(env);
            await stream.WriteAsync(frame);
            await stream.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var resp = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(resp);
            var joinResp = WireProtocol.GetPayload<JoinTokenResponse>(resp!);
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
            Assert.Fail($"condition not met within {limit.TotalSeconds:F0}s (entities={Server.EntityCount})");
        }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Server.ShutdownAsync();
            try { await RunTask; } catch (OperationCanceledException) { /* expected */ }
            Cts.Dispose();
        }
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException($"server never started listening on :{port}");
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
