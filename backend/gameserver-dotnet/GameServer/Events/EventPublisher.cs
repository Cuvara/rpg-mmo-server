using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using GameServer.Observability;

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
public sealed class EventPublisher : IDisposable
{
    private readonly IEventStream _stream;
    private readonly ILogger _logger;
    private readonly GameMetrics? _metrics;

    // Deaths are queued here by the tick thread and serialized + published by the
    // drain task below. QueueDeath used to be PublishDeathAsync called directly from
    // OnEntityDeath — JSON serialization plus the synchronous prefix of the stream
    // publish (against Redis Streams, any multiplexer backlog or reconnect handling)
    // ran on the tick thread while the world write lock was held, so a wave of AoE
    // kills serialised N payloads while every network thread waited on the lock
    // (#249). Same shape as KillRewardBatcher: the tick thread's share is one
    // channel write. Bounded so a dead stream cannot grow the queue without limit;
    // dropping the oldest death event under that backlog is acceptable — the stream
    // is telemetry/feed, and the authoritative reward path is the kill batcher.
    private readonly Channel<(string Type, DeathPayload Payload)> _deaths =
        Channel.CreateBounded<(string, DeathPayload)>(
            new BoundedChannelOptions(4096)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

    private readonly Task _drain;

    public EventPublisher(IEventStream stream, ILogger logger, GameMetrics? metrics = null)
    {
        _stream = stream;
        _logger = logger;
        _metrics = metrics;
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Queue a death event for publication. Tick-thread safe: one bounded-channel
    /// write, no serialization, no I/O.
    /// </summary>
    public void QueueDeath(string eventType, DeathPayload payload)
    {
        _deaths.Writer.TryWrite((eventType, payload));
    }

    /// <summary>Publish a death event to the event stream (fire-and-forget).</summary>
    public Task PublishDeathAsync(string eventType, DeathPayload payload)
    {
        try
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(payload, EventJsonContext.Default.DeathPayload);
            var evt = new GameEvent(eventType, data);
            var task = _stream.PublishAsync("game_events", evt, CancellationToken.None);
            _metrics?.RecordEventPublished(eventType);
            return task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish death event");
            return Task.CompletedTask;
        }
    }

    private async Task DrainAsync()
    {
        await foreach (var (type, payload) in _deaths.Reader.ReadAllAsync())
        {
            await PublishDeathAsync(type, payload);
        }
    }

    /// <summary>
    /// Completes the queue and waits briefly for the drain to publish what is left.
    /// </summary>
    public void Dispose()
    {
        _deaths.Writer.TryComplete();
        try
        {
            _drain.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // PublishDeathAsync already logs its own failures.
        }
    }
}
