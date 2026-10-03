using System.Diagnostics;
using System.Threading.Channels;
using RpgMmo.Wire.V1;

namespace GameServer.Commands;

/// <summary>
/// Limits of the gameplay command channel (ADR-30), per connection. Every number here is a
/// placeholder pending measurement of real client behaviour; each is documented in
/// <c>docs/API.md</c> ("Command channel").
/// </summary>
public static class CommandLimits
{
    /// <summary>
    /// Token bucket size: how many commands a connection may send back to back. A UI that
    /// opens the inventory and equips a few items in one gesture stays inside it.
    /// </summary>
    public const int BurstCapacity = 10;

    /// <summary>Token bucket refill, commands per second, sustained.</summary>
    public const double RefillPerSecond = 5.0;

    /// <summary>
    /// Admitted commands that may wait for the connection's worker. Commands run one at a time
    /// per connection, in arrival order, so a character's inventory is never mutated by two
    /// requests at once; a request that finds the queue full is answered <c>rate_limited</c>.
    /// </summary>
    public const int MaxQueued = 8;

    /// <summary>
    /// Pick-up range (world units, 3D distance feet to item) for opcode 2. Generous next to the
    /// melee range because the client sees the item where the server last told it, up to a
    /// snapshot interval ago.
    /// </summary>
    public const float PickupRange = 3.0f;

    /// <summary>
    /// Attempts at a grant whose outcome is unknown (an I/O failure rather than a refusal).
    /// Safe to repeat: the grant id makes the store idempotent.
    /// </summary>
    public const int GrantAttempts = 3;
}

/// <summary>
/// A token bucket over the monotonic clock. Single-threaded: only the owning connection's
/// read loop takes from it.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _refillPerTick;
    private double _tokens;
    private long _last;

    /// <summary>Builds a full bucket.</summary>
    /// <param name="capacity">Maximum tokens (burst).</param>
    /// <param name="refillPerSecond">Tokens added per second.</param>
    /// <param name="now">Current <see cref="Stopwatch.GetTimestamp"/> value.</param>
    public TokenBucket(int capacity, double refillPerSecond, long now)
    {
        _capacity = capacity;
        _refillPerTick = refillPerSecond / Stopwatch.Frequency;
        _tokens = capacity;
        _last = now;
    }

    /// <summary>Take one token at <paramref name="now"/> (a <see cref="Stopwatch"/> timestamp). False when empty.</summary>
    public bool TryTake(long now)
    {
        if (now > _last)
        {
            _tokens = Math.Min(_capacity, _tokens + (now - _last) * _refillPerTick);
            _last = now;
        }

        if (_tokens < 1.0) return false;
        _tokens -= 1.0;
        return true;
    }
}

/// <summary>
/// One connection's command-channel state: its rate-limit bucket and the ordered queue its
/// worker drains. Created with the connection, inert until the first command.
/// </summary>
public sealed class CommandSession
{
    private TokenBucket? _bucket;
    private Channel<CommandRequest>? _queue;
    private int _workerStarted;

    /// <summary>Rate-limit a request at <paramref name="now"/>. Read loop only.</summary>
    public bool TryAdmit(long now)
    {
        _bucket ??= new TokenBucket(CommandLimits.BurstCapacity, CommandLimits.RefillPerSecond, now);
        return _bucket.TryTake(now);
    }

    /// <summary>
    /// Queue an admitted request for the worker, starting the worker on first use. False when
    /// <see cref="CommandLimits.MaxQueued"/> requests are already waiting.
    /// </summary>
    public bool TryEnqueue(CommandRequest request, Func<ChannelReader<CommandRequest>, Task> startWorker)
    {
        _queue ??= Channel.CreateBounded<CommandRequest>(new BoundedChannelOptions(CommandLimits.MaxQueued)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        if (!_queue.Writer.TryWrite(request)) return false;
        if (Interlocked.Exchange(ref _workerStarted, 1) == 0)
        {
            ChannelReader<CommandRequest> reader = _queue.Reader;
            _ = Task.Run(() => startWorker(reader));
        }
        return true;
    }
}
