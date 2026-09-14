using System.Threading.Channels;
using GameServer.Observability;
using GameServer.Registry;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace GameServer.Events;

/// <summary>
/// Redis Streams-backed <see cref="IEventStream"/> — the publisher half of ADR-5.
///
/// <para><b>The Go gateway's consumer is the contract</b>
/// (<c>backend/shared/storage/redisstore/stream.go</c>): one <c>XADD</c> per event to
/// <c>events:{stream}</c> (the logical stream name <c>"game"</c> becomes the key
/// <c>events:game</c>, mirroring Go's <c>streamKey()</c> over
/// <c>constants.EventStreamPrefix</c>), exactly two entry fields — <c>type</c> and
/// <c>payload</c> — and <c>MAXLEN ~ 30000</c> trimming on every publish. The trim bound
/// exists because this Redis runs <c>maxmemory-policy noeviction</c> (ADR-4): an untrimmed
/// stream is the one unbounded consumer of the instance's memory budget, and the bound
/// belongs at the publisher because the publisher is the only party that runs on every
/// write (#202). 30 000 is Go's <c>DefaultStreamMaxLen</c> — a ~10-minute consumer-lag
/// budget at the planning rate of 50 events/s, not a memory figure; the two sides MUST
/// trim to the same length or the shorter one silently wins.</para>
///
/// <para><b>Never blocks and never throws into the caller.</b>
/// <see cref="PublishAsync"/> is one bounded-channel write — the same shape #249 gave
/// <see cref="EventPublisher"/>, for the same reason: the callers upstream sit on the
/// tick thread's drain path, and a Redis backlog or reconnect must not stall them. A
/// background drain task performs the actual <c>XADD</c>; connection recovery is the
/// multiplexer's job (<c>AbortOnConnectFail=false</c>, shared with
/// <see cref="RedisServerRegistry"/>), and on top of that each event gets a short
/// retry-with-backoff before it is dropped and counted. When the queue itself fills —
/// Redis down long enough for 4096 events to pile up — the OLDEST event is dropped and
/// counted (<see cref="Dropped"/>): the stream is telemetry/feed, the authoritative
/// reward path is the kill batcher, and a ten-minute-old kill event has already lost the
/// timeliness that made it worth delivering (same argument that sizes the MAXLEN).</para>
/// </summary>
public sealed class RedisEventStream : IEventStream, IAsyncDisposable
{
    /// <summary>Go's <c>constants.EventStreamPrefix</c> — key = prefix + logical stream name.</summary>
    public const string KeyPrefix = "events:";

    /// <summary>Entry field for the event type — Go's <c>streamFieldType</c>.</summary>
    public const string FieldType = "type";

    /// <summary>Entry field for the serialized payload — Go's <c>streamFieldPayload</c>.</summary>
    public const string FieldPayload = "payload";

    /// <summary>
    /// Retained stream length (<c>XADD MAXLEN ~ N</c>). MUST equal Go's
    /// <c>redisstore.DefaultStreamMaxLen</c> — both sides publish into the same stream,
    /// so the smaller bound is the effective one.
    /// </summary>
    public const int DefaultMaxLen = 30_000;

    /// <summary>Queue bound — matches <see cref="EventPublisher"/>'s death queue (#249).</summary>
    public const int DefaultQueueCapacity = 4096;

    // Two retries (three attempts) with short backoff. Deliberately short: the
    // multiplexer already reconnects on its own, so these only bridge a blip; a real
    // outage is the drop-oldest queue's problem, not a retry loop's.
    private static readonly TimeSpan[] DefaultRetryDelays =
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    private readonly IConnectionMultiplexer? _mux;
    private readonly bool _ownsMux;
    private readonly Func<string, GameEvent, Task> _sink;
    private readonly TimeSpan[] _retryDelays;
    private readonly int _maxLen;
    private readonly ILogger _logger;
    private readonly GameMetrics? _metrics;

    private readonly Channel<(string Stream, GameEvent Event)> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drain;

    private long _published;
    private long _dropped;
    private long _publishFailures;
    private long _consecutiveFailures;

    /// <summary>Events successfully XADDed since construction.</summary>
    public long Published => Interlocked.Read(ref _published);

    /// <summary>
    /// Events dropped because the bounded queue was full (oldest-first) or the stream was
    /// already shut down. Surfaced via <c>/status</c> and
    /// <c>gameserver_events_dropped_total</c> — mirrors Go's exported-counter pattern
    /// (<c>GroupLosses</c>/<c>DeadLetters</c>): a metric for operators, an assertion
    /// handle for tests.
    /// </summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Events dropped after exhausting the XADD retry budget. Each increment is one event
    /// that will never reach the gateway's relay.
    /// </summary>
    public long PublishFailures => Interlocked.Read(ref _publishFailures);

    /// <summary>
    /// Connect to Redis and return a publishing stream over that connection.
    /// </summary>
    /// <remarks>
    /// Reuses <see cref="RedisServerRegistry.BuildOptions"/>, so the connection semantics
    /// are the registry's: <c>AbortOnConnectFail=false</c> lets the server boot while
    /// Redis is down and lets the multiplexer reconnect by itself. The stream owns a
    /// multiplexer SEPARATE from the registry's on purpose — the two have independent
    /// lifetimes (the registry deregisters during host drain, the event stream flushes
    /// after it), and sharing would couple their shutdown ordering for the price of one
    /// idle connection.
    /// </remarks>
    public static async Task<RedisEventStream> ConnectAsync(
        string addr, string? password, ILogger logger, GameMetrics? metrics = null,
        int maxLen = DefaultMaxLen)
    {
        var options = RedisServerRegistry.BuildOptions(addr, password);
        var mux = await ConnectionMultiplexer.ConnectAsync(options);
        return new RedisEventStream(mux, logger, metrics, maxLen, ownsMux: true);
    }

    /// <summary>Wrap an existing multiplexer (shared pool, tests).</summary>
    public RedisEventStream(
        IConnectionMultiplexer mux, ILogger logger, GameMetrics? metrics = null,
        int maxLen = DefaultMaxLen, bool ownsMux = false)
        : this(mux, sink: null, logger, metrics, maxLen, ownsMux,
               DefaultQueueCapacity, retryDelays: null)
    {
    }

    /// <summary>
    /// Test seam: everything except the Redis call itself — queue bounding, drop-oldest
    /// counting, retry/backoff, never-throw — exercised against an injected sink.
    /// </summary>
    internal RedisEventStream(
        Func<string, GameEvent, Task> sink, ILogger logger, GameMetrics? metrics = null,
        int queueCapacity = DefaultQueueCapacity, TimeSpan[]? retryDelays = null)
        : this(mux: null, sink, logger, metrics, DefaultMaxLen, ownsMux: false,
               queueCapacity, retryDelays)
    {
    }

    private RedisEventStream(
        IConnectionMultiplexer? mux, Func<string, GameEvent, Task>? sink, ILogger logger,
        GameMetrics? metrics, int maxLen, bool ownsMux, int queueCapacity,
        TimeSpan[]? retryDelays)
    {
        _mux = mux;
        _ownsMux = ownsMux;
        _sink = sink ?? XAddAsync;
        _logger = logger;
        _metrics = metrics;
        _maxLen = maxLen > 0 ? maxLen : DefaultMaxLen;
        _retryDelays = retryDelays ?? DefaultRetryDelays;

        // DropOldest + the itemDropped callback: the drop is COUNTED, never silent.
        // Oldest-first for the same reason EventPublisher chose it — under a backlog the
        // newest events are the ones still worth delivering.
        _queue = Channel.CreateBounded<(string, GameEvent)>(
            new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            _ => OnDropped());

        _drain = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Queue an event for publication. One bounded-channel write: never blocks, never
    /// throws, never touches Redis on the caller's thread. An event offered after
    /// shutdown is dropped and counted.
    /// </summary>
    public Task PublishAsync(string stream, GameEvent evt, CancellationToken ct)
    {
        if (!_queue.Writer.TryWrite((stream, evt)))
        {
            // Writer completed (shutdown). Capacity overflows do NOT land here — with
            // DropOldest the write succeeds and the displaced item hits the callback.
            OnDropped();
        }
        return Task.CompletedTask;
    }

    private void OnDropped()
    {
        long dropped = Interlocked.Increment(ref _dropped);
        _metrics?.RecordEventDropped();
        // First drop is the alert; after that one line per 1000 keeps a dead Redis from
        // turning the log into the backlog it is reporting on.
        if (dropped == 1 || dropped % 1000 == 0)
        {
            _logger.LogWarning(
                "Event stream queue full or closed: {Dropped} event(s) dropped oldest-first " +
                "so far (queue bound {Capacity})", dropped, DefaultQueueCapacity);
        }
    }

    private async Task DrainAsync()
    {
        await foreach (var (stream, evt) in _queue.Reader.ReadAllAsync())
        {
            await PublishWithRetryAsync(stream, evt);
        }
    }

    private async Task PublishWithRetryAsync(string stream, GameEvent evt)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await _sink(stream, evt);
                Interlocked.Increment(ref _published);
                long streak = Interlocked.Exchange(ref _consecutiveFailures, 0);
                if (streak > 0)
                {
                    _logger.LogInformation(
                        "Event stream publishing recovered after {Failures} consecutive failure(s)",
                        streak);
                }
                return;
            }
            catch (Exception) when (attempt < _retryDelays.Length && !_cts.IsCancellationRequested)
            {
                // Bridge a blip; the real reconnect is the multiplexer's.
                try
                {
                    await Task.Delay(_retryDelays[attempt], _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down: the next loop iteration takes the final-failure
                    // branch instead of another network attempt.
                }
            }
            catch (Exception ex)
            {
                long failures = Interlocked.Increment(ref _publishFailures);
                Interlocked.Increment(ref _consecutiveFailures);
                _metrics?.RecordEventPublishFailure();
                if (failures == 1 || failures % 1000 == 0)
                {
                    _logger.LogWarning(ex,
                        "Failed to publish event {Type} to redis stream {Key} after " +
                        "{Attempts} attempt(s); dropped ({Failures} publish failure(s) so far)",
                        evt.Type, KeyPrefix + stream, attempt + 1, failures);
                }
                return;
            }
        }
    }

    /// <summary>
    /// The real sink: <c>XADD events:{stream} MAXLEN ~ {maxLen} type … payload …</c>.
    /// Approximate trimming, exactly like Go: Redis trims whole radix-tree nodes, so the
    /// publish pays for cheap batched deletion instead of entry-precise enforcement of a
    /// number that is itself a rounded-off lag budget.
    /// </summary>
    private Task XAddAsync(string stream, GameEvent evt)
    {
        return _mux!.GetDatabase().StreamAddAsync(
            KeyPrefix + stream,
            [new NameValueEntry(FieldType, evt.Type), new NameValueEntry(FieldPayload, evt.Payload)],
            maxLength: _maxLen,
            useApproximateMaxLength: true);
    }

    /// <summary>
    /// Completes the queue, gives the drain a bounded window to flush what is left, then
    /// cuts any in-flight backoff and closes the connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await Task.WhenAny(_drain, Task.Delay(TimeSpan.FromSeconds(2)));
        _cts.Cancel();
        await Task.WhenAny(_drain, Task.Delay(TimeSpan.FromMilliseconds(500)));

        // Dispose the CTS only once the drain has provably finished with it. Cancel()
        // resumes registered continuations inline, and this codebase has hit the
        // Cancel/Dispose teardown race three times (once as a live-lock) — a still-running
        // drain touching a disposed CTS is that same defect; leaking one CTS is not.
        if (_drain.IsCompleted)
        {
            _cts.Dispose();
        }

        if (_ownsMux && _mux is not null)
        {
            await _mux.CloseAsync();
            _mux.Dispose();
        }
    }
}
