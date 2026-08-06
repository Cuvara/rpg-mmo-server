using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Persistence;
using GameServer.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Server;

/// <summary>
/// The join hop is verified with JOIN_TOKEN_SECRET, not JWT_SECRET (2026-08-06).
/// JOIN_TOKEN_SECRET is distributed to every game-server pod; JWT_SECRET is not, so
/// sharing them meant one compromised pod could mint Nakama auth tokens for any user.
///
/// These tests drive the real handshake over TCP, because the failure this change
/// guards against is operational: gateway and game server disagreeing about which
/// secret signs the join token takes every join down.
/// </summary>
public class JoinTokenSecretTests
{
    private const string AuthSecret = "auth-secret-32-bytes-aaaaaaaaaaaaa";
    private const string JoinSecret = "join-secret-32-bytes-bbbbbbbbbbbbb";
    private const string OldJoinSecret = "old-join-secret-32-bytes-ccccccccc";
    private const string ServerId = "gs-join-secret-test";

    /// <summary>Split active: a token signed with JOIN_TOKEN_SECRET joins.</summary>
    [Fact]
    public async Task Join_TokenSignedWithJoinSecret_Accepted()
        => await AssertJoinAsync(jwtSecret: AuthSecret, joinTokenSecret: JoinSecret,
            tokenSecret: JoinSecret, expectOk: true);

    /// <summary>
    /// The point of the split: with JOIN_TOKEN_SECRET set, a token signed with
    /// JWT_SECRET must NOT open a session — otherwise a leaked auth secret still
    /// buys a join and the separation buys nothing.
    /// </summary>
    [Fact]
    public async Task Join_TokenSignedWithAuthSecret_RejectedWhenSplitActive()
        => await AssertJoinAsync(jwtSecret: AuthSecret, joinTokenSecret: JoinSecret,
            tokenSecret: AuthSecret, expectOk: false);

    /// <summary>
    /// Pre-split deployments (JOIN_TOKEN_SECRET unset on both halves) keep working:
    /// the gateway still signs with JWT_SECRET and this server still accepts it.
    /// </summary>
    [Fact]
    public async Task Join_JoinSecretUnset_FallsBackToJwtSecret()
        => await AssertJoinAsync(jwtSecret: AuthSecret, joinTokenSecret: "",
            tokenSecret: AuthSecret, expectOk: true);

    /// <summary>Rotation window: the previous join secret still verifies.</summary>
    [Theory]
    [InlineData(JoinSecret)]     // signed with the new current key
    [InlineData(OldJoinSecret)]  // signed before the rotation — must still drain
    public async Task Join_DuringRotation_BothJoinSecretsAccepted(string tokenSecret)
        => await AssertJoinAsync(jwtSecret: AuthSecret,
            joinTokenSecret: $"{JoinSecret},{OldJoinSecret}",
            tokenSecret: tokenSecret, expectOk: true);

    /// <summary>A key dropped from the keyring stops working.</summary>
    [Fact]
    public async Task Join_AfterRotationCompletes_OldJoinSecretRejected()
        => await AssertJoinAsync(jwtSecret: AuthSecret, joinTokenSecret: JoinSecret,
            tokenSecret: OldJoinSecret, expectOk: false);

    /// <summary>No secret anywhere must fail closed, never open.</summary>
    [Fact]
    public async Task Join_NoSecretsConfigured_RejectsEveryToken()
        => await AssertJoinAsync(jwtSecret: "", joinTokenSecret: "   ",
            tokenSecret: JoinSecret, expectOk: false);

    /// <summary>Expiry beats the keyring: a matching key does not resurrect a dead token.</summary>
    [Fact]
    public async Task Join_ExpiredToken_RejectedEvenWhenKeyMatches()
        => await AssertJoinAsync(jwtSecret: AuthSecret,
            joinTokenSecret: $"{JoinSecret},{OldJoinSecret}",
            tokenSecret: OldJoinSecret, expectOk: false, expired: true);

    // ── Helpers ──

    private static async Task AssertJoinAsync(
        string jwtSecret, string joinTokenSecret, string tokenSecret, bool expectOk, bool expired = false)
    {
        int port = FreeTcpPort();
        var options = new ServerOptions
        {
            ServerAddr = $":{port}",
            ServerId = ServerId,
            MapId = "map_join_secret",
            TickRate = 20,
            Capacity = 8,
            JwtSecret = jwtSecret,
            JoinTokenSecret = joinTokenSecret,
            SaveInterval = TimeSpan.FromSeconds(30),
            HoldTtl = TimeSpan.FromSeconds(1),
            PlayerStore = new MemoryPlayerStore(),
            LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var runTask = server.RunAsync($":{port}", cts.Token);

        try
        {
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await using var stream = client.GetStream();

            string token = expired
                ? TestHelpers.CreateExpiredJwt("user-join", ServerId, tokenSecret)
                : TestHelpers.CreateTestJwt("user-join", ServerId, tokenSecret);

            byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
                new JoinTokenRequest { Token = token }, WireJsonContext.Default.JoinTokenRequest);
            using var doc = JsonDocument.Parse(payloadBytes);
            var env = new Envelope { Type = (byte)MsgType.JoinToken, Payload = doc.RootElement.Clone() };
            await stream.WriteAsync(WireProtocol.Encode(env), cts.Token);
            await stream.FlushAsync(cts.Token);

            var respEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(respEnv);
            var resp = WireProtocol.GetPayload<JoinTokenResponse>(respEnv!);

            if (expectOk)
            {
                Assert.True(resp.Ok, $"join should have been accepted, got: {resp.Error}");
            }
            else
            {
                Assert.False(resp.Ok, "join should have been rejected but the server accepted the token");
            }
        }
        finally
        {
            cts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
            await server.DisposeAsync();
        }
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
