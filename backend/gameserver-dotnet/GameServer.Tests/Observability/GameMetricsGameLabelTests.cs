using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GameServer.Tests.Observability;

/// <summary>
/// ADR-33 D3: every measurement on the <c>rpg.gameserver</c> meter carries the
/// <c>game</c> label next to <c>map_id</c>.
/// </summary>
/// <remarks>
/// <para>
/// The audit is meter-wide on purpose. It enables EVERY instrument published under the
/// test's meter name, with no allow-list of instrument names, and listens for every
/// numeric type a <see cref="Meter"/> instrument can record. A future instrument that
/// records without <c>game</c> therefore fails here without anyone having to remember
/// to add it to this file; one that is never driven fails the silent-instrument check,
/// which forces whoever adds it to feed it below.
/// </para>
/// <para>
/// Uses a raw <see cref="MeterListener"/> rather than the OTel in-memory exporter that
/// <see cref="GameMetricsTests"/> uses: the listener sees each measurement with the exact
/// tags it was recorded with, before any aggregation could merge or drop a series.
/// </para>
/// </remarks>
public class GameMetricsGameLabelTests
{
    private const string MapId = "map_label";

    /// <summary>
    /// Instruments allowed to report zero measurements after <see cref="DriveEverything"/>.
    /// Every instrument <see cref="GameMetrics"/> creates today can be fed cheaply, so the
    /// list is empty. Add an entry only for an instrument that genuinely cannot be driven
    /// from a unit test, with a comment saying why: an entry here is an instrument whose
    /// labels this test no longer proves.
    /// </summary>
    private static readonly string[] AllowedSilent = [];

    /// <summary>
    /// Collects every measurement on one meter name and records each one whose tags do
    /// not carry exactly one <c>game</c> and one <c>map_id</c> with the expected values.
    /// </summary>
    private sealed class MeterAudit : IDisposable
    {
        private readonly string _meterName;
        private readonly string _expectedGame;
        private readonly string _expectedMap;
        private readonly MeterListener _listener = new();
        private readonly object _gate = new();

        public HashSet<string> Published { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Measured { get; } = new(StringComparer.Ordinal);
        public List<string> Violations { get; } = new();

        public MeterAudit(string meterName, string expectedGame, string expectedMap)
        {
            _meterName = meterName;
            _expectedGame = expectedGame;
            _expectedMap = expectedMap;

            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name != _meterName) return;
                lock (_gate) Published.Add(instrument.Name);
                listener.EnableMeasurementEvents(instrument);
            };

            // Every type System.Diagnostics.Metrics accepts, not only the ones GameMetrics
            // uses today (long, int, double): a callback missing for a type means an
            // instrument of that type is enabled but its measurements are never seen.
            _listener.SetMeasurementEventCallback<byte>(OnMeasurement);
            _listener.SetMeasurementEventCallback<short>(OnMeasurement);
            _listener.SetMeasurementEventCallback<int>(OnMeasurement);
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.SetMeasurementEventCallback<float>(OnMeasurement);
            _listener.SetMeasurementEventCallback<double>(OnMeasurement);
            _listener.SetMeasurementEventCallback<decimal>(OnMeasurement);
            _listener.Start();
        }

        /// <summary>Run every observable instrument's callback once, as a scrape would.</summary>
        public void Observe() => _listener.RecordObservableInstruments();

        private void OnMeasurement<T>(
            Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            where T : struct
        {
            int games = 0, maps = 0;
            object? game = null, map = null;
            var rendered = new List<string>(tags.Length);
            foreach (var tag in tags)
            {
                rendered.Add($"{tag.Key}={tag.Value}");
                if (tag.Key == "game") { games++; game = tag.Value; }
                else if (tag.Key == "map_id") { maps++; map = tag.Value; }
            }

            bool ok = games == 1 && Equals(game as string, _expectedGame)
                   && maps == 1 && Equals(map as string, _expectedMap);

            lock (_gate)
            {
                Measured[instrument.Name] = Measured.GetValueOrDefault(instrument.Name) + 1;
                if (!ok)
                {
                    Violations.Add(
                        $"{instrument.Name}: expected game={_expectedGame} map_id={_expectedMap}, " +
                        $"got [{string.Join(", ", rendered)}]");
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private static string UniqueMeterName(string testName) =>
        $"rpg.gameserver.test.{testName}.{Guid.NewGuid():N}";

    /// <summary>
    /// Call every public Record*/Set*/provider method of <see cref="GameMetrics"/> at least
    /// once, with every value of each enum-labelled argument, then run the observable
    /// callbacks. A new instrument added to <see cref="GameMetrics"/> must be fed here (or
    /// listed in <see cref="AllowedSilent"/>), or the silent-instrument check fails.
    /// </summary>
    private static void DriveEverything(GameMetrics m, MeterAudit audit)
    {
        // Tick loop and simulation groups.
        m.RecordTickDuration(0.001);
        long t0 = Stopwatch.GetTimestamp();
        m.RecordTickDuration(t0, t0 + 1);
        foreach (var group in Enum.GetValues<SimulationGroup>())
            m.RecordGroupRun(group, t0, t0 + 1);
        m.RecordTickOverrun();
        m.RecordTickTimerFallback();
        m.RecordTickBacklogDropped(2);
        m.RecordProcessedInputs(1);

        // Snapshots.
        m.RecordSnapshotsSent(1);
        m.RecordSnapshotsCoalesced(1);
        m.RecordSnapshotGather(3, 3);
        m.RecordSnapshotAnchorMissing(1);
        m.RecordSnapshotBudget(bytes: 100, entitiesShed: 1, removalsDeferred: 1, maxShedAge: 1);
        m.RecordSnapshotSchedule(deferredByInterval: 1, maxStateAge: 1, maxUpdateGapMs: 1);
        m.RecordSnapshotFramesWritten(1);
        m.RecordResyncRequested();

        // Transport posture and the KCP listener. A null stats object still reports
        // every KCP series at zero, with the same tags.
        m.SetTransportPosture("kcp", "none", encrypted: false, authenticated: false);
        m.SetKcpStatsProvider(() => null);

        // Players, persistence, events: the last four were untagged before ADR-33.
        m.PlayerJoined();
        m.RecordPlayerKicked();
        m.RecordPlayerSaveOk();
        m.RecordPlayerSaveError();
        m.RecordEventPublished("entity_killed");
        m.RecordEventDropped();
        m.RecordEventPublishFailure();

        // Admission.
        m.SetPendingHandshakesProvider(() => 1);
        m.SetConnectionCountProvider(() => 1);
        foreach (var reason in Enum.GetValues<HandshakeRejectReason>())
            m.RecordHandshakeRejected(reason);
        m.RecordUnversionedHandshake();

        // Nakama reward path.
        foreach (var outcome in Enum.GetValues<GameServer.Nakama.KillRewardOutcome>())
            m.RecordNakamaRewardOutcome(outcome);

        // Ingest, validation, anti-cheat observation.
        foreach (var result in Enum.GetValues<InputIngestResult>())
            m.RecordInputDropped(result);
        m.RecordInputCoalesced();
        m.PrimeCounters();
        foreach (var reason in GameServer.Input.InputRejection.All)
            m.RecordInputRejected(reason);
        m.RecordUnknownMessageType();
        m.RecordAnomalyAlert();
        m.RecordAttackRateViolation();
        m.RecordTransferRejected();

        // Commands (ADR-30): two reasons, so the lazy per-reason tag cache builds twice.
        m.RecordCommandReceived();
        m.RecordCommandAccepted();
        m.RecordCommandRejected("on_cooldown");
        m.RecordCommandRejected("protocol_version");

        // Observable gauges and counters: entities, achieved Hz, GC, process, KCP, ...
        m.SetEntityCountProvider(() => 2);
        m.SetAchievedTickHzProvider(() => 60d);
        audit.Observe();
    }

    [Theory]
    [InlineData(null, "cuvara")]     // no GAMESERVER_GAME_ID: the default game
    [InlineData("", "cuvara")]       // empty is the default game too (GameId.GameOrDefault)
    [InlineData("testgame2", "testgame2")]
    public void EveryMeasurementOnTheMeter_CarriesGameAndMapId(string? gameId, string expectedGame)
    {
        string meterName = UniqueMeterName(nameof(EveryMeasurementOnTheMeter_CarriesGameAndMapId));
        using var audit = new MeterAudit(meterName, expectedGame, MapId);
        using var metrics = new GameMetrics(MapId, meterName, gameId);

        Assert.Equal(expectedGame, metrics.GameId);

        DriveEverything(metrics, audit);

        Assert.NotEmpty(audit.Published);

        // (a) Every published instrument produced at least one measurement, so (b) below
        // speaks for every instrument on the meter rather than for the ones that happened
        // to fire.
        var silent = audit.Published
            .Where(name => !audit.Measured.ContainsKey(name))
            .Except(AllowedSilent, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.True(silent.Count == 0,
            "Instruments with no measurement (feed them in DriveEverything, or list them in " +
            "AllowedSilent with a reason): " + string.Join(", ", silent));

        // An allow-list entry that is no longer silent (or no longer exists) is stale.
        foreach (string allowed in AllowedSilent)
        {
            Assert.True(audit.Published.Contains(allowed) && !audit.Measured.ContainsKey(allowed),
                $"AllowedSilent entry {allowed} is stale: remove it.");
        }

        // (b) Every measurement carries game and map_id, exactly once each.
        Assert.True(audit.Violations.Count == 0,
            $"{audit.Violations.Count} measurement(s) without the base labels:\n" +
            string.Join("\n", audit.Violations));

        // The five paths that carried no map_id before ADR-33 were measured, so (b) covers them.
        foreach (string name in new[]
                 {
                     "gameserver.player.saves",
                     "gameserver.events.published",
                     "gameserver.events.dropped",
                     "gameserver.events.publish_failures",
                     "gameserver.entities",
                 })
        {
            Assert.True(audit.Measured.ContainsKey(name), $"{name} was not measured");
        }
    }

    /// <summary>
    /// The audit itself must fail on an instrument that omits <c>game</c>, for both a
    /// synchronous and an observable instrument, and must not blame the instruments that
    /// carry it. Without this, a listener that never saw the bad measurement (wrong meter
    /// name, a missing type callback) would pass vacuously.
    /// </summary>
    [Fact]
    public void Audit_ReportsAnInstrumentThatOmitsGame()
    {
        string meterName = UniqueMeterName(nameof(Audit_ReportsAnInstrumentThatOmitsGame));
        using var audit = new MeterAudit(meterName, "cuvara", MapId);
        using var metrics = new GameMetrics(MapId, meterName);

        // A second Meter under the same name stands in for a future GameMetrics instrument
        // recorded with map_id only.
        using var rogue = new Meter(meterName);
        rogue.CreateCounter<long>("gameserver.rogue.counter")
            .Add(1, new KeyValuePair<string, object?>("map_id", MapId));
        rogue.CreateObservableGauge("gameserver.rogue.gauge", () => 1);

        metrics.RecordResyncRequested();
        audit.Observe();

        Assert.Contains(audit.Violations, v => v.StartsWith("gameserver.rogue.counter:", StringComparison.Ordinal));
        Assert.Contains(audit.Violations, v => v.StartsWith("gameserver.rogue.gauge:", StringComparison.Ordinal));
        Assert.DoesNotContain(audit.Violations, v => v.StartsWith("gameserver.resyncs:", StringComparison.Ordinal));
        Assert.DoesNotContain(audit.Violations, v => v.StartsWith("gameserver.players.online:", StringComparison.Ordinal));
    }
}
