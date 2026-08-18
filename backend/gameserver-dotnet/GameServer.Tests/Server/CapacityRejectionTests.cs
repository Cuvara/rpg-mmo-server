using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Server;
using GameServer.Tests.Infrastructure;
using RpgMmo.Wire.V1;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// #145: a client refused because the server was at capacity produced <b>zero</b> server log
/// lines. `SendError` told the client and nothing told the operator, so a server correctly
/// turning players away and a server that was broken looked identical from the outside —
/// which is how a 120-player load run stopped dead at 100 joins with no server-side
/// explanation of why.
///
/// <para>The admission limit is <c>GAMESERVER_CAPACITY</c>. It is not a resource limit and
/// hitting it is not a fault; it is the signal that a chosen number has been reached, which
/// is only actionable if it is observable.</para>
/// </summary>
public class CapacityRejectionTests
{
    private const string JwtSecret = "capacity-test-secret";
    private const string ServerId = "gs-capacity";

    [Fact]
    public async Task JoinBeyondCapacity_IsRefusedAndLogged()
    {
        var captured = new CapturingLoggerProvider();
        using var metrics = new GameMetrics("map_capacity", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics, captured, capacity: 1);

        // Fill the one slot.
        using var first = new TcpClient();
        await first.ConnectAsync("127.0.0.1", h.Port);
        var firstResp = await JoinAsync(first, "user-in");
        Assert.True(firstResp.Ok, firstResp.Error);

        // The next join must be refused...
        using var second = new TcpClient();
        await second.ConnectAsync("127.0.0.1", h.Port);
        var secondResp = await JoinAsync(second, "user-out");
        Assert.False(secondResp.Ok);
        Assert.Equal("Server is full", secondResp.Error);

        // ...and the refusal must appear in the log. This is the whole point of the issue:
        // the rejection behaviour was already correct, its invisibility was not.
        var line = await captured.WaitForAsync(
            e => e.Level == LogLevel.Warning && e.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10));

        Assert.NotNull(line);
        // The operator needs to be able to tell an admission limit from a resource limit
        // without reading the source, and needs the user it happened to.
        Assert.Contains("user-out", line!.Message);
        Assert.Contains("GAMESERVER_CAPACITY", line.Message);
        Assert.Contains("1/1", line.Message);
    }

    /// <summary>
    /// The negative half: a join that fits must not log a capacity warning. Without this a
    /// log line emitted unconditionally on every join would satisfy the test above.
    /// </summary>
    [Fact]
    public async Task JoinWithinCapacity_LogsNoCapacityWarning()
    {
        var captured = new CapturingLoggerProvider();
        using var metrics = new GameMetrics("map_capacity", $"test.{Guid.NewGuid():N}");
        await using var h = await Harness.StartAsync(metrics, captured, capacity: 4);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", h.Port);
        var resp = await JoinAsync(client, "user-fits");
        Assert.True(resp.Ok, resp.Error);

        Assert.DoesNotContain(
            captured.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("at capacity", StringComparison.OrdinalIgnoreCase));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<JoinTokenResponse> JoinAsync(TcpClient client, string userId)
    {
        var stream = client.GetStream();
        // Built here rather than reused from EntityLifecycleTests: that helper bakes in its
        // own server id, and a token for a different server is rejected two steps before the
        // capacity check this file is about.
        var join = WireProtocol.NewEnvelope(
            MsgType.JoinToken,
            new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, ServerId, JwtSecret) },
            WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(join));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env);
        return WireProtocol.GetPayload<JoinTokenResponse>(env!);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    /// <summary>
    /// Minimal in-memory <see cref="ILoggerProvider"/>. The suite otherwise runs on
    /// <c>NullLoggerFactory</c>, which is exactly why a missing log line was invisible to
    /// every existing test.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ILoggerFactory
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Sink(_entries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        /// <summary>
        /// Poll for a matching entry. Logging happens on the connection-accept task, not on
        /// the caller's, so the assertion cannot assume the line has landed by the time the
        /// client's error response has been read.
        /// </summary>
        public async Task<LogEntry?> WaitForAsync(Func<LogEntry, bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var hit = _entries.FirstOrDefault(predicate);
                if (hit != null) return hit;
                await Task.Delay(25);
            }
            return null;
        }

        private sealed class Sink(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required GameServerHost Server { get; init; }
        public required int Port { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task RunTask { get; init; }

        public static async Task<Harness> StartAsync(
            GameMetrics metrics, ILoggerFactory loggerFactory, int capacity)
        {
            var options = new ServerOptions
            {
                ServerAddr = ":0",
                ServerId = ServerId,
                MapId = "map_capacity",
                Mode = "map",
                Transport = TransportKind.Tcp,
                TickRate = 20,
                Capacity = capacity,
                JwtSecret = JwtSecret,
                JoinTokenSecret = JwtSecret,
                HoldTtl = TimeSpan.FromMilliseconds(200),
                SaveInterval = TimeSpan.FromHours(1),
                Metrics = metrics,
                LoggerFactory = loggerFactory
            };

            var server = new GameServerHost(options);
            var cts = new CancellationTokenSource();
            var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);
            return new Harness { Server = server, Port = port, Cts = cts, RunTask = runTask };
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
