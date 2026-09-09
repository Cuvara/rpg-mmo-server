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
/// gameserver.snapshots.coalesced      -> gameserver_snapshots_coalesced_total
/// gameserver.snapshots.frames_written  -> gameserver_snapshots_frames_written_total
/// gameserver.snapshots.bytes          -> gameserver_snapshots_bytes_total
/// gameserver.snapshots.entities_shed  -> gameserver_snapshots_entities_shed_total
/// gameserver.snapshots.removals_deferred -> gameserver_snapshots_removals_deferred_total
/// gameserver.snapshots.max_shed_age   -> gameserver_snapshots_max_shed_age
/// gameserver.transport.encrypted      -> gameserver_transport_encrypted{transport,cipher}
/// gameserver.transport.authenticated  -> gameserver_transport_authenticated{transport,cipher}
/// gameserver.player.saves             -> gameserver_player_saves_total
/// gameserver.events.published         -> gameserver_events_published_total
/// gameserver.events.dropped           -> gameserver_events_dropped_total
/// gameserver.events.publish_failures  -> gameserver_events_publish_failures_total
/// gameserver.tick.processed_inputs    -> gameserver_tick_processed_inputs_total
/// gameserver.handshakes.pending       -> gameserver_handshakes_pending
/// gameserver.handshakes.rejected      -> gameserver_handshakes_rejected_total{reason}
/// gameserver.inputs.dropped           -> gameserver_inputs_dropped_total{reason}
/// gameserver.inputs.coalesced         -> gameserver_inputs_coalesced_total
/// gameserver.transfers.rejected       -> gameserver_transfers_rejected_total
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
    private readonly Counter<long> _snapshotsCoalesced;
    private readonly Counter<long> _snapshotFramesWritten;
    private readonly string _mapId;
    private readonly Counter<long> _snapshotBytes;
    private readonly Counter<long> _snapshotEntitiesShed;
    private readonly Counter<long> _snapshotRemovalsDeferred;
    private readonly Counter<long> _playerSaves;
    private readonly Counter<long> _eventsPublished;
    private readonly Counter<long> _eventsDropped;
    private readonly Counter<long> _eventPublishFailures;
    private readonly Counter<long> _processedInputs;
    private readonly Counter<long> _resyncsRequested;
    private readonly Counter<long> _playersKicked;
    private readonly Counter<long> _handshakesRejected;
    private readonly Counter<long> _unversionedHandshakes;
    private readonly Counter<long> _inputsRejected;
    private readonly Counter<long> _anomalyAlerts;
    private readonly TagList[] _inputRejectionTags;
    private readonly Counter<long> _inputsDropped;
    private readonly Counter<long> _inputsCoalesced;
    private readonly Counter<long> _transfersRejected;

    // Pre-built tag sets — never allocate per record call.
    private readonly TagList _mapTags;
    private readonly TagList _handshakePoolFullTags;
    private readonly TagList _handshakeTimeoutTags;
    private readonly TagList _handshakeMalformedTags;
    private readonly TagList _handshakeProtocolVersionTags;
    private readonly TagList _inputBudgetTags;
    private readonly TagList _inputQueueFullTags;
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
        _mapId = mapId;
        _mapTags = new TagList { { "map_id", mapId } };
        _saveOkTags = new TagList { { "status", "ok" } };
        _saveErrorTags = new TagList { { "status", "error" } };

        _criticalTags = new TagList { { "map_id", mapId }, { "group", "critical" } };
        _worldTags = new TagList { { "map_id", mapId }, { "group", "world" } };
        _backgroundTags = new TagList { { "map_id", mapId }, { "group", "background" } };

        _handshakePoolFullTags = new TagList { { "map_id", mapId }, { "reason", "pool_full" } };
        _handshakeTimeoutTags = new TagList { { "map_id", mapId }, { "reason", "timeout" } };
        _handshakeMalformedTags = new TagList { { "map_id", mapId }, { "reason", "malformed" } };
        _handshakeProtocolVersionTags = new TagList { { "map_id", mapId }, { "reason", "protocol_version" } };
        _inputBudgetTags = new TagList { { "map_id", mapId }, { "reason", "connection_budget" } };
        _inputQueueFullTags = new TagList { { "map_id", mapId }, { "reason", "queue_full" } };

        _tickDuration = _meter.CreateHistogram<double>(
            TickDurationInstrument,
            unit: "s",
            description: "Wall-clock duration of a single simulation tick.");

        _snapshotsSent = _meter.CreateCounter<long>(
            "gameserver.snapshots.sent",
            description: "Snapshots STAGED on player connections. A staged snapshot is not " +
                         "necessarily a frame on a socket -- see snapshots.coalesced.");

        _snapshotFramesWritten = _meter.CreateCounter<long>(
            "gameserver.snapshots.frames_written",
            description: "Snapshot frames written to client sockets. The only figure " +
                         "comparable with what a client counts arriving.");

        _snapshotBytes = _meter.CreateCounter<long>(
            "gameserver.snapshots.bytes",
            unit: "By",
            description: "Bytes of snapshot frames written to client sockets, envelope and " +
                         "length prefix included. Divide by players_online and by the scrape " +
                         "interval for per-client downlink KB/s, the figure ADR-7's < 50 KB/s " +
                         "mobile threshold is about.");

        _snapshotEntitiesShed = _meter.CreateCounter<long>(
            "gameserver.snapshots.entities_shed",
            description: "Entity updates DEFERRED by the per-connection downlink budget " +
                         "(GAMESERVER_MAX_SNAPSHOT_BYTES). Deferred, not dropped: the entity " +
                         "stays dirty and is re-offered on the next snapshot. A non-zero rate " +
                         "means clients are seeing stale entities, and the pair to read it " +
                         "with is max_shed_age -- a high rate with a low age is the budget " +
                         "working, a high age is a client falling behind.");

        _snapshotRemovalsDeferred = _meter.CreateCounter<long>(
            "gameserver.snapshots.removals_deferred",
            description: "Despawn notifications deferred by the downlink budget. Worse than a " +
                         "deferred update -- the client renders an entity that no longer " +
                         "exists -- so this should be flat at zero; despawns outrank every " +
                         "non-self update and only a budget too small for the despawn list " +
                         "itself can make it move.");

        _snapshotsCoalesced = _meter.CreateCounter<long>(
            "gameserver.snapshots.coalesced",
            description: "Staged snapshots replaced before the write task claimed them, so " +
                         "that tick's frame was never sent. The difference between this and " +
                         "snapshots.sent is what a client actually receives.");

        _playerSaves = _meter.CreateCounter<long>(
            "gameserver.player.saves",
            description: "Player state save attempts, labelled by outcome.");

        _eventsPublished = _meter.CreateCounter<long>(
            "gameserver.events.published",
            description: "Game events published to the event stream, labelled by type.");

        _eventsDropped = _meter.CreateCounter<long>(
            "gameserver.events.dropped",
            description: "Events dropped oldest-first because the publish queue was full " +
                         "(Redis down long enough to fill the bound) or already shut down.");

        _eventPublishFailures = _meter.CreateCounter<long>(
            "gameserver.events.publish_failures",
            description: "Events dropped after exhausting the XADD retry budget. Each is " +
                         "one event that never reached the gateway's relay.");

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

        _playersKicked = _meter.CreateCounter<long>(
            "gameserver.players.kicked",
            description: "Connections force-closed by a session_superseded event " +
                         "(duplicate login: a newer login for the same user superseded this one). " +
                         "Entity released immediately, no reconnect hold.");

        _handshakesRejected = _meter.CreateCounter<long>(
            "gameserver.handshakes.rejected",
            description: "Accepted transports refused before authentication, labelled by " +
                         "reason: pool_full (GAMESERVER_MAX_PENDING_HANDSHAKES reached, closed " +
                         "on accept), timeout (no complete join frame within " +
                         "GAMESERVER_HANDSHAKE_TIMEOUT_MS), malformed (first frame was not a " +
                         "well-formed MsgJoinToken), protocol_version (the client advertised a " +
                         "wire protocol version this build cannot serve, or advertised none while " +
                         "GAMESERVER_MIN_PROTOCOL_VERSION required one). Not a capacity refusal: " +
                         "those are authenticated and logged separately.");

        _unversionedHandshakes = _meter.CreateCounter<long>(
            "gameserver.handshakes.unversioned",
            description: "Clients admitted without advertising a wire protocol version. This is a " +
                         "migration instrument, not a health metric: admitting one is admission on " +
                         "trust, since a pre-versioning build is indistinguishable from a " +
                         "non-conforming one. This going flat at zero is the evidence that raising " +
                         "GAMESERVER_MIN_PROTOCOL_VERSION will not lock out real players.");

        _inputsDropped = _meter.CreateCounter<long>(
            "gameserver.inputs.dropped",
            description: "Client inputs discarded at ingest, labelled by reason: " +
                         "connection_budget (one connection exceeded " +
                         "GAMESERVER_MAX_INPUTS_PER_TICK between two drains) or queue_full " +
                         "(the world-wide pending queue reached GAMESERVER_MAX_PENDING_INPUTS).");

        _inputsRejected = _meter.CreateCounter<long>(
            "gameserver.inputs.rejected",
            description: "Client inputs REFUSED by validation, labelled by reason. Not the " +
                         "same as gameserver.inputs.dropped, which is ingest backpressure: " +
                         "these reached the tick and the server declined to act on them. " +
                         "Most reasons are latency-explicable and rise on an honest client " +
                         "with a bad connection; invalid_direction is the exception, being " +
                         "something the shipped client cannot emit. See docs/METRICS.md.");

        // PRIMED AT ZERO, one series per reason. The OTel Prometheus exporter emits an
        // instrument only once it has recorded a value, so an alarm counter that has never
        // fired is ABSENT rather than zero -- indistinguishable from a broken scrape or a
        // build without the feature (docs/METRICS.md). These are exactly that kind of
        // counter: absence is the healthy reading. Recording an explicit 0 for every
        // reason at construction makes "no rejections" visibly zero on the scrape.
        //
        // This is only possible because InputRejectionReason is a BOUNDED enum. It is the
        // concrete payoff of not labelling with the free-form reason strings.
        _inputRejectionTags = new TagList[GameServer.Input.InputRejection.All.Length];
        foreach (var reason in GameServer.Input.InputRejection.All)
        {
            var tags = new TagList
            {
                { "map_id", mapId },
                { "reason", GameServer.Input.InputRejection.Label(reason) },
            };
            _inputRejectionTags[(int)reason] = tags;
            _inputsRejected.Add(0, tags);
        }

        _anomalyAlerts = _meter.CreateCounter<long>(
            "gameserver.input.anomaly.alerts",
            description: "Times an account's decaying input-anomaly score first crossed the " +
                         "alert threshold. OBSERVATION ONLY: nothing is done to the player. " +
                         "The threshold is not yet tuned against a measured honest-player " +
                         "baseline, so treat a non-zero rate as a prompt to look, not as a " +
                         "verdict -- see docs/METRICS.md.");
        // Primed at zero for the same reason as the rejection counters: this is an alarm
        // counter whose healthy reading is no rate at all, and an absent series is
        // indistinguishable from a broken scrape.
        _anomalyAlerts.Add(0, _mapTags);

        _inputsCoalesced = _meter.CreateCounter<long>(
            "gameserver.inputs.coalesced",
            description: "Movement-only inputs that replaced the sender's previous queued " +
                         "movement in place instead of growing the queue. Not a loss: the " +
                         "tick integrates one direction per player per tick regardless.");

        _transfersRejected = _meter.CreateCounter<long>(
            "gameserver.transfers.rejected",
            description: "MsgTransferMap requests refused because a transfer was already " +
                         "running on that connection.");

        _meter.CreateObservableGauge(
            "gameserver.handshakes.pending",
            ObservePendingHandshakes,
            description: "Accepted transports that have not completed the join handshake. " +
                         "Bounded by GAMESERVER_MAX_PENDING_HANDSHAKES and outside " +
                         "players_online, which counts only authenticated connections.");

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

        // Gauges, not counters, and that choice is the point: a counter that never
        // increments is ABSENT from /metrics, and "is this server encrypted" must never
        // answer by being missing. An observable gauge's callback runs on every scrape, so
        // these two always report — including, and especially, when they report 0.
        _meter.CreateObservableGauge(
            "gameserver.transport.encrypted",
            ObserveTransportEncrypted,
            description: "1 when packets leave this server as ciphertext, 0 when they are in " +
                         "cleartext. 0 is the DEFAULT (transport=tcp has no packet encryption, " +
                         "and TRANSPORT_KEY defaults to empty) -- alert on it rather than " +
                         "assuming it. Labelled with the transport and cipher in force.");

        _meter.CreateObservableGauge(
            "gameserver.transport.authenticated",
            ObserveTransportAuthenticated,
            description: "1 when tampering with a packet in flight is detectable. Currently 0 on " +
                         "every supported configuration: the KCP path is AES-CFB with a CRC32, " +
                         "and a CRC32 is linear, not a MAC. Deliberately separate from " +
                         "transport.encrypted so encryption cannot be read as integrity.");

        _meter.CreateObservableGauge(
            "gameserver.snapshots.max_shed_age",
            ObserveMaxShedAge,
            description: "Longest deferral, in snapshots, any entity on any live connection " +
                         "has reached since that connection was opened. High-water mark, so " +
                         "it never falls while a connection lives. The budget schedules " +
                         "strictly oldest-first, so this is bounded by the number of dirty " +
                         "entities in one observer's AOI, not by session length -- a value " +
                         "that keeps climbing means the budget is too small for the crowd.");

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

    /// <summary>
    /// Record snapshots STAGED for connections during a tick.
    /// </summary>
    /// <remarks>
    /// Staged, not written to a socket, and the difference is not academic: a gather that
    /// finds the previous one still unclaimed replaces it, and that tick's frame is never
    /// sent. Measured against a live client, the server staged 15/s while the client
    /// measured 14.2/s arriving. Pair this with
    /// <see cref="RecordSnapshotsCoalesced"/> before concluding a client is losing frames.
    /// </remarks>
    public void RecordSnapshotsSent(int count)
    {
        if (count > 0) _snapshotsSent.Add(count, _mapTags);
    }

    /// <summary>
    /// Record staged snapshots that were replaced before the write task claimed them.
    /// </summary>
    public void RecordSnapshotsCoalesced(long count)
    {
        if (count > 0) _snapshotsCoalesced.Add(count, _mapTags);
    }

    /// <summary>
    /// Record one tick's downlink-budget activity: bytes on the wire, entity updates and
    /// despawns the budget deferred, and the longest deferral seen so far.
    /// </summary>
    /// <remarks>
    /// One call, not four, because these are read together or not at all. Bytes without
    /// shedding is a link that is fine; shedding without bytes is meaningless; and the
    /// deferral age is what separates "the cap is doing its job" from "a client is falling
    /// permanently behind". The lesson this codebase keeps relearning is that a check
    /// nobody reads is not a check -- so these also surface on <c>/status</c>, not only in
    /// a Prometheus scrape.
    /// </remarks>
    public void RecordSnapshotBudget(long bytes, long entitiesShed, long removalsDeferred, int maxShedAge)
    {
        if (bytes > 0)
        {
            _snapshotBytes.Add(bytes, _mapTags);
            Interlocked.Add(ref _snapshotBytesTotal, bytes);
        }
        if (entitiesShed > 0)
        {
            _snapshotEntitiesShed.Add(entitiesShed, _mapTags);
            Interlocked.Add(ref _snapshotEntitiesShedTotal, entitiesShed);
        }
        if (removalsDeferred > 0)
        {
            _snapshotRemovalsDeferred.Add(removalsDeferred, _mapTags);
            Interlocked.Add(ref _snapshotRemovalsDeferredTotal, removalsDeferred);
        }
        if (maxShedAge > Volatile.Read(ref _maxShedAge)) Volatile.Write(ref _maxShedAge, maxShedAge);
    }

    private int _maxShedAge;
    private long _snapshotBytesTotal;
    private long _snapshotEntitiesShedTotal;
    private long _snapshotRemovalsDeferredTotal;

    /// <summary>Snapshot bytes written to sockets since start. Mirrors the counter for <c>/status</c>.</summary>
    public long SnapshotBytes => Interlocked.Read(ref _snapshotBytesTotal);

    /// <summary>Entity updates deferred by the downlink budget since start.</summary>
    public long SnapshotEntitiesShed => Interlocked.Read(ref _snapshotEntitiesShedTotal);

    /// <summary>Despawn notifications deferred by the downlink budget since start.</summary>
    public long SnapshotRemovalsDeferred => Interlocked.Read(ref _snapshotRemovalsDeferredTotal);

    /// <summary>Longest snapshot deferral observed on this server (see <see cref="RecordSnapshotBudget"/>).</summary>
    public int MaxShedAge => Volatile.Read(ref _maxShedAge);

    private Measurement<int> ObserveMaxShedAge() => new(Volatile.Read(ref _maxShedAge), _mapTags);

    private TagList _transportTags;
    private volatile bool _transportEncrypted;
    private volatile bool _transportAuthenticated;

    /// <summary>
    /// Publish the transport confidentiality posture, once, at startup.
    /// </summary>
    /// <remarks>
    /// Called from the composition root because the posture is configuration, not something
    /// the server discovers. The tag set is built here rather than in the constructor
    /// because the transport and cipher are not known until it is called; both are constant
    /// for the process lifetime, so cardinality is one series each.
    /// </remarks>
    public void SetTransportPosture(string transport, string cipher, bool encrypted, bool authenticated)
    {
        _transportTags = new TagList
        {
            { "map_id", _mapId },
            { "transport", transport },
            { "cipher", cipher },
        };
        _transportEncrypted = encrypted;
        _transportAuthenticated = authenticated;
    }

    private Measurement<int> ObserveTransportEncrypted()
        => new(_transportEncrypted ? 1 : 0, _transportTags);

    private Measurement<int> ObserveTransportAuthenticated()
        => new(_transportAuthenticated ? 1 : 0, _transportTags);

    /// <summary>
    /// Record snapshot frames actually written to sockets.
    /// </summary>
    /// <remarks>
    /// The only server figure comparable with what a client counts arriving.
    /// </remarks>
    public void RecordSnapshotFramesWritten(long count)
    {
        if (count > 0) _snapshotFramesWritten.Add(count, _mapTags);
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

    /// <summary>
    /// Record one duplicate-login kick. Also mirrored into
    /// <see cref="PlayersKicked"/> so <c>/status</c> and tests can read the count
    /// without a metrics scrape — the same dual-surface pattern as
    /// <c>RedisEventStream.Dropped</c>.
    /// </summary>
    public void RecordPlayerKicked()
    {
        Interlocked.Increment(ref _playersKickedCount);
        _playersKicked.Add(1, _mapTags);
    }

    private long _playersKickedCount;

    /// <summary>Duplicate-login kicks executed since start (see <see cref="RecordPlayerKicked"/>).</summary>
    public long PlayersKicked => Interlocked.Read(ref _playersKickedCount);

    /// <summary>Record a successful player save.</summary>
    public void RecordPlayerSaveOk() => _playerSaves.Add(1, _saveOkTags);

    /// <summary>Record a failed player save.</summary>
    public void RecordPlayerSaveError() => _playerSaves.Add(1, _saveErrorTags);

    /// <summary>Record a published game event of the given type.</summary>
    public void RecordEventPublished(string type)
        => _eventsPublished.Add(1, new KeyValuePair<string, object?>("type", type));

    /// <summary>Record an event dropped by the bounded publish queue (oldest-first).</summary>
    public void RecordEventDropped() => _eventsDropped.Add(1);

    /// <summary>Record an event dropped after exhausting the publish retry budget.</summary>
    public void RecordEventPublishFailure() => _eventPublishFailures.Add(1);

    // ── Pre-join admission (workspace audit F03) ─────────────────────────────

    private Func<int>? _pendingHandshakesProvider;
    private long _handshakesRejectedPoolFull;
    private long _handshakesRejectedTimeout;
    private long _handshakesRejectedMalformed;
    private long _handshakesRejectedProtocolVersion;
    private long _unversionedHandshakeCount;

    /// <summary>
    /// Register the callback used by the <c>gameserver_handshakes_pending</c> gauge:
    /// accepted transports that have not completed the join handshake. Separate from
    /// <c>players_online</c> on purpose — these are sockets outside the capacity limit,
    /// which is exactly why they need their own number.
    /// </summary>
    public void SetPendingHandshakesProvider(Func<int> provider) => _pendingHandshakesProvider = provider;

    private Measurement<int> ObservePendingHandshakes()
        => new(_pendingHandshakesProvider?.Invoke() ?? 0, _mapTags);

    /// <summary>
    /// Record a handshake refused before authentication. Mirrored into the
    /// <c>HandshakesRejected*</c> properties so <c>/status</c> and tests can read the
    /// counts without a metrics scrape.
    /// </summary>
    public void RecordHandshakeRejected(HandshakeRejectReason reason)
    {
        switch (reason)
        {
            case HandshakeRejectReason.PoolFull:
                Interlocked.Increment(ref _handshakesRejectedPoolFull);
                _handshakesRejected.Add(1, _handshakePoolFullTags);
                break;
            case HandshakeRejectReason.Timeout:
                Interlocked.Increment(ref _handshakesRejectedTimeout);
                _handshakesRejected.Add(1, _handshakeTimeoutTags);
                break;
            case HandshakeRejectReason.ProtocolVersion:
                Interlocked.Increment(ref _handshakesRejectedProtocolVersion);
                _handshakesRejected.Add(1, _handshakeProtocolVersionTags);
                break;
            default:
                Interlocked.Increment(ref _handshakesRejectedMalformed);
                _handshakesRejected.Add(1, _handshakeMalformedTags);
                break;
        }
    }

    /// <summary>Accepted transports closed because the pending-handshake pool was full.</summary>
    public long HandshakesRejectedPoolFull => Interlocked.Read(ref _handshakesRejectedPoolFull);

    /// <summary>Handshakes that did not deliver a complete join frame before the deadline.</summary>
    public long HandshakesRejectedTimeout => Interlocked.Read(ref _handshakesRejectedTimeout);

    /// <summary>Handshakes whose first frame was not a well-formed <c>MsgJoinToken</c>.</summary>
    public long HandshakesRejectedMalformed => Interlocked.Read(ref _handshakesRejectedMalformed);

    /// <summary>Handshakes refused for an unsupported wire protocol version.</summary>
    public long HandshakesRejectedProtocolVersion => Interlocked.Read(ref _handshakesRejectedProtocolVersion);

    /// <summary>Clients admitted without advertising a wire protocol version.</summary>
    public long UnversionedHandshakes => Interlocked.Read(ref _unversionedHandshakeCount);

    /// <summary>
    /// Record one client admitted without advertising a wire protocol version.
    /// </summary>
    public void RecordUnversionedHandshake()
    {
        Interlocked.Increment(ref _unversionedHandshakeCount);
        _unversionedHandshakes.Add(1, _mapTags);
    }

    /// <summary>All pre-authentication handshake rejections, every reason.</summary>
    public long HandshakesRejected
        => HandshakesRejectedPoolFull + HandshakesRejectedTimeout + HandshakesRejectedMalformed
           + HandshakesRejectedProtocolVersion;

    // ── Bounded ingestion (workspace audit F04) ──────────────────────────────

    private long _inputsDroppedConnectionBudget;
    private long _inputsDroppedQueueFull;
    private long _inputsCoalescedCount;
    private long _anomalyAlertCount;
    private readonly long[] _inputsRejectedByReason =
        new long[GameServer.Input.InputRejection.All.Length];
    private long _transfersRejectedCount;

    /// <summary>Record one input dropped at ingest for <paramref name="reason"/>.</summary>
    public void RecordInputDropped(GameServer.World.InputIngestResult reason)
    {
        if (reason == GameServer.World.InputIngestResult.DroppedQueueFull)
        {
            Interlocked.Increment(ref _inputsDroppedQueueFull);
            _inputsDropped.Add(1, _inputQueueFullTags);
        }
        else
        {
            Interlocked.Increment(ref _inputsDroppedConnectionBudget);
            _inputsDropped.Add(1, _inputBudgetTags);
        }
    }

    /// <summary>Record one movement input coalesced in place at ingest (queue did not grow).</summary>
    public void RecordInputCoalesced()
    {
        Interlocked.Increment(ref _inputsCoalescedCount);
        _inputsCoalesced.Add(1, _mapTags);
    }

    /// <summary>Inputs dropped because one connection exhausted its per-tick budget.</summary>
    public long InputsDroppedConnectionBudget => Interlocked.Read(ref _inputsDroppedConnectionBudget);

    /// <summary>Inputs dropped because the world-wide pending queue was full.</summary>
    public long InputsDroppedQueueFull => Interlocked.Read(ref _inputsDroppedQueueFull);

    /// <summary>
    /// Record one client input refused by validation.
    /// </summary>
    /// <remarks>
    /// Mirrored into a plain array read by <c>/status</c>, the same dual-surface pattern as
    /// the handshake reject counters -- and for a sharper reason here: these are alarm
    /// counters whose healthy value is zero, and a Prometheus counter at zero is only
    /// visible because of the priming in the constructor. <c>/status</c> publishes them as
    /// plain zeros regardless.
    /// </remarks>
    public void RecordInputRejected(GameServer.Input.InputRejectionReason reason)
    {
        int i = (int)reason;
        if ((uint)i >= (uint)_inputsRejectedByReason.Length) return;

        Interlocked.Increment(ref _inputsRejectedByReason[i]);
        _inputsRejected.Add(1, _inputRejectionTags[i]);
    }

    /// <summary>
    /// Record one account crossing the input-anomaly alert threshold. Observation only.
    /// </summary>
    public void RecordAnomalyAlert()
    {
        Interlocked.Increment(ref _anomalyAlertCount);
        _anomalyAlerts.Add(1, _mapTags);
    }

    /// <summary>Accounts that have crossed the anomaly alert threshold since start.</summary>
    public long AnomalyAlerts => Interlocked.Read(ref _anomalyAlertCount);

    /// <summary>Inputs refused by validation for one reason.</summary>
    public long InputsRejected(GameServer.Input.InputRejectionReason reason)
    {
        int i = (int)reason;
        return (uint)i < (uint)_inputsRejectedByReason.Length
            ? Interlocked.Read(ref _inputsRejectedByReason[i])
            : 0;
    }

    /// <summary>Inputs refused by validation, every reason.</summary>
    public long InputsRejectedTotal
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _inputsRejectedByReason.Length; i++)
                total += Interlocked.Read(ref _inputsRejectedByReason[i]);
            return total;
        }
    }

    /// <summary>All inputs dropped at ingest, every reason.</summary>
    public long InputsDropped => InputsDroppedConnectionBudget + InputsDroppedQueueFull;

    /// <summary>Movement inputs coalesced at ingest, since start.</summary>
    public long InputsCoalesced => Interlocked.Read(ref _inputsCoalescedCount);

    /// <summary>Record a <c>MsgTransferMap</c> refused because one was already running on that connection.</summary>
    public void RecordTransferRejected()
    {
        Interlocked.Increment(ref _transfersRejectedCount);
        _transfersRejected.Add(1, _mapTags);
    }

    /// <summary>Transfer requests refused as concurrent, since start.</summary>
    public long TransfersRejected => Interlocked.Read(ref _transfersRejectedCount);

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

/// <summary>Why a handshake was refused before authentication — the <c>reason</c> label of <c>gameserver_handshakes_rejected_total</c>.</summary>
public enum HandshakeRejectReason
{
    /// <summary>The pending-handshake pool (<c>GAMESERVER_MAX_PENDING_HANDSHAKES</c>) was full at accept.</summary>
    PoolFull,

    /// <summary>No complete join frame arrived within <c>GAMESERVER_HANDSHAKE_TIMEOUT_MS</c>.</summary>
    Timeout,

    /// <summary>The first frame was not a well-formed <c>MsgJoinToken</c> (wrong type, bad length prefix, undecodable body, or EOF mid-frame).</summary>
    Malformed,

    /// <summary>
    /// The client advertised a wire protocol version this build cannot serve, or
    /// advertised none while <c>GAMESERVER_MIN_PROTOCOL_VERSION</c> required one.
    /// Distinct from <see cref="Malformed"/> on purpose: the frame parsed perfectly,
    /// and telling the two apart is the difference between "a client is broken" and
    /// "a rollout is skewed".
    /// </summary>
    ProtocolVersion,
}
