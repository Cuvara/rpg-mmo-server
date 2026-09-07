using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Server;
using GameServer.Tests.Infrastructure;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// A live <see cref="GameServerHost"/> on an ephemeral port for the admission-hardening
/// tests (workspace audit F03/F04). Same shape as the harnesses in
/// <c>EntityLifecycleTests</c> and <c>CapacityRejectionTests</c>, with the pre-join and
/// ingestion bounds exposed so a test can set them low enough to hit.
/// </summary>
internal sealed class HardeningHarness : IAsyncDisposable
{
    public const string JwtSecret = "hardening-test-secret";
    public const string ServerId = "gs-hardening";
    public const string MapId = "map_hardening";

    public required GameServerHost Server { get; init; }
    public required GameMetrics Metrics { get; init; }
    public required int Port { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public required Task RunTask { get; init; }

    public static async Task<HardeningHarness> StartAsync(
        GameMetrics metrics,
        int capacity = 100,
        int maxPendingHandshakes = ServerOptions.DefaultMaxPendingHandshakes,
        TimeSpan? handshakeTimeout = null,
        TimeSpan? hold = null,
        IPlayerStore? playerStore = null,
        int maxInputsPerConnection = 0,
        int maxPendingInputs = 0)
    {
        var options = new ServerOptions
        {
            ServerAddr = ":0",
            ServerId = ServerId,
            MapId = MapId,
            Mode = "map",
            Transport = TransportKind.Tcp,
            TickRate = 20,
            Capacity = capacity,
            MaxPendingHandshakes = maxPendingHandshakes,
            HandshakeTimeout = handshakeTimeout ?? ServerOptions.DefaultHandshakeTimeout,
            MaxInputsPerConnection = maxInputsPerConnection,
            MaxPendingInputs = maxPendingInputs,
            JwtSecret = JwtSecret,
            JoinTokenSecret = JwtSecret,
            HoldTtl = hold ?? TimeSpan.FromMilliseconds(200),
            SaveInterval = TimeSpan.FromHours(1),
            PlayerStore = playerStore,
            Metrics = metrics,
            LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        var cts = new CancellationTokenSource();
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);
        return new HardeningHarness
        {
            Server = server, Metrics = metrics, Port = port, Cts = cts, RunTask = runTask
        };
    }

    /// <summary>Open a raw TCP connection to the server and send nothing.</summary>
    public async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, Port);
                return client;
            }
            catch (SocketException) when (attempt < 60)
            {
                await Task.Delay(50);
            }
        }
    }

    /// <summary>Send a join for <paramref name="userId"/> on an already-open connection and return the reply.</summary>
    public static async Task<JoinTokenResponse> SendJoinAsync(
        TcpClient client, string userId, TimeSpan? timeout = null)
    {
        var stream = client.GetStream();
        var join = WireProtocol.NewEnvelope(
            MsgType.JoinToken,
            new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
            WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(join));
        await stream.FlushAsync();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        var env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env);
        Assert.Equal((uint)MsgType.JoinTokenResp, env!.Type);
        return WireProtocol.GetPayload<JoinTokenResponse>(env);
    }

    /// <summary>Connect, join, and require success.</summary>
    public async Task<TcpClient> JoinAsync(string userId)
    {
        var client = await ConnectAsync();
        var resp = await SendJoinAsync(client, userId);
        Assert.True(resp.Ok, resp.Error);
        return client;
    }

    /// <summary>Send one <c>MsgInput</c> on a joined connection.</summary>
    public static async Task SendInputAsync(
        NetworkStream stream, ulong tick, float moveX, float moveY, string? attackTargetId = null)
    {
        var msg = new InputMessage { Tick = tick, MoveX = moveX, MoveY = moveY };
        if (attackTargetId != null) msg.AttackTargetId = attackTargetId;
        var env = WireProtocol.NewEnvelope(MsgType.Input, msg, WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(env));
    }

    /// <summary>
    /// True when the peer has closed: the next read returns EOF within
    /// <paramref name="within"/>. False when bytes arrive or the wait runs out.
    /// </summary>
    public static async Task<bool> ObservesEofAsync(TcpClient client, TimeSpan within)
    {
        var buf = new byte[256];
        using var cts = new CancellationTokenSource(within);
        try
        {
            while (true)
            {
                int n = await client.GetStream().ReadAsync(buf, cts.Token);
                if (n == 0) return true;
                // A reply frame (a JoinTokenResp error, say) before the close is fine;
                // keep reading until EOF or the budget runs out.
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return true; }   // reset by peer counts as closed
        catch (SocketException) { return true; }
    }

    /// <summary>Read frames until one of <paramref name="type"/> arrives.</summary>
    public static async Task<GameServer.Net.Envelope> ReadUntilAsync(NetworkStream stream, MsgType type, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var env = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(env);
            if ((MsgType)env!.Type == type) return env;
        }
    }

    /// <summary>Poll until <paramref name="predicate"/> holds, or fail loudly.</summary>
    public async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null, string? what = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        Assert.Fail(
            $"{what ?? "condition"} not met within {limit.TotalSeconds:F0}s " +
            $"(online={Metrics.PlayersOnline}, entities={Server.EntityCount}, " +
            $"pendingHandshakes={Server.PendingHandshakes}, reservations={Server.PendingReservations})");
    }

    public async ValueTask DisposeAsync()
    {
        Cts.Cancel();
        await Server.ShutdownAsync();
        try { await RunTask; } catch (OperationCanceledException) { /* expected */ }
        Cts.Dispose();
    }
}
