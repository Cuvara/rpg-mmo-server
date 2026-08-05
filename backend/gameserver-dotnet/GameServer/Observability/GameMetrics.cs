using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GameServer.Observability;

/// <summary>
/// OpenTelemetry instruments for the game server, exposed through a single
/// <see cref="Meter"/> ("rpg.gameserver") and scraped by Prometheus.
///
/// Instrument names use OpenTelemetry dotted convention; the Prometheus
/// exporter maps them to the documented scrape names (see docs/METRICS.md):
///
/// <code>
/// gameserver.tick.duration            -> gameserver_tick_duration_seconds
/// gameserver.players.online           -> gameserver_players_online
/// gameserver.entities                 -> gameserver_entities
/// gameserver.snapshots.sent           -> gameserver_snapshots_sent_total
/// gameserver.player.saves             -> gameserver_player_saves_total
/// gameserver.events.published         -> gameserver_events_published_total
/// gameserver.tick.processed_inputs    -> gameserver_tick_processed_inputs_total
/// </code>
///
/// All record paths are allocation-free: tag sets are pre-built once in the
/// constructor and passed by value as <see cref="TagList"/> structs, so the
/// tick loop never allocates on the managed heap.
/// </summary>
public sealed class GameMetrics : IDisposable
{
    /// <summary>Default meter name. Must be registered on the MeterProvider.</summary>
    public const string DefaultMeterName = "rpg.gameserver";

    /// <summary>Instrument name of the tick duration histogram (used for SDK views).</summary>
    public const string TickDurationInstrument = "gameserver.tick.duration";

    private readonly Meter _meter;
    private readonly Histogram<double> _tickDuration;
    private readonly Counter<long> _snapshotsSent;
    private readonly Counter<long> _playerSaves;
    private readonly Counter<long> _eventsPublished;
    private readonly Counter<long> _processedInputs;

    // Pre-built tag sets — never allocate per record call.
    private readonly TagList _mapTags;
    private readonly TagList _saveOkTags;
    private readonly TagList _saveErrorTags;
    private readonly KeyValuePair<string, object?>[] _mapTagArray;

    private int _playersOnline;
    private Func<int>? _entityCountProvider;

    /// <summary>Meter name this instance publishes under.</summary>
    public string MeterName { get; }

    /// <summary>Current number of connected players (as reported to the gauge).</summary>
    public int PlayersOnline => Volatile.Read(ref _playersOnline);

    /// <summary>
    /// Create the instrument set for a given map.
    /// </summary>
    /// <param name="mapId">Map identifier used as the <c>map_id</c> label.</param>
    /// <param name="meterName">
    /// Meter name override. Defaults to <see cref="DefaultMeterName"/>; tests pass
    /// a unique name to isolate collection.
    /// </param>
    public GameMetrics(string mapId, string? meterName = null)
    {
        MeterName = meterName ?? DefaultMeterName;
        _meter = new Meter(MeterName);

        _mapTagArray = [new KeyValuePair<string, object?>("map_id", mapId)];
        _mapTags = new TagList { { "map_id", mapId } };
        _saveOkTags = new TagList { { "status", "ok" } };
        _saveErrorTags = new TagList { { "status", "error" } };

        _tickDuration = _meter.CreateHistogram<double>(
            TickDurationInstrument,
            unit: "s",
            description: "Wall-clock duration of a single simulation tick.");

        _snapshotsSent = _meter.CreateCounter<long>(
            "gameserver.snapshots.sent",
            description: "Snapshots enqueued to player connections.");

        _playerSaves = _meter.CreateCounter<long>(
            "gameserver.player.saves",
            description: "Player state save attempts, labelled by outcome.");

        _eventsPublished = _meter.CreateCounter<long>(
            "gameserver.events.published",
            description: "Game events published to the event stream, labelled by type.");

        _processedInputs = _meter.CreateCounter<long>(
            "gameserver.tick.processed_inputs",
            description: "Client inputs drained and applied by the tick loop.");

        _meter.CreateObservableGauge(
            "gameserver.players.online",
            ObservePlayersOnline,
            description: "Players currently connected to this server.");

        _meter.CreateObservableGauge(
            "gameserver.entities",
            ObserveEntities,
            description: "Entities currently present in the world.");
    }

    /// <summary>
    /// Register the callback used by the <c>gameserver_entities</c> gauge.
    /// The callback runs on the scrape thread, never on the tick thread.
    /// </summary>
    public void SetEntityCountProvider(Func<int> provider) => _entityCountProvider = provider;

    /// <summary>Record the duration of one tick, in seconds.</summary>
    public void RecordTickDuration(double seconds) => _tickDuration.Record(seconds, _mapTags);

    /// <summary>
    /// Record the duration of one tick from a <see cref="Stopwatch.GetTimestamp"/> pair.
    /// </summary>
    public void RecordTickDuration(long startTimestamp, long endTimestamp)
        => RecordTickDuration((endTimestamp - startTimestamp) / (double)Stopwatch.Frequency);

    /// <summary>Record how many queued inputs a tick applied.</summary>
    public void RecordProcessedInputs(int count)
    {
        if (count > 0) _processedInputs.Add(count, _mapTags);
    }

    /// <summary>Record snapshots handed to connections during a tick.</summary>
    public void RecordSnapshotsSent(int count)
    {
        if (count > 0) _snapshotsSent.Add(count, _mapTags);
    }

    /// <summary>Record a successful player save.</summary>
    public void RecordPlayerSaveOk() => _playerSaves.Add(1, _saveOkTags);

    /// <summary>Record a failed player save.</summary>
    public void RecordPlayerSaveError() => _playerSaves.Add(1, _saveErrorTags);

    /// <summary>Record a published game event of the given type.</summary>
    public void RecordEventPublished(string type)
        => _eventsPublished.Add(1, new KeyValuePair<string, object?>("type", type));

    /// <summary>Increment the connected-player gauge.</summary>
    public void PlayerJoined() => Interlocked.Increment(ref _playersOnline);

    /// <summary>Decrement the connected-player gauge (never below zero).</summary>
    public void PlayerLeft()
    {
        if (Interlocked.Decrement(ref _playersOnline) < 0)
        {
            Interlocked.Exchange(ref _playersOnline, 0);
        }
    }

    private Measurement<int> ObservePlayersOnline() => new(PlayersOnline, _mapTagArray);

    private Measurement<int> ObserveEntities()
    {
        var provider = _entityCountProvider;
        return new Measurement<int>(provider?.Invoke() ?? 0);
    }

    public void Dispose() => _meter.Dispose();
}
