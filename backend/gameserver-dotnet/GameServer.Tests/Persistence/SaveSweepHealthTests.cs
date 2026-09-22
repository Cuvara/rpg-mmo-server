using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.World;

namespace GameServer.Tests.Persistence;

/// <summary>
/// The save sweep must say so when it stops working.
///
/// <para>#402: <c>gameserver_player_saves_total</c> stood at 27 ok / 239 error — a 90%
/// failure rate — for 7.8 hours, and the only surface carrying it was a Prometheus counter
/// nothing scrapes on a dev box. The per-player <c>LogWarning</c> that already existed did
/// not help: it fires once per player per sweep at warning level, states no ratio, and is
/// therefore indistinguishable from routine noise. These tests pin the two surfaces added
/// in response — the readable counters behind <c>/status</c>, and an edge-triggered
/// <c>Error</c> line on the degraded transition.</para>
///
/// <para>Everything here runs in memory. There is no external dependency to gate on, so no
/// test in this file skips; a skip here would be a defect, not a missing database.</para>
/// </summary>
public class SaveSweepHealthTests
{
    // ── The readable counters behind /status ─────────────────────────────────

    /// <summary>
    /// The outcome of a save must be readable from the process itself, not only by
    /// scraping <c>/metrics</c>. A <see cref="System.Diagnostics.Metrics.Counter{T}"/> is
    /// write-only, which is the mechanical reason the ratio in #402 had nowhere to appear.
    /// </summary>
    [Fact]
    public async Task SaveOutcomesAreReadableWithoutAMetricsScrape()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("ok-1"));
        world.AddEntity(TestHelpers.CreatePlayer("ok-2"));
        world.AddEntity(TestHelpers.CreatePlayer("bad-1"));

        var metrics = new GameMetrics("map_01", UniqueMeter());
        var store = new SelectivelyFailingStore("bad-1");
        var saver = NewSaver(store, world, metrics);

        await saver.SaveAllAsync();

        Assert.Equal(2, metrics.PlayerSavesOk);
        Assert.Equal(1, metrics.PlayerSavesError);
        Assert.Equal(1d / 3d, metrics.PlayerSaveErrorRatio, 6);
    }

    /// <summary>
    /// A server that has attempted nothing has not failed at anything. Division by zero
    /// here would surface on <c>/status</c> as <c>NaN</c>, which serialises to invalid JSON
    /// and takes the whole endpoint down with it.
    /// </summary>
    [Fact]
    public void ErrorRatioIsZeroBeforeAnySaveIsAttempted()
    {
        var metrics = new GameMetrics("map_01", UniqueMeter());

        Assert.Equal(0, metrics.PlayerSavesOk);
        Assert.Equal(0, metrics.PlayerSavesError);
        Assert.Equal(0d, metrics.PlayerSaveErrorRatio);
    }

    // ── The log line ─────────────────────────────────────────────────────────

    /// <summary>
    /// Crossing the threshold raises exactly one <c>Error</c>, and staying there stays
    /// quiet until the re-log interval. Both halves matter: without the line nobody learns,
    /// and with a line per sweep the signal is buried by the same noise it was added to cut
    /// through.
    /// </summary>
    [Fact]
    public async Task CrossingTheThresholdLogsOnceAtErrorLevel()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        world.AddEntity(TestHelpers.CreatePlayer("p2"));

        var log = new CapturingLogger();
        var store = new SelectivelyFailingStore("p1", "p2");
        var saver = NewSaver(store, world, metrics: null, log);

        await saver.SaveAllAsync();
        await saver.SaveAllAsync();
        await saver.SaveAllAsync();

        Assert.True(saver.IsDegraded);

        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Single(errors);
        Assert.Contains("DEGRADED", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("2/2", errors[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure ratio below the threshold is not a degraded sweep. One player whose row is
    /// rejected while the store is otherwise healthy must not raise the alarm that means
    /// "persistence is down".
    /// </summary>
    [Fact]
    public async Task AMinorityOfFailuresDoesNotDegradeTheSweep()
    {
        using var world = new EcsWorld();
        for (int i = 0; i < 4; i++) world.AddEntity(TestHelpers.CreatePlayer($"p{i}"));

        var log = new CapturingLogger();
        var saver = NewSaver(new SelectivelyFailingStore("p0"), world, metrics: null, log);

        await saver.SaveAllAsync();

        Assert.False(saver.IsDegraded);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
    }

    /// <summary>
    /// Recovery is announced. A degraded line with no closing line leaves a reader unable to
    /// tell a server that healed from one still failing.
    /// </summary>
    [Fact]
    public async Task RecoveryIsLoggedAndClearsTheDegradedState()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));

        var log = new CapturingLogger();
        var store = new SelectivelyFailingStore("p1");
        var saver = NewSaver(store, world, metrics: null, log);

        await saver.SaveAllAsync();
        Assert.True(saver.IsDegraded);

        store.HealAll();
        await saver.SaveAllAsync();

        Assert.False(saver.IsDegraded);
        Assert.Contains(
            log.Entries,
            e => e.Level == LogLevel.Information
                 && e.Message.Contains("recovered", StringComparison.Ordinal));
    }

    /// <summary>
    /// A sweep with nobody online is not evidence that the store is healthy — it never
    /// touched the store. Clearing the degraded state on an empty sweep would silently
    /// retract a standing alarm every time the last player logged off, which on a dev box
    /// with one or two players is most of the time.
    /// </summary>
    [Fact]
    public async Task AnEmptySweepDoesNotClearAStandingDegradedState()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));

        var log = new CapturingLogger();
        var saver = NewSaver(new SelectivelyFailingStore("p1"), world, metrics: null, log);

        await saver.SaveAllAsync();
        Assert.True(saver.IsDegraded);

        world.RemoveEntity("p1");
        await saver.SaveAllAsync();

        Assert.True(saver.IsDegraded);
        Assert.DoesNotContain(
            log.Entries,
            e => e.Message.Contains("recovered", StringComparison.Ordinal));
    }

    /// <summary>
    /// A sustained outage restates itself. The condition that prompted this ran for 33
    /// minutes and counting; a single line at the start of it scrolls away, and its absence
    /// afterwards reads exactly like recovery.
    /// </summary>
    [Fact]
    public async Task ASustainedOutageIsRestatedPeriodically()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));

        var log = new CapturingLogger();
        var saver = NewSaver(new SelectivelyFailingStore("p1"), world, metrics: null, log);

        // 20 sweeps: the first raises the transition, the 20th hits the re-log interval.
        for (int i = 0; i < 20; i++) await saver.SaveAllAsync();

        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains("DEGRADED", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("still DEGRADED after 20 sweeps", errors[1].Message, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniqueMeter() => $"rpg.gameserver.test.{Guid.NewGuid():N}";

    private static AsyncSaver NewSaver(
        IPlayerStore store, EcsWorld world, GameMetrics? metrics, ILogger? logger = null)
        => new(
            store, world, "map_01", TimeSpan.FromSeconds(30),
            logger ?? new CapturingLogger(), metrics);

    /// <summary>
    /// Store that throws for a named set of player ids and succeeds for the rest, so a
    /// sweep's failure ratio can be set exactly.
    /// </summary>
    private sealed class SelectivelyFailingStore : IPlayerStore
    {
        private readonly HashSet<string> _failing;

        public SelectivelyFailingStore(params string[] failingIds)
            => _failing = new HashSet<string>(failingIds, StringComparer.Ordinal);

        public void HealAll() => _failing.Clear();

        public Task SavePlayerAsync(PlayerState state, CancellationToken ct)
            => _failing.Contains(state.UserId)
                ? throw new InvalidOperationException(
                    $"pgstore save player {state.UserId}: Name does not resolve")
                : Task.CompletedTask;

        public Task<PlayerState?> LoadPlayerAsync(string userId, CancellationToken ct)
            => Task.FromResult<PlayerState?>(null);
    }

    private sealed record Entry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
    }
}
