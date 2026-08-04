using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GameServer.Events;

/// <summary>Payload for entity death events.</summary>
public record DeathPayload(
    [property: JsonPropertyName("victim_id")] string VictimId,
    [property: JsonPropertyName("victim_type")] string VictimType,
    [property: JsonPropertyName("killer_id")] string? KillerId,
    [property: JsonPropertyName("map_id")] string MapId,
    [property: JsonPropertyName("server_id")] string ServerId);

/// <summary>Generic game event for streaming.</summary>
public record GameEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] byte[] Payload);

/// <summary>Abstraction for event streaming (Redis Streams in production).</summary>
public interface IEventStream
{
    Task PublishAsync(string stream, GameEvent evt, CancellationToken ct);
}

/// <summary>No-op event stream for local development.</summary>
public sealed class NoopEventStream : IEventStream
{
    public Task PublishAsync(string stream, GameEvent evt, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>AOT-compatible JSON context for event types.</summary>
[JsonSerializable(typeof(DeathPayload))]
[JsonSerializable(typeof(GameEvent))]
public partial class EventJsonContext : JsonSerializerContext;

/// <summary>
/// Publishes game events to the event stream.
/// Port of Go events/publisher.go.
/// </summary>
public sealed class EventPublisher
{
    private readonly IEventStream _stream;
    private readonly ILogger _logger;

    public EventPublisher(IEventStream stream, ILogger logger)
    {
        _stream = stream;
        _logger = logger;
    }

    /// <summary>Publish a death event to the event stream (fire-and-forget).</summary>
    public Task PublishDeathAsync(string eventType, DeathPayload payload)
    {
        try
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload, EventJsonContext.Default.DeathPayload);
            var evt = new GameEvent(eventType, data);
            return _stream.PublishAsync("game_events", evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish death event");
            return Task.CompletedTask;
        }
    }
}
