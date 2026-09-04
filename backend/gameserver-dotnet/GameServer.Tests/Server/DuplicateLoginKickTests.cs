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
/// Duplicate-login kick, the server half: <see cref="GameServerHost.KickPlayerAsync"/>
/// is what the <c>events:kick</c> consumer invokes when a <c>session_superseded</c>
/// event names this server. These tests call it directly (the Redis loop has its own
/// tests; the live end-to-end chain is
/// <c>backend/integration_test/duplicate_login_kick_e2e_test.go</c>) and pin the
/// contract: jti-matched, idempotent, full teardown with NO reconnect hold, and the
/// MsgKick-then-MsgDisconnect frame pair with <c>reason=duplicate_login</c>.
/// </summary>
public class DuplicateLoginKickTests
{
    private const string JwtSecret = "kick-test-secret";
    private const string ServerId = "gs-kick";
    private const string MapId = "map_01";

    [Fact]
    public async Task Kick_MatchingJti_TearsDownWithoutHold_AndNotifiesClient()
    {
        using var metrics = new GameMetrics("map_kick", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        string jti = Guid.NewGuid().ToString("N");
        using var client = await h.JoinAsync("user-kicked", jti);
        Assert.Equal(1, h.Server.EntityCount);
        Assert.Equal(1, metrics.PlayersOnline);

        Assert.True(await h.Server.KickPlayerAsync("user-kicked", jti));

        // The client is told why, MsgKick first, MsgDisconnect second, and the two
        // frames must never disagree about the reason — the gateway's contract.
        var stream = client.GetStream();
        var kickEnv = await ReadUntilAsync(stream, MsgType.Kick);
        var kick = WireProtocol.GetPayload<KickMessage>(kickEnv);
        Assert.Equal("duplicate_login", kick.Reason);
        // DisconnectMessage has no server-side GetPayload decoder (the server only
        // ever writes it), so read the JSON payload raw.
        var discEnv = await ReadUntilAsync(stream, MsgType.Disconnect);
        using var doc = System.Text.Json.JsonDocument.Parse(discEnv.Payload);
        Assert.Equal(kick.Reason, doc.RootElement.GetProperty("reason").GetString());

        // Entity released immediately, gauge balanced exactly once, and — the point —
        // no 30s reconnect hold parked for a login whose token is spent.
        await h.WaitForAsync(() => h.Server.EntityCount == 0 && metrics.PlayersOnline == 0);
        Assert.Equal(0, h.Server.PendingHolds);
        Assert.Equal(1, metrics.PlayersKicked);
    }

    [Fact]
    public async Task Kick_WrongOrEmptyJti_IsANoOp()
    {
        using var metrics = new GameMetrics("map_kick", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        string jti = Guid.NewGuid().ToString("N");
        using var client = await h.JoinAsync("user-safe", jti);

        // A supersede event naming a different login's jti must not touch this
        // connection — this is exactly the "event delivered after the newer login
        // joined" race, resolved in the newer login's favour.
        Assert.False(await h.Server.KickPlayerAsync("user-safe", "some-other-jti"));
        // An empty jti can never match anything (defence against a malformed event
        // that slipped past parsing).
        Assert.False(await h.Server.KickPlayerAsync("user-safe", ""));

        Assert.Equal(1, h.Server.EntityCount);
        Assert.Equal(1, metrics.PlayersOnline);
        Assert.Equal(0, metrics.PlayersKicked);
    }

    [Fact]
    public async Task Kick_RedeliveredEvent_IsIdempotent()
    {
        using var metrics = new GameMetrics("map_kick", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        string jti = Guid.NewGuid().ToString("N");
        using var client = await h.JoinAsync("user-redelivered", jti);

        Assert.True(await h.Server.KickPlayerAsync("user-redelivered", jti));
        await h.WaitForAsync(() => h.Server.EntityCount == 0 && metrics.PlayersOnline == 0);

        // At-least-once delivery WILL hand the consumer the same event again
        // (crash before ACK, PEL redelivery). The second delivery must change
        // nothing — no counter movement, no gauge movement.
        Assert.False(await h.Server.KickPlayerAsync("user-redelivered", jti));
        Assert.Equal(1, metrics.PlayersKicked);
        Assert.Equal(0, metrics.PlayersOnline);
        Assert.Equal(0, h.Server.PendingHolds);
    }

    [Fact]
    public async Task Kick_StaleEvent_NeverTouchesTheNewerLogin()
    {
        using var metrics = new GameMetrics("map_kick", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        // Old login joins and is kicked.
        string oldJti = Guid.NewGuid().ToString("N");
        using (var oldClient = await h.JoinAsync("user-relogin", oldJti))
        {
            Assert.True(await h.Server.KickPlayerAsync("user-relogin", oldJti));
            await h.WaitForAsync(() => h.Server.EntityCount == 0 && metrics.PlayersOnline == 0);
        }

        // The same user's NEW login joins with a freshly minted token.
        string newJti = Guid.NewGuid().ToString("N");
        using var newClient = await h.JoinAsync("user-relogin", newJti);
        Assert.Equal(1, metrics.PlayersOnline);

        // The old event redelivered once more — after the new login is in. The jti
        // no longer matches anything, so the new login survives untouched.
        Assert.False(await h.Server.KickPlayerAsync("user-relogin", oldJti));
        Assert.Equal(1, h.Server.EntityCount);
        Assert.Equal(1, metrics.PlayersOnline);
        Assert.Equal(1, metrics.PlayersKicked);
    }

    [Fact]
    public async Task Kick_UnknownUser_IsANoOp()
    {
        using var metrics = new GameMetrics("map_kick", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        Assert.False(await h.Server.KickPlayerAsync("user-never-joined", "any-jti"));
        Assert.Equal(0, metrics.PlayersKicked);
    }

    /// <summary>
    /// A user whose connection already dropped (entity in the reconnect hold) is left
    /// alone by a late supersede event: the event names a connection, and the
    /// connection is gone. The hold expires (or the user reattaches) on its own terms.
    /// </summary>
    [Fact]
    public async Task Kick_AfterDisconnect_LeavesTheHoldAlone()
    {
        using var metrics = new GameMetrics("map_kick", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics);

        string jti = Guid.NewGuid().ToString("N");
        var client = await h.JoinAsync("user-held", jti);
        client.Close(); // plain disconnect -> reconnect hold scheduled
        await h.WaitForAsync(() => metrics.PlayersOnline == 0 && h.Server.PendingHolds == 1);

        Assert.False(await h.Server.KickPlayerAsync("user-held", jti));
        Assert.Equal(1, h.Server.PendingHolds);
        Assert.Equal(1, h.Server.EntityCount); // still held
        Assert.Equal(0, metrics.PlayersKicked);
    }

    // ── plumbing (the TransferMapTests harness shape) ───────────────────────────

    private static async Task<GameServer.Net.Envelope> ReadUntilAsync(
        Stream stream, MsgType want, TimeSpan? timeout = null)
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
                throw;
            }
            Assert.NotNull(env);
            if (env!.Type == (byte)want) return env;
            // Snapshots and heartbeat frames legitimately interleave; anything else
            // while waiting for an eviction frame is a wrong reply, not noise.
            Assert.True(
                env.Type is (byte)MsgType.Snapshot or (byte)MsgType.Ping or (byte)MsgType.Pong,
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
                HoldTtl = TimeSpan.FromSeconds(30),
                SaveInterval = TimeSpan.FromHours(1),
                Metrics = metrics,
                LoggerFactory = NullLoggerFactory.Instance
            };

            var server = new GameServerHost(options);
            var cts = new CancellationTokenSource();
            var runTask = server.RunAsync(":0", cts.Token);
            string boundAddr = await server.ListeningAddressAsync.WaitAsync(TimeSpan.FromSeconds(30));
            int port = TransportFactory.ParseAddr(boundAddr).Port;
            return new Harness { Server = server, Metrics = metrics, Port = port, Cts = cts, RunTask = runTask };
        }

        public async Task<TcpClient> JoinAsync(string userId, string jti)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port);
            var stream = client.GetStream();

            var env = WireProtocol.NewEnvelope(
                MsgType.JoinToken,
                new JoinTokenRequest
                {
                    Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret, jti: jti)
                },
                WireEncoding.Json);
            await stream.WriteAsync(WireProtocol.Encode(env));
            await stream.FlushAsync();

            var resp = await ReadUntilAsync(stream, MsgType.JoinTokenResp);
            var joinResp = WireProtocol.GetPayload<JoinTokenResponse>(resp);
            Assert.True(joinResp.Ok, joinResp.Error);
            return client;
        }

        public async Task WaitForAsync(Func<bool> predicate, TimeSpan? timeout = null)
        {
            // Stopwatch, never DateTime.UtcNow (#153).
            var limit = timeout ?? TimeSpan.FromSeconds(15);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < limit)
            {
                if (predicate()) return;
                await Task.Delay(25);
            }
            Assert.Fail($"condition not met within {limit.TotalSeconds:F0}s " +
                        $"(entities={Server.EntityCount}, playersOnline={Metrics.PlayersOnline}, " +
                        $"holds={Server.PendingHolds}, kicked={Metrics.PlayersKicked})");
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
