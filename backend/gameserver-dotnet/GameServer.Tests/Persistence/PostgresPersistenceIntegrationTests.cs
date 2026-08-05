using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Persistence;

/// <summary>
/// Full-loop evidence that player state survives a server restart: boot a real
/// <see cref="GameServerHost"/> backed by a real PostgreSQL, drive a TCP client
/// through join → move → disconnect, then assert the row landed in
/// <c>player_states</c> and a freshly-constructed store reloads that position.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresPersistenceIntegrationTests
{
    private const string JwtSecret = "integration-test-secret";

    private readonly PostgresFixture _pg;

    public PostgresPersistenceIntegrationTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task PlayerPosition_SurvivesServerRestart()
    {
        if (_pg.SkipIfUnavailable(nameof(PlayerPosition_SurvivesServerRestart))) return;

        string userId = $"itest-{Guid.NewGuid():N}"[..20];
        string serverId = "gs-itest";
        int port = FreeTcpPort();

        // ── Boot #1: postgres-backed server ──
        var store = await _pg.ConnectStoreAsync();
        await store.MigrateAsync();

        var options = new ServerOptions
        {
            ServerAddr = $":{port}",
            ServerId = serverId,
            MapId = "map_itest",
            Mode = "map",
            TickRate = 20,
            Capacity = 4,
            JwtSecret = JwtSecret,
            SaveInterval = TimeSpan.FromMilliseconds(300),
            PlayerStore = store,
            LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        using var cts = new CancellationTokenSource();
        var runTask = server.RunAsync($":{port}", cts.Token);

        try
        {
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await using var stream = client.GetStream();

            // Join
            await SendAsync(stream, MsgType.JoinToken,
                new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, serverId, JwtSecret) });

            var joinEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(joinEnv);
            var joinResp = WireProtocol.GetPayload<JoinTokenResponse>(joinEnv!);
            Assert.True(joinResp.Ok, joinResp.Error);
            Assert.Equal(userId, joinResp.UserId);

            // Move east for a while so the persisted X is unambiguously non-zero.
            for (ulong tick = 1; tick <= 40; tick++)
            {
                await SendAsync(stream, MsgType.Input,
                    new InputMessage { Tick = tick, MoveX = 1f, MoveY = 0f });
                await Task.Delay(25, cts.Token);
            }

            // Let the async saver flush at least once while still connected.
            await Task.Delay(700, cts.Token);
        }
        finally
        {
            cts.Cancel();
            await server.ShutdownAsync(); // triggers the final save
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }

        // ── Assert the row really exists in postgres ──
        var persisted = await store.LoadPlayerAsync(userId, default);
        Assert.NotNull(persisted);
        Assert.True(persisted!.X > 0, $"expected persisted X > 0, got {persisted.X}");
        Assert.Equal("map_itest", persisted.MapId);
        Assert.True(persisted.Hp > 0);

        await store.DisposeAsync();

        // ── Boot #2: a brand-new store (simulating a process restart) reloads it ──
        await using var restarted = await _pg.ConnectStoreAsync();
        await restarted.MigrateAsync();

        var reloaded = await restarted.LoadPlayerAsync(userId, default);
        Assert.NotNull(reloaded);
        Assert.Equal(persisted.X, reloaded!.X);
        Assert.Equal(persisted.Y, reloaded.Y);
        Assert.Equal(persisted.Hp, reloaded.Hp);
        Assert.Equal(persisted.MapId, reloaded.MapId);
    }

    // ── Helpers ──

    private static async Task SendAsync(Stream stream, MsgType type, JoinTokenRequest payload)
        => await WriteFrameAsync(stream, type,
            JsonSerializer.SerializeToUtf8Bytes(payload, WireJsonContext.Default.JoinTokenRequest));

    private static async Task SendAsync(Stream stream, MsgType type, InputMessage payload)
        => await WriteFrameAsync(stream, type,
            JsonSerializer.SerializeToUtf8Bytes(payload, WireJsonContext.Default.InputMessage));

    private static async Task WriteFrameAsync(Stream stream, MsgType type, byte[] payloadBytes)
    {
        using var doc = JsonDocument.Parse(payloadBytes);
        var env = new Envelope { Type = (byte)type, Payload = doc.RootElement.Clone() };
        byte[] frame = WireProtocol.Encode(env);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100); // listener not up yet
            }
        }
        throw new TimeoutException($"game server never started listening on :{port}");
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
