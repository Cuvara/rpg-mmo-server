using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Observability;

/// <summary>
/// Metric assertions driven by an in-memory <see cref="MetricReader"/>.
/// Each test uses a unique meter name so collections never cross-contaminate.
/// </summary>
public class GameMetricsTests
{
    private sealed class Harness : IDisposable
    {
        public GameMetrics Metrics { get; }
        public List<Metric> Exported { get; } = new();
        private readonly MeterProvider _provider;

        public Harness(string testName, string mapId = "map_01")
        {
            string meterName = $"rpg.gameserver.test.{testName}.{Guid.NewGuid():N}";
            Metrics = new GameMetrics(mapId, meterName);
            _provider = Sdk.CreateMeterProviderBuilder()
                .AddMeter(meterName)
                .AddInMemoryExporter(Exported)
                .Build()!;
        }

        /// <summary>Force a collection cycle and return the exported metrics.</summary>
        public IReadOnlyList<Metric> Collect()
        {
            Exported.Clear();
            _provider.ForceFlush();
            return Exported;
        }

        public void Dispose()
        {
            _provider.Dispose();
            Metrics.Dispose();
        }
    }

    private static Metric? Find(IReadOnlyList<Metric> metrics, string name)
        => metrics.FirstOrDefault(m => m.Name == name);

    private static long SumLong(Metric metric, params (string Key, string Value)[] requiredTags)
    {
        long total = 0;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            if (!HasTags(point.Tags, requiredTags)) continue;
            total += point.GetSumLong();
        }
        return total;
    }

    private static bool HasTags(ReadOnlyTagCollection tags, (string Key, string Value)[] required)
    {
        foreach (var (key, value) in required)
        {
            bool found = false;
            foreach (var tag in tags)
            {
                if (tag.Key == key && Equals(tag.Value?.ToString(), value)) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    // ── Tick loop ──

    [Fact]
    public void TickOnce_RecordsTickDurationHistogramPoint()
    {
        using var h = new Harness(nameof(TickOnce_RecordsTickDurationHistogramPoint));
        using var world = new GameWorld();
        var loop = new TickLoop(
            world,
            new InputHandler(world, NullLogger.Instance),
            new ConnectionManager(),
            GameConstants.DefaultTickRate,
            GameConstants.DefaultAoiRadius,
            NullLogger.Instance,
            h.Metrics);

        loop.TickOnce();

        var metric = Find(h.Collect(), GameMetrics.TickDurationInstrument);
        Assert.NotNull(metric);
        Assert.Equal(MetricType.Histogram, metric.MetricType);

        long count = 0;
        double sum = 0;
        string? mapId = null;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            count += point.GetHistogramCount();
            sum += point.GetHistogramSum();
            foreach (var tag in point.Tags)
            {
                if (tag.Key == "map_id") mapId = tag.Value?.ToString();
            }
        }

        Assert.Equal(1, count);
        Assert.True(sum >= 0, "Tick duration must be non-negative");
        Assert.Equal("map_01", mapId);
        Assert.Equal("s", metric.Unit);
    }

    [Fact]
    public void TickOnce_RecordsProcessedInputs()
    {
        using var h = new Harness(nameof(TickOnce_RecordsProcessedInputs));
        using var world = new GameWorld();
        var loop = new TickLoop(
            world,
            new InputHandler(world, NullLogger.Instance),
            new ConnectionManager(),
            GameConstants.DefaultTickRate,
            GameConstants.DefaultAoiRadius,
            NullLogger.Instance,
            h.Metrics);

        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        world.PushInput("p1", new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null));
        world.PushInput("p1", new InputData(tick: 2, moveX: 0f, moveY: 1f, attackTargetId: null));

        loop.TickOnce();

        var metric = Find(h.Collect(), "gameserver.tick.processed_inputs");
        Assert.NotNull(metric);
        Assert.Equal(2, SumLong(metric, ("map_id", "map_01")));
    }

    // ── Persistence ──

    [Fact]
    public async Task SaveAll_Success_RecordsStatusOk()
    {
        using var h = new Harness(nameof(SaveAll_Success_RecordsStatusOk));
        using var world = new GameWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));

        var saver = new AsyncSaver(
            new MemoryPlayerStore(), world, "map_01",
            TimeSpan.FromSeconds(30), NullLogger.Instance, h.Metrics);

        await saver.SaveAllAsync();

        var metric = Find(h.Collect(), "gameserver.player.saves");
        Assert.NotNull(metric);
        Assert.Equal(1, SumLong(metric, ("status", "ok")));
        Assert.Equal(0, SumLong(metric, ("status", "error")));
    }

    [Fact]
    public async Task SaveAll_Failure_RecordsStatusError()
    {
        using var h = new Harness(nameof(SaveAll_Failure_RecordsStatusError));
        using var world = new GameWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        world.AddEntity(TestHelpers.CreatePlayer("p2"));

        var saver = new AsyncSaver(
            new FailingPlayerStore(), world, "map_01",
            TimeSpan.FromSeconds(30), NullLogger.Instance, h.Metrics);

        await saver.SaveAllAsync();

        var metric = Find(h.Collect(), "gameserver.player.saves");
        Assert.NotNull(metric);
        Assert.Equal(2, SumLong(metric, ("status", "error")));
        Assert.Equal(0, SumLong(metric, ("status", "ok")));
    }

    private sealed class FailingPlayerStore : IPlayerStore
    {
        public Task SavePlayerAsync(PlayerState state, CancellationToken ct)
            => throw new InvalidOperationException("store unavailable");

        public Task<PlayerState?> LoadPlayerAsync(string userId, CancellationToken ct)
            => Task.FromResult<PlayerState?>(null);
    }

    // ── Events ──

    [Fact]
    public async Task PublishDeath_RecordsEventCounterWithType()
    {
        using var h = new Harness(nameof(PublishDeath_RecordsEventCounterWithType));
        var publisher = new EventPublisher(new NoopEventStream(), NullLogger.Instance, h.Metrics);

        await publisher.PublishDeathAsync("entity_killed",
            new DeathPayload("v1", "player", "k1", "map_01", "gs-1"));

        var metric = Find(h.Collect(), "gameserver.events.published");
        Assert.NotNull(metric);
        Assert.Equal(1, SumLong(metric, ("type", "entity_killed")));
    }

    // ── Gauges ──

    [Fact]
    public void PlayersOnlineGauge_TracksJoinAndLeave()
    {
        using var h = new Harness(nameof(PlayersOnlineGauge_TracksJoinAndLeave));

        h.Metrics.PlayerJoined();
        h.Metrics.PlayerJoined();
        h.Metrics.PlayerLeft();

        var metric = Find(h.Collect(), "gameserver.players.online");
        Assert.NotNull(metric);

        long value = 0;
        string? mapId = null;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            value = point.GetGaugeLastValueLong();
            foreach (var tag in point.Tags)
            {
                if (tag.Key == "map_id") mapId = tag.Value?.ToString();
            }
        }

        Assert.Equal(1, value);
        Assert.Equal("map_01", mapId);
        Assert.Equal(1, h.Metrics.PlayersOnline);
    }

    [Fact]
    public void PlayerLeft_NeverGoesNegative()
    {
        using var h = new Harness(nameof(PlayerLeft_NeverGoesNegative));

        h.Metrics.PlayerLeft();
        h.Metrics.PlayerLeft();

        Assert.Equal(0, h.Metrics.PlayersOnline);
    }

    [Fact]
    public void EntitiesGauge_ReadsWorldEntityCount()
    {
        using var h = new Harness(nameof(EntitiesGauge_ReadsWorldEntityCount));
        using var world = new GameWorld();
        h.Metrics.SetEntityCountProvider(() => world.EntityCount);

        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        world.AddEntity(TestHelpers.CreateMob("m1", 1, 1));

        var metric = Find(h.Collect(), "gameserver.entities");
        Assert.NotNull(metric);

        long value = 0;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            value = point.GetGaugeLastValueLong();
        }

        Assert.Equal(2, value);
    }

    /// <summary>
    /// The resync counter is the only field-visible signal that entity-id
    /// interning has gone wrong, so it has to exist and be labelled like every
    /// other server metric.
    /// </summary>
    [Fact]
    public void ResyncsRequested_CounterExistsAfterRecording()
    {
        using var h = new Harness(nameof(ResyncsRequested_CounterExistsAfterRecording));

        h.Metrics.RecordResyncRequested();
        h.Metrics.RecordResyncRequested();

        var metric = Find(h.Collect(), "gameserver.resyncs");
        Assert.NotNull(metric);
        Assert.Equal(2, SumLong(metric, ("map_id", "map_01")));
    }

    /// <summary>
    /// A server nobody resynced against must report nothing rather than a zero
    /// that looks like a reading. The counter only appears once something has
    /// happened, which is standard counter behaviour and worth pinning: a
    /// permanently-absent series would read the same as a healthy one.
    /// </summary>
    [Fact]
    public void ResyncsRequested_AbsentUntilSomethingHappens()
    {
        using var h = new Harness(nameof(ResyncsRequested_AbsentUntilSomethingHappens));

        h.Metrics.RecordSnapshotsSent(1); // unrelated activity
        Assert.Null(Find(h.Collect(), "gameserver.resyncs"));
    }

    [Fact]
    public void SnapshotsSent_CounterExistsAfterRecording()
    {
        using var h = new Harness(nameof(SnapshotsSent_CounterExistsAfterRecording));

        h.Metrics.RecordSnapshotsSent(3);

        var metric = Find(h.Collect(), "gameserver.snapshots.sent");
        Assert.NotNull(metric);
        Assert.Equal(3, SumLong(metric, ("map_id", "map_01")));
    }
}
