namespace GameServer.Server;

/// <summary>
/// Bounded pool of accepted-but-not-yet-joined transports.
///
/// <para>Before this existed the accept loop spawned a handler per accepted socket and
/// that handler awaited the first frame with no deadline, so a peer that connected and
/// sent nothing held a socket and a task for as long as it liked — outside
/// <c>GAMESERVER_CAPACITY</c>, which counts only authenticated connections, and
/// outside the heartbeat, which only starts after the join (workspace audit F03).</para>
///
/// <para>The gate is the admission bound for that pre-join phase: an accept that would
/// push the pending count past <see cref="Max"/> is closed on the spot, without a reply,
/// and counted. The deadline half of the fix (<c>GAMESERVER_HANDSHAKE_TIMEOUT_MS</c>)
/// lives in <see cref="GameServerHost"/>, which is where the read happens.</para>
/// </summary>
public sealed class HandshakeGate
{
    private int _pending;

    public HandshakeGate(int max)
    {
        Max = max < 1 ? 1 : max;
    }

    /// <summary>Most handshakes allowed in flight at once (<c>GAMESERVER_MAX_PENDING_HANDSHAKES</c>).</summary>
    public int Max { get; }

    /// <summary>Handshakes in flight right now.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>
    /// Claim a slot for a freshly accepted transport. Returns false when the pool is full;
    /// the caller then owns closing the transport. Every true must be balanced by exactly
    /// one <see cref="Exit"/>.
    /// </summary>
    public bool TryEnter()
    {
        // Increment first, then check: a compare-and-retry loop is not needed because an
        // over-increment is undone immediately and Max is a soft ceiling on sockets, not
        // an exact resource count. Two racing accepts can never both see a free slot that
        // only one of them gets.
        if (Interlocked.Increment(ref _pending) <= Max)
        {
            return true;
        }
        Interlocked.Decrement(ref _pending);
        return false;
    }

    /// <summary>Release a slot claimed by <see cref="TryEnter"/>.</summary>
    public void Exit() => Interlocked.Decrement(ref _pending);
}
