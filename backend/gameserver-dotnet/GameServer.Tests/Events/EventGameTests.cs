using System.Text;
using System.Text.Json;
using GameServer.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace GameServer.Tests.Events;

public class EventGameTests
{
    [Theory]
    [InlineData(null, null, "cuvara")]
    [InlineData("foo", null, "foo")]
    [InlineData("foo", "", "foo")]
    [InlineData("foo", "explicit", "explicit")]
    public async Task DeathEvent_ContainsGameId(string? publisherGame, string? payloadGame, string expected)
    {
        var stream = new CapturingStream();
        using var publisher = new EventPublisher(stream, NullLogger.Instance, gameId: publisherGame);
        await publisher.PublishDeathAsync("entity_killed", new DeathPayload("victim", "npc", "killer", "map", "server", payloadGame));

        Assert.Equal(EventStreams.Game, stream.Stream);
        Assert.Equal("entity_killed", stream.Event!.Type);
        using var json = JsonDocument.Parse(stream.Event.Payload);
        Assert.Equal(expected, json.RootElement.GetProperty("game_id").GetString());
    }

    [Theory]
    [InlineData("foo", "foo", 0)]
    [InlineData("foo", "bar", 1)]
    [InlineData("cuvara", null, 0)]
    [InlineData("foo", null, 1)]
    public async Task KickEntry_ReportsMismatchButStillDispatches(string serverGame, string? eventGame, long mismatches)
    {
        var handled = new List<SessionSupersededPayload>();
        var logger = new CapturingLogger();
        var consumer = new RedisKickConsumer(null!, "srv1", p =>
        {
            handled.Add(p);
            return Task.CompletedTask;
        }, logger, gameId: serverGame);
        var gameField = eventGame is null ? "" : $",\"game_id\":\"{eventGame}\"";
        var payload = Encoding.UTF8.GetBytes($"{{\"user_id\":\"u1\",\"server_id\":\"srv1\",\"jti\":\"j1\"{gameField}}}");
        var entry = new StreamEntry("1-0", new NameValueEntry[]
        {
            new(RedisEventStream.FieldType, KickEvents.SessionSuperseded),
            new(RedisEventStream.FieldPayload, payload),
        });

        await consumer.HandleEntryAsync(entry);

        var result = Assert.Single(handled);
        Assert.Equal("u1", result.UserId);
        Assert.Equal("srv1", result.ServerId);
        Assert.Equal("j1", result.Jti);
        Assert.Equal(eventGame, result.GameId);
        Assert.Equal(eventGame ?? "cuvara", result.Game);
        Assert.Equal(mismatches, consumer.GameMismatches);
        Assert.Equal((int)mismatches, logger.Warnings.Count);
        if (mismatches != 0)
        {
            string warning = Assert.Single(logger.Warnings);
            Assert.Contains("u1", warning);
            Assert.Contains("srv1", warning);
            Assert.Contains("j1", warning);
            Assert.Contains(eventGame ?? "cuvara", warning);
            Assert.Contains(serverGame, warning);
        }
    }

    [Fact]
    public async Task KickEntry_ForeignServerOfAnotherGame_IsNotAMismatch()
    {
        var handled = new List<SessionSupersededPayload>();
        var logger = new CapturingLogger();
        var consumer = new RedisKickConsumer(null!, "srv1", p =>
        {
            handled.Add(p);
            return Task.CompletedTask;
        }, logger, gameId: "cuvara");
        var payload = Encoding.UTF8.GetBytes("{\"user_id\":\"u1\",\"server_id\":\"foo-srv2\",\"jti\":\"j1\",\"game_id\":\"foo\"}");
        var entry = new StreamEntry("1-0", new NameValueEntry[]
        {
            new(RedisEventStream.FieldType, KickEvents.SessionSuperseded),
            new(RedisEventStream.FieldPayload, payload),
        });

        await consumer.HandleEntryAsync(entry);

        Assert.Empty(handled);
        Assert.Equal(0, consumer.GameMismatches);
        Assert.Empty(logger.Warnings);
    }

    private sealed class CapturingStream : IEventStream
    {
        public string? Stream { get; private set; }
        public GameEvent? Event { get; private set; }
        public Task PublishAsync(string stream, GameEvent evt, CancellationToken ct)
        {
            Stream = stream;
            Event = evt;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
