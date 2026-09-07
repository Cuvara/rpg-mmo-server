using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GameServer.Nakama;

/// <summary>
/// Coalesces kill rewards per killer and flushes them to Nakama on an interval,
/// one <c>reward_kills</c> call per batch.
///
/// <para><b>Why this exists (#233).</b> The death callback used to fire two
/// fire-and-forget HTTP RPCs per mob kill — 2 requests and 2 meta-Postgres
/// transactions each. At 200 players on the grindy end that is ~133 commits/s,
/// the first thing to saturate a shared small-VPS Postgres; and because the
/// tasks were unbounded with a 100s default timeout, a stalled Nakama
/// accumulated thousands of in-flight tasks. Both underlying operations are
/// increments, so summing per killer is semantically identical.</para>
///
/// <para><b>Exactly-once (audit F06).</b> Each batch gets ONE id when it is cut
/// from the pending count and keeps it for every retry. Nakama files a receipt
/// under that id in the same transaction as the wallet grant, so a resend is
/// replayed from the receipt: gold is granted at most once per id no matter how
/// many times the batch is sent. That is what makes every failure retryable —
/// including a timeout, whose outcome the sender cannot know — instead of the
/// old "drop on Unknown" policy that traded lost gold for the absence of
/// double gold. Splitting is the only operation that mints new ids, and it only
/// happens to batches Nakama has just refused before any grant.</para>
///
/// <para><b>Splitting (audit F07).</b> Nakama caps a batch at
/// <see cref="DefaultMaxKillsPerBatch"/> kills. A killer whose backlog grew past
/// that during an outage is cut into several batches, each under the cap with
/// its own id, so the backlog is always sendable once Nakama is back.</para>
///
/// <para><b>Memory.</b> Pending state is one <c>long</c> per killer with unsent
/// kills plus, per killer with un-acknowledged batches, ⌈backlog / cap⌉ small
/// records. During an outage that is bounded by the number of distinct killers
/// seen since it began — not by the current online count — times their backlog
/// over the cap. Nothing here is durable: kills recorded but not yet
/// acknowledged by Nakama are lost if the process dies (see
/// <c>docs/DESIGN.md</c>, "Kill rewards are exactly-once per batch id").</para>
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

    /// <summary>Nakama's per-batch cap (<c>economy.MaxKillsPerBatch</c>). Larger backlogs are split.</summary>
    public const int DefaultMaxKillsPerBatch = 1000;

    /// <summary>Longest wait between retries of one batch; backoff doubles from the flush interval up to this.</summary>
    public static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromSeconds(60);

    /// <summary>One cut batch: stable id, its kills, and when it may next be sent.</summary>
    private sealed class PendingBatch
    {
        public required string Id { get; init; }
        public required long Kills { get; init; }
        public int Attempts;
        public DateTimeOffset NotBefore;
    }

    private readonly NakamaClient _client;
    private readonly ILogger _logger;
    private readonly string _mapId;
    private readonly TimeSpan _interval;
    private readonly int _maxKillsPerBatch;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, long> _pending = new();
    private readonly Dictionary<string, List<PendingBatch>> _batches = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private long _requeued;

    /// <summary>Batches re-queued after a non-Granted answer (any kind). Diagnostic.</summary>
    public long RequeuedBatches => Interlocked.Read(ref _requeued);

    /// <summary>Kills recorded or cut into batches that Nakama has not acknowledged as granted. Diagnostic.</summary>
    public long PendingKills
    {
        get
        {
            long n = 0;
            foreach (long v in _pending.Values) n += v;
            lock (_batches)
            {
                foreach (var list in _batches.Values)
                    foreach (var b in list) n += b.Kills;
            }
            return n;
        }
    }

    public KillRewardBatcher(NakamaClient client, string mapId, ILogger logger, TimeSpan? flushInterval = null)
        : this(client, mapId, logger, flushInterval, maxKillsPerBatch: DefaultMaxKillsPerBatch, clock: null) { }

    /// <param name="maxKillsPerBatch">Test seam: the split threshold. Production uses Nakama's cap.</param>
    /// <param name="clock">Test seam: drives retry backoff. Null uses <see cref="TimeProvider.System"/>.</param>
    public KillRewardBatcher(NakamaClient client, string mapId, ILogger logger, TimeSpan? flushInterval,
        int maxKillsPerBatch, TimeProvider? clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKillsPerBatch, 1);
        _client = client;
        _mapId = mapId;
        _logger = logger;
        _interval = flushInterval ?? DefaultFlushInterval;
        _maxKillsPerBatch = maxKillsPerBatch;
        _clock = clock ?? TimeProvider.System;
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
    /// Cut every pending count into batches and send every batch that is due.
    /// Sequential on purpose: the point of this class is to stop hammering
    /// Nakama. Public so tests (and a drain path) can flush without waiting out
    /// the timer; concurrent calls serialise.
    /// </summary>
    public async Task FlushAsync()
    {
        await _flushGate.WaitAsync();
        try
        {
            CutPendingIntoBatches();
            await SendDueBatchesAsync();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>
    /// Move unsent counts into batches of at most <see cref="_maxKillsPerBatch"/>,
    /// each with a fresh, from-now-on stable id. Kills recorded after the
    /// TryRemove land in <see cref="_pending"/> again and form the next batch;
    /// they are never merged into a batch that already has an id, because that
    /// id may already be filed at Nakama with the smaller count.
    /// </summary>
    private void CutPendingIntoBatches()
    {
        foreach (string killerId in _pending.Keys)
        {
            if (!_pending.TryRemove(killerId, out long kills) || kills <= 0)
            {
                continue;
            }
            lock (_batches)
            {
                var list = ListFor(killerId);
                for (long remaining = kills; remaining > 0; remaining -= _maxKillsPerBatch)
                {
                    list.Add(NewBatch(Math.Min(remaining, _maxKillsPerBatch)));
                }
            }
        }
    }

    private async Task SendDueBatchesAsync()
    {
        List<string> killers;
        lock (_batches) killers = new List<string>(_batches.Keys);

        foreach (string killerId in killers)
        {
            while (true)
            {
                PendingBatch? batch;
                lock (_batches)
                {
                    batch = FirstDue(killerId);
                }
                if (batch is null)
                {
                    break; // nothing due for this killer
                }

                var outcome = await _client.RewardKillsAsync(killerId, batch.Kills, _mapId, batch.Id);
                lock (_batches)
                {
                    var list = ListFor(killerId);
                    switch (outcome)
                    {
                        case KillRewardOutcome.Granted:
                            list.Remove(batch);
                            break;

                        case KillRewardOutcome.TooLarge:
                            // Refused before any grant, so the id is unfiled and the halves
                            // may take new ids. Only reachable if Nakama's cap is below ours.
                            list.Remove(batch);
                            long half = Math.Max(1, batch.Kills / 2);
                            list.Add(NewBatch(half));
                            if (batch.Kills - half > 0) list.Add(NewBatch(batch.Kills - half));
                            _logger.LogWarning("Batch {BatchId} ({Kills} kills) for {KillerId} exceeds Nakama's cap; split",
                                batch.Id, batch.Kills, killerId);
                            break;

                        case KillRewardOutcome.Partial:
                        case KillRewardOutcome.NotGranted:
                        case KillRewardOutcome.Unknown:
                            // Same id next time: the receipt makes the resend safe. Back off
                            // this killer's whole queue — Nakama is unwell or this player's
                            // wallet is; either way the next batch will not fare better now.
                            Backoff(list, batch);
                            Interlocked.Increment(ref _requeued);
                            break;
                    }
                    if (list.Count == 0) _batches.Remove(killerId);
                }
                if (outcome is KillRewardOutcome.Partial or KillRewardOutcome.NotGranted or KillRewardOutcome.Unknown)
                {
                    break; // this killer is backed off; move on
                }
            }
        }
    }

    private List<PendingBatch> ListFor(string killerId)
    {
        if (!_batches.TryGetValue(killerId, out var list))
        {
            list = new List<PendingBatch>();
            _batches[killerId] = list;
        }
        return list;
    }

    private PendingBatch NewBatch(long kills) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kills = kills,
        NotBefore = DateTimeOffset.MinValue,
    };

    private PendingBatch? FirstDue(string killerId)
    {
        if (!_batches.TryGetValue(killerId, out var list)) return null;
        var now = _clock.GetUtcNow();
        foreach (var b in list)
        {
            if (b.NotBefore <= now) return b;
        }
        return null;
    }

    private void Backoff(List<PendingBatch> list, PendingBatch failed)
    {
        failed.Attempts++;
        // interval, 2·interval, 4·interval … capped. Attempt 1 waits one interval,
        // i.e. the very next flush, so a blip costs nothing extra.
        double factor = Math.Pow(2, Math.Min(failed.Attempts - 1, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(_interval.Ticks * factor, MaxRetryBackoff.Ticks));
        var notBefore = _clock.GetUtcNow() + delay;
        foreach (var b in list)
        {
            if (b.NotBefore < notBefore) b.NotBefore = notBefore;
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
