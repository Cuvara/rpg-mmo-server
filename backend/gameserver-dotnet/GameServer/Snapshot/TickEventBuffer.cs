using System;
using System.Collections.Generic;
using Shared.GameLogic.Components;

namespace GameServer.Snapshot;

/// <summary>
/// The events one tick produced, collected while inputs are processed and read by every
/// connection's gather in the same tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime is exactly one tick.</b> <see cref="Clear"/> runs at the start of the input
/// phase and the gather phase reads what accumulated. Nothing here survives into the next
/// tick, because an event that outlived its tick would be delivered twice — once by the
/// snapshot it belongs to and once by the next one.
/// </para>
/// <para>
/// <b>Threading.</b> Written by the tick thread during input processing (inside the world
/// write lock) and read by the tick thread during gather. The write task never touches it:
/// gather copies what a connection may see into that connection's own queue, because the
/// write task encodes later and this buffer is gone by then.
/// </para>
/// <para>
/// <b>Bounded.</b> A tick that produced more than <see cref="Capacity"/> events drops the
/// surplus and counts it rather than growing without limit. The cap is not a performance
/// guess: it is the difference between a pathological tick costing a bounded amount of work
/// per observer and costing an unbounded amount, and the event path is per-observer.
/// </para>
/// </remarks>
public sealed class TickEventBuffer
{
    /// <summary>
    /// Most events one tick may carry. Beyond this the surplus is dropped and counted.
    /// </summary>
    /// <remarks>
    /// Sized against the worst ordinary tick rather than the worst conceivable one: every
    /// player in a 200-player instance landing a hit on the same tick is 200 damage events
    /// plus their deaths. A tick that exceeds this is producing more events than any client
    /// could present anyway.
    /// </remarks>
    public const int Capacity = 512;

    private readonly List<PendingGameEvent> _events = new(64);

    /// <summary>Events produced this tick, in the order the simulation produced them.</summary>
    public IReadOnlyList<PendingGameEvent> Events => _events;

    public int Count => _events.Count;

    /// <summary>Total events dropped because a tick exceeded <see cref="Capacity"/>.</summary>
    /// <remarks>
    /// Exposed rather than logged: this fires on the tick thread inside the world write
    /// lock, where a log line is the thing that turns a busy tick into a slow one. A counter
    /// read by /status says the same thing and costs an increment.
    /// </remarks>
    public long Dropped { get; private set; }

    /// <summary>
    /// Records an event. <paramref name="sourceKey"/> and <paramref name="targetKey"/> are
    /// <see cref="PendingGameEvent.NoKey"/> when the event has no such participant.
    /// </summary>
    public void Add(in GameEventData data, int sourceKey, int targetKey)
    {
        if (_events.Count >= Capacity)
        {
            Dropped++;
            return;
        }

        _events.Add(new PendingGameEvent(in data, sourceKey, targetKey));
    }

    /// <summary>Drops everything from the previous tick. Called once per tick, before input.</summary>
    public void Clear() => _events.Clear();
}
