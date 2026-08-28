using System.Collections.Concurrent;

namespace GameServer.Net;

/// <summary>
/// Thread-safe registry of active player connections.
/// Uses ConcurrentDictionary keyed by user ID.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    /// <summary>
    /// Copy-on-write snapshot of the values, rebuilt after every mutation.
    ///
    /// <para>Why it exists: <see cref="CopyTo"/> runs once per world tick on the tick
    /// thread, and a <c>foreach</c> over a <see cref="ConcurrentDictionary{K,V}"/>
    /// allocates its enumerator — measured at 64 B/call via
    /// <c>GC.GetAllocatedBytesForCurrentThread</c> (Release, 2000 calls), a steady
    /// per-tick allocation in the snapshot broadcast, which the module contract
    /// forbids. Joins and leaves are rare next to ticks, so the rebuild is the right
    /// side to pay the enumerator on; the per-tick read becomes an array copy.</para>
    /// </summary>
    private volatile Connection[] _snapshot = Array.Empty<Connection>();

    /// <summary>
    /// Guards the mutate-then-rebuild sequence so the LAST rebuild always reflects
    /// the final dictionary state. Without it two concurrent mutations could finish
    /// with the earlier rebuild published. Mutations only — reads are lock-free.
    /// </summary>
    private readonly object _mutateLock = new();

    private void RebuildSnapshotLocked()
    {
        var arr = new Connection[_connections.Count];
        int i = 0;
        foreach (var kvp in _connections)
        {
            // Count can move under a concurrent handler thread mid-enumeration;
            // resize rather than throw, then trim. Normal case: exact fit.
            if (i == arr.Length) Array.Resize(ref arr, arr.Length + 4);
            arr[i++] = kvp.Value;
        }
        if (i != arr.Length) Array.Resize(ref arr, i);
        _snapshot = arr;
    }

    /// <summary>Number of active connections.</summary>
    public int Count => _connections.Count;

    /// <summary>Register a connection. Replaces any existing connection for the same user ID.</summary>
    public void Add(Connection conn)
    {
        lock (_mutateLock)
        {
            if (_connections.TryRemove(conn.UserId, out var old))
            {
                old.Close();
            }
            _connections[conn.UserId] = conn;
            RebuildSnapshotLocked();
        }
    }

    /// <summary>Remove and close the connection for the given user ID.</summary>
    public void Remove(string userId)
    {
        lock (_mutateLock)
        {
            if (_connections.TryRemove(userId, out var conn))
            {
                conn.Close();
                RebuildSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Remove and close <paramref name="conn"/> only if it is still the connection
    /// registered for its user. Returns whether it was.
    /// </summary>
    /// <remarks>
    /// The identity check is the whole point. Teardown used to remove by user id
    /// alone, so when <see cref="Add"/> had already replaced a half-dead connection
    /// with a fresh reconnect, the dying handler's teardown found the NEW connection
    /// under its user id and closed it — the player was kicked milliseconds after a
    /// successful rejoin, and <c>players_online</c> under-counted permanently (#229).
    /// A teardown may only ever destroy the connection it belongs to.
    /// </remarks>
    public bool RemoveIfCurrent(Connection conn)
    {
        lock (_mutateLock)
        {
            if (!_connections.TryGetValue(conn.UserId, out var current) ||
                !ReferenceEquals(current, conn))
            {
                return false;
            }
            _connections.TryRemove(conn.UserId, out _);
            conn.Close();
            RebuildSnapshotLocked();
            return true;
        }
    }

    /// <summary>Look up a connection by user ID.</summary>
    public Connection? Get(string userId)
    {
        _connections.TryGetValue(userId, out var conn);
        return conn;
    }

    /// <summary>Iterate over all connections. The action must not modify the collection.</summary>
    public void ForEach(Action<Connection> action)
    {
        foreach (var kvp in _connections)
        {
            action(kvp.Value);
        }
    }

    /// <summary>
    /// Copy the current connections into <paramref name="destination"/>.
    ///
    /// <para>The snapshot broadcast needs a stable list it can walk twice — once inside
    /// the world read scope to gather, once outside it to encode and send — without
    /// holding a delegate or an enumerator across the two. Same count-don't-saturate
    /// contract as the AOI scan: the return value is the connection count and may exceed
    /// the buffer, so the caller resizes and retries rather than broadcasting to a
    /// prefix and silently starving whoever fell off the end.</para>
    /// </summary>
    public int CopyTo(Span<Connection> destination)
    {
        // Reads the copy-on-write snapshot rather than enumerating the dictionary:
        // the enumerator was a measured 64 B allocation per world tick. See _snapshot.
        var snap = _snapshot;
        int n = Math.Min(snap.Length, destination.Length);
        snap.AsSpan(0, n).CopyTo(destination);
        return snap.Length;
    }

    /// <summary>Close and remove all connections.</summary>
    public void CloseAll()
    {
        lock (_mutateLock)
        {
            foreach (var kvp in _connections)
            {
                kvp.Value.Close();
            }
            _connections.Clear();
            _snapshot = Array.Empty<Connection>();
        }
    }
}
