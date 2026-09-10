namespace GameServer.Observability;

/// <summary>
/// Measures whether frames can reach the per-connection decode step out of order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> ADR-22's transport-crypto model uses the AEAD nonce as a
/// monotonic sequence number and rejects any nonce at or below the highest seen. That is
/// only safe if frames cannot legitimately arrive out of order; if they can, the rule
/// needs a sliding window, and a window has its own bugs — every one of them a security
/// bug. The ADR left the question open to be <i>measured rather than assumed</i>. This is
/// the measurement.
/// </para>
/// <para>
/// <b>Where it observes, and why that is the right place.</b> It is driven from the input
/// dispatch inside <c>Connection.ReadLoopAsync</c>'s handler, which is awaited inline: one
/// frame is decoded, dispatched and completed before the next is read. So the order seen
/// here is the order bytes arrived on that connection's stream — the same order a decrypt
/// step would see, because a decrypt would sit immediately after the same decode.
/// </para>
/// <para>
/// <b>It deliberately does not observe the tick loop.</b> The existing
/// <c>input.Tick &lt;= cursor.LastInputTick</c> check in <c>InputHandler</c> runs after
/// ingest queueing and movement coalescing, so it reports post-queue order, not arrival
/// order. Those are different questions and conflating them would answer neither.
/// </para>
/// <para>
/// <b>The signal.</b> Client input ticks are strictly increasing per connection by
/// construction, so any frame whose tick is not greater than the highest already seen on
/// that connection arrived out of order, was duplicated, or is a replay. On a healthy
/// ordered transport this must read exactly zero.
/// </para>
/// <para>
/// <b>Threading.</b> One read loop per connection, so the per-connection cursor is
/// single-threaded and needs no synchronisation; the process-wide aggregates are written
/// from every read loop and use <see cref="Interlocked"/>.
/// </para>
/// </remarks>
public sealed class FrameOrderProbe
{
    private long _framesObserved;
    private long _inversions;
    private long _duplicates;
    private long _largestBackwardJump;
    private long _forwardGaps;

    /// <summary>Input frames whose arrival order was inspected.</summary>
    public long FramesObserved => Interlocked.Read(ref _framesObserved);

    /// <summary>
    /// Frames that arrived with a tick STRICTLY BELOW the highest already seen on their
    /// connection — genuine reordering or replay. The number ADR-22 turns on.
    /// </summary>
    public long Inversions => Interlocked.Read(ref _inversions);

    /// <summary>
    /// Frames repeating the highest tick already seen. Counted apart from
    /// <see cref="Inversions"/> because a duplicate and a reorder have different causes and
    /// a strict monotonic rule rejects both — but only one of them means the transport
    /// failed to order.
    /// </summary>
    public long Duplicates => Interlocked.Read(ref _duplicates);

    /// <summary>
    /// How far back the worst inversion reached, in ticks. This is what a sliding window
    /// would have to be wide enough to cover; zero means no window is needed to accept
    /// anything actually observed.
    /// </summary>
    public long LargestBackwardJump => Interlocked.Read(ref _largestBackwardJump);

    /// <summary>
    /// Frames arriving more than one tick above the previous highest — a gap, i.e. a frame
    /// that was lost or never sent.
    /// </summary>
    /// <remarks>
    /// A strict "greater than the highest seen" rule must ACCEPT these: after a genuine
    /// loss the counter has a hole, and rejecting forward gaps would turn packet loss into
    /// a self-inflicted disconnect. Counted so the distinction between "gaps happen" and
    /// "reordering happens" is measured rather than argued.
    /// </remarks>
    public long ForwardGaps => Interlocked.Read(ref _forwardGaps);

    /// <summary>
    /// Record one input frame's arrival. <paramref name="highestSeen"/> is the connection's
    /// own cursor and is updated in place.
    /// </summary>
    /// <returns>True if this frame arrived out of order relative to that connection.</returns>
    public bool Observe(ref ulong highestSeen, ulong tick)
    {
        Interlocked.Increment(ref _framesObserved);

        // First frame on the connection establishes the cursor and is not a judgement.
        if (highestSeen == 0)
        {
            highestSeen = tick;
            return false;
        }

        if (tick > highestSeen)
        {
            if (tick > highestSeen + 1) Interlocked.Increment(ref _forwardGaps);
            highestSeen = tick;
            return false;
        }

        if (tick == highestSeen)
        {
            Interlocked.Increment(ref _duplicates);
            return true;
        }

        Interlocked.Increment(ref _inversions);

        long back = (long)(highestSeen - tick);
        long seen;
        while (back > (seen = Interlocked.Read(ref _largestBackwardJump)))
        {
            if (Interlocked.CompareExchange(ref _largestBackwardJump, back, seen) == seen) break;
        }

        return true;
    }
}
