using System.Collections.Concurrent;

namespace GameServer.Net;

/// <summary>
/// Thread-safe registry of active player connections.
/// Uses ConcurrentDictionary keyed by user ID.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    /// <summary>Number of active connections.</summary>
    public int Count => _connections.Count;

    /// <summary>Register a connection. Replaces any existing connection for the same user ID.</summary>
    public void Add(Connection conn)
    {
        if (_connections.TryRemove(conn.UserId, out var old))
        {
            old.Close();
        }
        _connections[conn.UserId] = conn;
    }

    /// <summary>Remove and close the connection for the given user ID.</summary>
    public void Remove(string userId)
    {
        if (_connections.TryRemove(userId, out var conn))
        {
            conn.Close();
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
        int i = 0;
        foreach (var kvp in _connections)
        {
            if (i < destination.Length) destination[i] = kvp.Value;
            i++;
        }
        return i;
    }

    /// <summary>Close and remove all connections.</summary>
    public void CloseAll()
    {
        foreach (var kvp in _connections)
        {
            kvp.Value.Close();
        }
        _connections.Clear();
    }
}
