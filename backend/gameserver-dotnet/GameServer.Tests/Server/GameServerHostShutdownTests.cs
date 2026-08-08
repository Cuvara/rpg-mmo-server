using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Server;

/// <summary>
/// Shutdown must be idempotent and concurrency-safe. RunAsync calls ShutdownAsync at its
/// tail while the process owner (SIGTERM handler, Agones drain, a test harness) normally
/// calls it too — so two teardowns overlap on essentially every termination. Before the
/// guard, both racers walked the entity-hold table and one cancelled a
/// CancellationTokenSource the other had already disposed, so a pod that should have
/// drained cleanly threw ObjectDisposedException out of RunAsync instead.
/// </summary>
public class GameServerHostShutdownTests
{
    private const string JwtSecret = "shutdown-test-secret-key-32-bytes!!";
    private const string ServerId = "gs-shutdown-test";

    /// <summary>Bare double shutdown on a server that never ran must be a no-op, not a throw.</summary>
    [Fact]
    public async Task ShutdownAsync_CalledTwiceSequentially_DoesNotThrow()
    {
        await using var server = new GameServerHost(NewOptions(0, new MemoryPlayerStore()));

        await server.ShutdownAsync();
        await server.ShutdownAsync();
    }

    /// <summary>
    /// The real race: many callers enter ShutdownAsync at once while entity holds are
    /// pending, and RunAsync's own tail call joins them. Every caller must return
    /// normally — none may observe a hold CTS another racer already disposed.
    /// </summary>
    [Fact]
    public async Task ShutdownAsync_ConcurrentCallers_WithPendingHolds_DoNotThrow()
    {
        int port = FreeTcpPort();
        var server = new GameServerHost(NewOptions(port, new MemoryPlayerStore()));
        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runTask = server.RunAsync($":{port}", runCts.Token);

        // Join three players and drop each connection. Each disconnect parks a live hold
        // CTS in the table — those are the objects the two shutdown racers fought over.
        for (int i = 0; i < 3; i++)
        {
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await using var stream = client.GetStream();
            await JoinAsync(stream, $"hold-user-{i}", runCts.Token);
        } // disposing the client drops the connection -> OnPlayerDisconnected -> hold added

        // Let the server observe all three disconnects before we tear down.
        await Task.Delay(300);

        // Eight concurrent ShutdownAsync callers plus RunAsync's own tail call once the
        // cancellation lands. Exactly one must do the work; none may fault.
        var racers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => server.ShutdownAsync()))
            .ToArray();
        runCts.Cancel();

        var racerException = await Record.ExceptionAsync(() => Task.WhenAll(racers));
        Assert.Null(racerException);

        var runException = await Record.ExceptionAsync(async () =>
        {
            try { await runTask; }
            catch (OperationCanceledException) { /* expected: the run token was cancelled */ }
        });
        Assert.Null(runException);

        // DisposeAsync shuts down a third time and disposes the CTS — still no throw.
        var disposeException = await Record.ExceptionAsync(async () => await server.DisposeAsync());
        Assert.Null(disposeException);
    }

    /// <summary>
    /// A later ShutdownAsync must not return before the first one finished its final
    /// save — "shutdown returned" has to mean the state is durable, otherwise a caller
    /// that shuts down and immediately reads the store races the save it just requested.
    /// </summary>
    [Fact]
    public async Task ShutdownAsync_SecondCaller_AwaitsTheFirstTeardown()
    {
        int port = FreeTcpPort();
        var store = new BlockingPlayerStore();
        var server = new GameServerHost(NewOptions(port, store));
        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runTask = server.RunAsync($":{port}", runCts.Token);

        try
        {
            // One connected player, so the final SaveAllAsync has something to write and
            // therefore actually reaches the blocking store.
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await using var stream = client.GetStream();
            await JoinAsync(stream, "blocking-user", runCts.Token);

            var first = Task.Run(() => server.ShutdownAsync());
            await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));

            var second = Task.Run(() => server.ShutdownAsync());
            // The first teardown is parked inside SaveAllAsync, so the second caller must
            // stay parked too rather than reporting a shutdown that has not happened.
            await Task.Delay(300);
            Assert.False(second.IsCompleted,
                "second ShutdownAsync returned before the first teardown finished its final save");

            store.Release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            store.Release.TrySetResult();
            runCts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
            await server.DisposeAsync();
        }
    }

    // ── Helpers ──

    private static ServerOptions NewOptions(int port, IPlayerStore store) => new()
    {
        ServerAddr = $":{port}",
        ServerId = ServerId,
        MapId = "map_shutdown",
        Mode = "map",
        TickRate = 20,
        Capacity = 8,
        JwtSecret = JwtSecret,
        JoinTokenSecret = JwtSecret,
        // Long enough that the periodic sweep never fires: the saves under test are the
        // shutdown ones, so a background sweep would muddy the timing assertion.
        SaveInterval = TimeSpan.FromSeconds(30),
        HoldTtl = TimeSpan.FromSeconds(30),
        PlayerStore = store,
        LoggerFactory = NullLoggerFactory.Instance
    };

    /// <summary>Player store whose save parks until released, pinning a teardown mid-flight.</summary>
    private sealed class BlockingPlayerStore : IPlayerStore
    {
        public readonly TaskCompletionSource SaveEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PlayerState?> LoadPlayerAsync(string userId, CancellationToken ct)
            => Task.FromResult<PlayerState?>(null);

        public async Task SavePlayerAsync(PlayerState state, CancellationToken ct)
        {
            SaveEntered.TrySetResult();
            await Release.Task;
        }
    }

    private static async Task JoinAsync(NetworkStream stream, string userId, CancellationToken ct)
    {
        var env = WireProtocol.NewEnvelope(MsgType.JoinToken, new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) }, WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(env), ct);
        await stream.FlushAsync(ct);

        var respEnv = await WireProtocol.DecodeAsync(stream, ct);
        Assert.NotNull(respEnv);
        var resp = WireProtocol.GetPayload<JoinTokenResponse>(respEnv!);
        Assert.True(resp.Ok, resp.Error);
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
