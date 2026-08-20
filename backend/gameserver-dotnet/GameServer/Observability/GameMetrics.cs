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

    /// <summary>
    /// Instrument name for the per-group duration histogram. Public because
    /// <see cref="MetricsEndpoint"/> registers an explicit bucket view for it, and a view
    /// keyed off a string literal that drifted from the instrument would silently stop
    /// applying.
    /// </summary>
    public const string GroupDurationInstrument = "gameserver.sim.group.duration";

    private readonly Meter _meter;
    private readonly Histogram<double> _tickDuration;
    private readonly Histogram<double> _groupDuration;
    private readonly Counter<long> _groupRuns;
    private readonly Counter<long> _tickOverruns;
    private readonly Counter<long> _backlogDropped;
    private readonly Counter<long> _snapshotsSent;
    private readonly Counter<long> _playerSaves;
    private readonly Counter<long> _eventsPublished;
    private readonly Counter<long> _processedInputs;
    private readonly Counter<long> _resyncsRequested;

    // Pre-built tag sets — never allocate per record call.
    private readonly TagList _mapTags;
    private readonly TagList _saveOkTags;
    private readonly TagList _criticalTags;
    private readonly TagList _worldTags;
    private readonly TagList _backgroundTags;
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

        _criticalTags = new TagList { { "map_id", mapId }, { "group", "critical" } };
        _worldTags = new TagList { { "map_id", mapId }, { "group", "world" } };
        _backgroundTags = new TagList { { "map_id", mapId }, { "group", "background" } };

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

        // Counts CLIENT-INITIATED resyncs only, never the periodic keyframe. The
        // periodic one is routine (every N snapshots, by design); folding it in
        // would bury the signal under a constant background rate, and the signal
        // is the entire point of the counter. See docs/METRICS.md.
        _resyncsRequested = _meter.CreateCounter<long>(
            "gameserver.resyncs",
            description: "Keyframes requested by a client (MsgResync), i.e. a client that " +
                         "could not reconstruct state from the delta stream.");

        // Per-group instruments. One instrument with a `group` label rather than three
        // named instruments: the groups are configuration (SIM_*_HZ), so a metric name that
        // encoded the group would have to change if a group were ever renamed, and a
        // dashboard cannot sum across differently-named series. The TagLists are pre-built
        // per group for the same reason every other TagList here is — no allocation on the
        // tick thread.
        _groupDuration = _meter.CreateHistogram<double>(
            GroupDurationInstrument,
            unit: "s",
            description: "Wall-clock duration of one run of a simulation group, labelled by group.");

        _groupRuns = _meter.CreateCounter<long>(
            "gameserver.sim.group.runs",
            description: "Times a simulation group has run, labelled by group. The ratio " +
                         "between groups is the configured rate ratio, so a drift here is a " +
                         "scheduler bug rather than a load symptom.");

        _tickOverruns = _meter.CreateCounter<long>(
            "gameserver.tick.overruns",
            description: "Base ticks whose work exceeded the base period. A non-zero rate " +
                         "means the configured SIM_CRITICAL_HZ is not sustainable on this host.");

        _backlogDropped = _meter.CreateCounter<long>(
            "gameserver.tick.backlog_dropped",
            description: "Base ticks discarded because the loop fell too far behind wall " +
                         "clock to recover. Simulation time is behind real time by this much.");

        _meter.CreateObservableGauge(
            "gameserver.players.online",
            ObservePlayersOnline,
            description: "Players currently connected to this server.");

        _meter.CreateObservableGauge(
            "gameserver.entities",
            ObserveEntities,
            description: "Entities currently present in the world.");

        _meter.CreateObservableGauge(
            "gameserver.achieved_tick_hz",
            ObserveAchievedTickHz,
            description: "Measured base-tick rate over the last window, from the MONOTONIC " +
                         "clock. Compare with the configured SIM_CRITICAL_HZ: a healthy " +
                         "server has them equal. Deliberately not derived from wall time — " +
                         "a wall-clock rate on a host with a fast CLOCK_REALTIME reports a " +
                         "healthy loop as slow, which is issue #147. 0 = not measured yet.");
    }

    /// <summary>
    /// Register the callback used by the <c>gameserver_entities</c> gauge.
    /// The callback runs on the scrape thread, never on the tick thread.
    /// </summary>
    public void SetEntityCountProvider(Func<int> provider) => _entityCountProvider = provider;

    /// <summary>
    /// Register the callback used by the <c>gameserver_achieved_tick_hz</c> gauge.
    /// Runs on the scrape thread, never on the tick thread — the tick loop only ever writes
    /// the value, it never formats or exports it.
    /// </summary>
    public void SetAchievedTickHzProvider(Func<double> provider) => _achievedTickHzProvider = provider;

    private Func<double>? _achievedTickHzProvider;

    private Measurement<double> ObserveAchievedTickHz()
        => new(_achievedTickHzProvider?.Invoke() ?? 0d, _mapTags);

    /// <summary>Record the duration of one tick, in seconds.</summary>
    public void RecordTickDuration(double seconds) => _tickDuration.Record(seconds, _mapTags);

    /// <summary>
    /// Record the duration of one tick from a <see cref="Stopwatch.GetTimestamp"/> pair.
    /// </summary>
    public void RecordTickDuration(long startTimestamp, long endTimestamp)
        => RecordTickDuration((endTimestamp - startTimestamp) / (double)Stopwatch.Frequency);

    /// <summary>
    /// Record one run of a simulation group, from a <see cref="Stopwatch.GetTimestamp"/>
    /// pair. Increments the group's run counter as well, so the ratio between groups is
    /// observable without deriving it from the histogram count.
    /// </summary>
    public void RecordGroupRun(GameServer.Server.SimulationGroup group, long startTimestamp, long endTimestamp)
    {
        double seconds = (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
        TagList tags = TagsFor(group);
        _groupDuration.Record(seconds, tags);
        _groupRuns.Add(1, tags);
    }

    /// <summary>Record a base tick whose work exceeded the base period.</summary>
    public void RecordTickOverrun() => _tickOverruns.Add(1, _mapTags);

    /// <summary>Record base ticks dropped because the loop could not catch up.</summary>
    public void RecordTickBacklogDropped(long ticks)
    {
        if (ticks > 0) _backlogDropped.Add(ticks, _mapTags);
    }

    private TagList TagsFor(GameServer.Server.SimulationGroup group) => group switch
    {
        GameServer.Server.SimulationGroup.Critical => _criticalTags,
        GameServer.Server.SimulationGroup.World => _worldTags,
        _ => _backgroundTags,
    };

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

    /// <summary>
    /// Record a client asking for a full keyframe (<c>MsgResync</c>).
    /// </summary>
    /// <remarks>
    /// A client only sends this when it cannot reconstruct state from the delta
    /// stream. Since entity-id interning shipped, the most likely cause is a
    /// snapshot referencing an entity handle the client has no binding for — the
    /// two ends disagreeing about the interning table. See docs/METRICS.md for
    /// how to read a rising rate.
    /// </remarks>
    public void RecordResyncRequested() => _resyncsRequested.Add(1, _mapTags);

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
