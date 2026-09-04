using GameServer.Registry;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace GameServer.Events;

/// <summary>
/// Consumer half of the duplicate-login kick (ADR-5 Streams, never Pub/Sub):
/// reads <c>session_superseded</c> events from the shared <c>events:kick</c>
/// stream and hands the ones addressed to THIS server to a handler that closes
/// the superseded connection.
///
/// <para><b>Stream shape — one shared stream, one consumer group per server.</b>
/// Every game server needs to see every kick event (the stream is a broadcast;
/// each server keeps only the events whose <c>server_id</c> names it), so each
/// server joins with its OWN group (<c>gs:{server_id}</c>) rather than sharing
/// one. The alternative — a stream key per server — was rejected because server
/// ids churn (dungeon instances, pod restarts) and this Redis runs
/// <c>noeviction</c> (ADR-4): per-server keys would accumulate without bound,
/// while one shared stream stays trimmed by the publisher's <c>MAXLEN ~</c>.
/// A server that is down and never ACKs strands only its own group's PEL,
/// which stops growing the moment the process stops reading; the entries
/// themselves are reclaimed by the trim.</para>
///
/// <para><b>The group starts at <c>$</c> and is destroyed on graceful dispose,
/// deliberately.</b> Kick events target live in-process connections and mean
/// nothing across a restart: a server that was down while an event was
/// published has no connection for it to kick, so catching up on missed events
/// would be pure no-op work. Starting at <c>$</c> skips history on first boot;
/// destroying the group on graceful shutdown keeps dead server ids from
/// leaving group metadata behind forever (the noeviction argument again). A
/// CRASHED server leaves its group, and the next boot with the same id drains
/// the stale PEL first — every stale entry is a no-op because the connections
/// it named died with the old process. That is the idempotency the jti guard
/// provides, exercised rather than assumed.</para>
///
/// <para><b>ACK always, even for malformed or foreign entries.</b> An entry
/// this server will never act on (another server's kick, an unknown type, an
/// unparseable payload) must not sit in the PEL as fake backlog; malformed
/// ones are additionally counted (<see cref="Malformed"/>) and logged. The
/// handler runs BEFORE the ACK, so a crash mid-kick redelivers — and the jti
/// guard makes the redelivery safe.</para>
///
/// <para>StackExchange.Redis multiplexes and cannot block on XREADGROUP, so
/// this polls: one <c>XREADGROUP COUNT 16</c> per <see cref="_pollInterval"/>
/// (default 250ms — a kick landing within a quarter second is far below what a
/// player perceives). Connection recovery is the multiplexer's job
/// (<c>AbortOnConnectFail=false</c>, same options as the registry); read
/// errors log-and-continue on the next tick.</para>
/// </summary>
public sealed class RedisKickConsumer : IAsyncDisposable
{
    /// <summary>Concrete Redis key — Go's <c>streamKey(constants.KickEventStream)</c>.</summary>
    public const string StreamKey = RedisEventStream.KeyPrefix + KickEvents.Stream;

    /// <summary>Consumer-group name prefix; the full group is <c>gs:{server_id}</c>.</summary>
    public const string GroupPrefix = "gs:";

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);
    private const int ReadBatch = 16;

    private readonly IConnectionMultiplexer _mux;
    private readonly bool _ownsMux;
    private readonly string _serverId;
    private readonly string _group;
    private readonly Func<SessionSupersededPayload, Task> _handler;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private long _consumed;
    private long _malformed;

    /// <summary>Entries read from the stream (own and foreign) since construction.</summary>
    public long Consumed => Interlocked.Read(ref _consumed);

    /// <summary>Entries ACKed unhandled because the payload would not parse.
    /// Every increment is a contract violation worth looking at.</summary>
    public long Malformed => Interlocked.Read(ref _malformed);

    /// <summary>
    /// Connect to Redis and return a started consumer. Same connection semantics
    /// as <see cref="RedisEventStream.ConnectAsync"/> (and its own multiplexer for
    /// the same lifetime-independence reason).
    /// </summary>
    public static async Task<RedisKickConsumer> ConnectAsync(
        string addr, string? password, string serverId,
        Func<SessionSupersededPayload, Task> handler, ILogger logger,
        TimeSpan? pollInterval = null)
    {
        var options = RedisServerRegistry.BuildOptions(addr, password);
        // RESP2, pinned. SE.Redis v3 negotiates RESP3 (HELLO 3) by default, and this
        // consumer's CORRECTNESS rides on XREADGROUP reply parsing — the one command
        // family whose reply shape differs between the protocols. miniredis (the
        // in-process Redis the E2E suite runs against) accepts HELLO 3 but answers
        // XREADGROUP in the RESP2 array shape, which SE.Redis under RESP3 silently
        // parses as an EMPTY result: the consumer polls forever and every kick is
        // lost, with zero errors anywhere. RESP3 buys this connection nothing (no
        // client-side caching, no push messages), so the portable reply shape wins.
        options.Protocol = RedisProtocol.Resp2;
        var mux = await ConnectionMultiplexer.ConnectAsync(options);
        var consumer = new RedisKickConsumer(mux, serverId, handler, logger, ownsMux: true, pollInterval);
        await consumer.StartAsync();
        return consumer;
    }

    /// <summary>Wrap an existing multiplexer (shared pool, tests). Call
    /// <see cref="StartAsync"/> to begin consuming.</summary>
    public RedisKickConsumer(
        IConnectionMultiplexer mux, string serverId,
        Func<SessionSupersededPayload, Task> handler, ILogger logger,
        bool ownsMux = false, TimeSpan? pollInterval = null)
    {
        _mux = mux;
        _ownsMux = ownsMux;
        _serverId = serverId;
        _group = GroupPrefix + serverId;
        _handler = handler;
        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>
    /// Create the consumer group (at <c>$</c>, MKSTREAM) and start the poll loop.
    /// Throws only when the group cannot be created at all — a consumer that
    /// cannot subscribe must fail loudly at wiring time, not run dark.
    /// </summary>
    public async Task StartAsync()
    {
        var db = _mux.GetDatabase();
        try
        {
            await db.StreamCreateConsumerGroupAsync(
                StreamKey, _group, StreamPosition.NewMessages, createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Crash-restart with the same server id: the group survives, and the
            // first read below drains its stale PEL (all no-ops, see class doc).
        }
        _loop = Task.Run(ConsumeLoopAsync);
        _logger.LogInformation(
            "Kick consumer started: stream {Stream}, group {Group}", StreamKey, _group);
    }

    private async Task ConsumeLoopAsync()
    {
        var db = _mux.GetDatabase();

        // First pass: our own pending entries ("0"), left by a crash mid-batch.
        // Then new entries (">") forever.
        bool drainedPel = false;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                RedisValue position = drainedPel ? StreamPosition.NewMessages : "0";
                var entries = await db.StreamReadGroupAsync(
                    StreamKey, _group, _serverId, position, ReadBatch);

                if (!drainedPel && entries.Length == 0)
                {
                    drainedPel = true;
                    continue;
                }

                if (entries.Length == 0)
                {
                    await Task.Delay(_pollInterval, _cts.Token);
                    continue;
                }

                var acks = new RedisValue[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    acks[i] = entries[i].Id;
                    await HandleEntryAsync(entries[i]);
                }
                await db.StreamAcknowledgeAsync(StreamKey, _group, acks);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                _logger.LogWarning(ex,
                    "Kick consumer read failed on {Stream}; retrying in {Interval}",
                    StreamKey, _pollInterval);
                try { await Task.Delay(_pollInterval, _cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task HandleEntryAsync(StreamEntry entry)
    {
        Interlocked.Increment(ref _consumed);

        string? type = null;
        byte[]? payload = null;
        foreach (var field in entry.Values)
        {
            if (field.Name == RedisEventStream.FieldType) type = field.Value;
            else if (field.Name == RedisEventStream.FieldPayload) payload = field.Value;
        }

        if (type != KickEvents.SessionSuperseded)
        {
            // Not ours to act on; ACKed by the caller so it never reads as backlog.
            return;
        }
        var parsed = payload is null ? null : KickEvents.TryParse(payload);
        if (parsed is null)
        {
            long malformed = Interlocked.Increment(ref _malformed);
            _logger.LogWarning(
                "Kick consumer: malformed {Type} entry {Id} ACKed unhandled ({Malformed} so far)",
                KickEvents.SessionSuperseded, entry.Id, malformed);
            return;
        }
        if (parsed.ServerId != _serverId)
        {
            // Broadcast stream: another server's kick. Skip quietly.
            return;
        }

        try
        {
            await _handler(parsed);
        }
        catch (Exception ex)
        {
            // The handler is expected to be exception-free (KickPlayerAsync
            // swallows its own I/O failures); a throw here is a bug, but it must
            // not kill the loop or turn this entry into poison — it is ACKed.
            _logger.LogError(ex,
                "Kick handler failed for user {UserId} (entry {Id}); entry ACKed",
                parsed.UserId, entry.Id);
        }
    }

    /// <summary>
    /// Stop the loop and, on this graceful path, destroy the consumer group so a
    /// permanently retired server id leaves nothing behind in Redis. A crash
    /// skips this by definition; the class doc covers why that is safe.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        if (_loop?.IsCompleted ?? true)
        {
            _cts.Dispose(); // same Cancel/Dispose teardown discipline as RedisEventStream
        }

        try
        {
            await _mux.GetDatabase().StreamDeleteConsumerGroupAsync(StreamKey, _group);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Kick consumer group {Group} not destroyed (Redis unreachable?)", _group);
        }

        if (_ownsMux)
        {
            await _mux.CloseAsync();
            _mux.Dispose();
        }
    }
}
