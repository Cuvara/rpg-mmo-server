using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GameServer.Nakama;

/// <summary>
/// Coalesces kill rewards per killer and flushes them to Nakama on an interval,
/// one <c>reward_kills</c> call per killer per flush.
///
/// <para><b>Why this exists (#233).</b> The death callback used to fire two
/// fire-and-forget HTTP RPCs per mob kill — 2 requests and 2 meta-Postgres
/// transactions each. At 200 players on the grindy end that is ~133 commits/s,
/// the first thing to saturate a shared small-VPS Postgres; and because the
/// tasks were unbounded with a 100s default timeout, a stalled Nakama
/// accumulated thousands of in-flight tasks. Both underlying operations are
/// increments, so summing per killer is semantically identical.</para>
///
/// <para><b>Memory is bounded by construction.</b> The pending state is one
/// <c>long</c> per killer — not a queue of kill events — so a Nakama outage of
/// any length holds at most (players online) map entries, each an ever-larger
/// count that is granted in one call when Nakama returns.</para>
///
/// <para><b>Retry policy</b> follows <see cref="NakamaClient.RewardKillsAsync"/>'s
/// outcome contract: <c>NotGranted</c> re-queues the kills (Nakama's error
/// contract guarantees nothing was granted); <c>Unknown</c> (timeout) drops the
/// batch with a loud log, because retrying an unknown outcome is the
/// double-gold path and ADR-6 tolerates bounded loss but not double grants.</para>
/// </summary>
public sealed class KillRewardBatcher : IAsyncDisposable
{
    /// <summary>
    /// Default flush cadence. Short enough that a player sees their gold and
    /// leaderboard rank move within a breath of the kill; long enough that a
    /// grinding player's kills coalesce (at one kill per 3s, every flush
    /// carries work).
    /// </summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(3);

    private readonly NakamaClient _client;
    private readonly ILogger _logger;
    private readonly string _mapId;
    private readonly TimeSpan _interval;
    private readonly ConcurrentDictionary<string, long> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private long _dropped;

    /// <summary>Kills dropped because Nakama's answer never arrived (outcome unknown). Diagnostic.</summary>
    public long DroppedKills => Interlocked.Read(ref _dropped);

    public KillRewardBatcher(NakamaClient client, string mapId, ILogger logger, TimeSpan? flushInterval = null)
    {
        _client = client;
        _mapId = mapId;
        _logger = logger;
        _interval = flushInterval ?? DefaultFlushInterval;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>
    /// Record one kill for <paramref name="killerId"/>. Called from the death
    /// callback on the tick thread: one dictionary increment, no I/O, no task.
    /// </summary>
    public void RecordKill(string killerId) =>
        _pending.AddOrUpdate(killerId, 1, static (_, n) => n + 1);

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: DisposeAsync runs the final flush.
        }
    }

    /// <summary>
    /// Send every pending killer's accumulated count. Sequential on purpose:
    /// the point of this class is to stop hammering Nakama, and at one call
    /// per online killer per interval there is nothing worth parallelising.
    /// Public so tests (and a drain path) can flush without waiting out the timer.
    /// </summary>
    public async Task FlushAsync()
    {
        foreach (string killerId in _pending.Keys)
        {
            if (!_pending.TryRemove(killerId, out long kills) || kills <= 0)
            {
                continue;
            }

            var outcome = await _client.RewardKillsAsync(
                killerId, kills, _mapId, Guid.NewGuid().ToString("N"));

            switch (outcome)
            {
                case KillRewardOutcome.Granted:
                    break;

                case KillRewardOutcome.NotGranted:
                    // Nothing was granted — put the kills back so the next flush
                    // retries. AddOrUpdate, not an overwrite: kills recorded
                    // since the TryRemove above must not be lost.
                    _pending.AddOrUpdate(killerId, kills, (_, n) => n + kills);
                    break;

                case KillRewardOutcome.Unknown:
                    Interlocked.Add(ref _dropped, kills);
                    _logger.LogError(
                        "Dropped {Kills} kill reward(s) for {KillerId}: Nakama's answer never " +
                        "arrived and a retry could double-grant. Total dropped: {Total}",
                        kills, killerId, DroppedKills);
                    break;
            }
        }
    }

    private int _disposed;

    /// <summary>Stop the loop and flush what is still pending. Idempotent; later calls only flush.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cts.Cancel();
            try { await _loop; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
        await FlushAsync();
    }
}
