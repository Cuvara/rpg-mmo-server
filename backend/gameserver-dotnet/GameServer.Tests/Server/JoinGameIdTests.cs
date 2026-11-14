using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServer.Games;
using GameServer.Net.Transport;
using GameServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Server;

/// <summary>ADR-33: join tokens can only enter the game served by this server.</summary>
public class JoinGameIdTests
{
    private const string JoinSecret = "join-game-id-test-secret";
    private const string DefaultServerId = "gs-join-game-id";
    private const string OtherServerId = "foo-map01";

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "cuvara")]
    [InlineData("foo", "foo")]
    public async Task MatchingGame_Joins(string? serverGame, string? tokenGame)
    {
        await using var h = await Harness.StartAsync(serverGame);
        using var client = new KcpTestClient();
        await client.ConnectAsync("127.0.0.1", h.Port);

        var response = await JoinAsync(client, CreateToken("user-matching", h.ServerId, tokenGame));

        Assert.True(response.Ok, response.Error);
    }

    [Theory]
    [InlineData(null, "foo")]
    [InlineData("foo", null)]
    public async Task DifferentGame_IsRefusedAndLogged_WithoutConsumingJtiOrCapacity(
        string? serverGame, string? tokenGame)
    {
        using var logs = new CapturingLoggerFactory();
        await using var h = await Harness.StartAsync(serverGame, logs);
        string jti = Guid.NewGuid().ToString("N");

        using var refusedClient = new KcpTestClient();
        await refusedClient.ConnectAsync("127.0.0.1", h.Port);
        var refused = await JoinAsync(refusedClient, CreateToken("user-refused", h.ServerId, tokenGame, jti));

        Assert.False(refused.Ok);
        Assert.Equal(GameServerHost.ReasonDifferentGame, refused.Error);
        Assert.Contains(logs.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("user-refused", StringComparison.Ordinal) &&
            entry.Message.Contains($"token is for game {GameId.GameOrDefault(tokenGame)}", StringComparison.Ordinal) &&
            entry.Message.Contains($"this server serves {GameId.GameOrDefault(serverGame)}", StringComparison.Ordinal));

        // Capacity is one. A different user with the same jti must still join after
        // the mismatch, proving the refusal neither reserved a slot nor consumed jti.
        using var matchingClient = new KcpTestClient();
        await matchingClient.ConnectAsync("127.0.0.1", h.Port);
        var matching = await JoinAsync(matchingClient, CreateToken("user-admitted", h.ServerId, serverGame, jti));

        Assert.True(matching.Ok, matching.Error);
    }

    [Fact]
    public async Task DifferentServer_IsRefusedBeforeDifferentGame()
    {
        await using var h = await Harness.StartAsync();
        using var client = new KcpTestClient();
        await client.ConnectAsync("127.0.0.1", h.Port);

        var response = await JoinAsync(client, CreateToken("user-wrong-server", OtherServerId, "foo"));

        Assert.False(response.Ok);
        Assert.Equal("Token is for a different server", response.Error);
    }

    private static string CreateToken(string userId, string serverId, string? gameId, string? jti = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["sid"] = serverId,
            ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        };
        if (gameId != null) payload["gid"] = gameId;

        string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        string body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        string input = $"{header}.{body}";
        byte[] signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(JoinSecret), Encoding.ASCII.GetBytes(input));
        return $"{input}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<JoinTokenResponse> JoinAsync(KcpTestClient client, string token)
    {
        var stream = client.GetStream();
        var request = WireProtocol.NewEnvelope(
            MsgType.JoinToken, new JoinTokenRequest { Token = token }, WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(request));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await WireProtocol.DecodeAsync(stream, timeout.Token);
        Assert.NotNull(response);
        Assert.Equal((byte)MsgType.JoinTokenResp, response.Type);
        return WireProtocol.GetPayload<JoinTokenResponse>(response);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Sink(Entries);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required GameServerHost Server { get; init; }
        public required string ServerId { get; init; }
        public required int Port { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task RunTask { get; init; }

        public static async Task<Harness> StartAsync(string? gameId = null, ILoggerFactory? loggerFactory = null)
        {
            var options = new ServerOptions
            {
                ServerAddr = ":0",
                ServerId = gameId == "foo" ? OtherServerId : DefaultServerId,
                MapId = "map_join_game_id",
                Mode = "map",
                Transport = TransportKind.Kcp,
                TickRate = 20,
                Capacity = 1,
                JoinTokenSecret = JoinSecret,
                HoldTtl = TimeSpan.FromMilliseconds(200),
                SaveInterval = TimeSpan.FromHours(1),
                LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance
            };
            // Leaving the property untouched on the default path verifies its default.
            if (gameId != null) options.GameId = gameId;

            var server = new GameServerHost(options);
            var cts = new CancellationTokenSource();
            var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);
            return new Harness { Server = server, ServerId = options.ServerId, Port = port, Cts = cts, RunTask = runTask };
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
