using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Net;

namespace GameServer.Tests.Server;

public class ConnectionManagerTests : IDisposable
{
    /// <summary>
    /// Concurrent, because <see cref="CreateFakeConnection"/> is called from four threads at
    /// once by <see cref="ConcurrentAccess_NoDeadlock"/>. This was a plain <c>List</c>: an
    /// unsynchronised <c>Add</c> from four threads can corrupt the backing array, throw, or
    /// silently drop entries — and a dropped entry is a socket triple <see cref="Dispose"/>
    /// never closes, which leaks into whatever test runs next.
    /// </summary>
    private readonly ConcurrentBag<(TcpListener listener, TcpClient client, TcpClient serverSide)> _tcpPairs = new();

    /// <summary>
    /// Creates a real Connection backed by a loopback TCP pair.
    /// </summary>
    private Connection CreateFakeConnection(string userId)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        var serverSide = listener.AcceptTcpClient();
        _tcpPairs.Add((listener, client, serverSide));
        return new Connection(userId, serverSide, NullLogger.Instance);
    }

    public void Dispose()
    {
        foreach (var (listener, client, serverSide) in _tcpPairs)
        {
            try { client.Close(); } catch { }
            try { serverSide.Close(); } catch { }
            try { listener.Stop(); } catch { }
        }
    }

    [Fact]
    public void Add_IncreasesCount()
    {
        var mgr = new ConnectionManager();
        Assert.Equal(0, mgr.Count);

        mgr.Add(CreateFakeConnection("conn1"));
        Assert.Equal(1, mgr.Count);
    }

    [Fact]
    public void Get_ReturnsAddedConnection()
    {
        var mgr = new ConnectionManager();
        var conn = CreateFakeConnection("conn1");
        mgr.Add(conn);

        var result = mgr.Get("conn1");
        Assert.NotNull(result);
    }

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        var mgr = new ConnectionManager();
        var result = mgr.Get("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public void Remove_DecreasesCount()
    {
        var mgr = new ConnectionManager();
        mgr.Add(CreateFakeConnection("conn1"));
        mgr.Add(CreateFakeConnection("conn2"));
        Assert.Equal(2, mgr.Count);

        mgr.Remove("conn1");
        Assert.Equal(1, mgr.Count);
    }

    [Fact]
    public void Remove_NoLongerRetrievable()
    {
        var mgr = new ConnectionManager();
        mgr.Add(CreateFakeConnection("conn1"));
        mgr.Remove("conn1");

        var result = mgr.Get("conn1");
        Assert.Null(result);
    }

    [Fact]
    public void Remove_NonExistent_DoesNotThrow()
    {
        var mgr = new ConnectionManager();
        var ex = Record.Exception(() => mgr.Remove("nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public void ForEach_VisitsAllConnections()
    {
        var mgr = new ConnectionManager();
        mgr.Add(CreateFakeConnection("conn1"));
        mgr.Add(CreateFakeConnection("conn2"));
        mgr.Add(CreateFakeConnection("conn3"));

        var visited = new List<string>();
        mgr.ForEach(conn => visited.Add(conn.UserId));

        Assert.Equal(3, visited.Count);
        Assert.Contains("conn1", visited);
        Assert.Contains("conn2", visited);
        Assert.Contains("conn3", visited);
    }

    [Fact]
    public void ForEach_EmptyManager_DoesNothing()
    {
        var mgr = new ConnectionManager();
        var count = 0;
        mgr.ForEach(conn => count++);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Count_ReflectsCurrentState()
    {
        var mgr = new ConnectionManager();
        Assert.Equal(0, mgr.Count);

        mgr.Add(CreateFakeConnection("a"));
        mgr.Add(CreateFakeConnection("b"));
        Assert.Equal(2, mgr.Count);

        mgr.Remove("a");
        Assert.Equal(1, mgr.Count);

        mgr.Remove("b");
        Assert.Equal(0, mgr.Count);
    }

    /// <summary>
    /// Four threads hammering the same <see cref="ConnectionManager"/> must not deadlock.
    ///
    /// <para>
    /// The connections are built UP FRONT, outside the timed region. They used to be created
    /// inside the loop, which meant the 10s deadline was mostly measuring 400 loopback TCP
    /// handshakes and 1200 sockets held open until Dispose — so the assertion could fail
    /// because the box was busy, while the thing it names could not fail at all
    /// (ConnectionManager is a ConcurrentDictionary with no locks anywhere, so a slow run
    /// and a deadlocked one were indistinguishable). With the sockets hoisted out, the timed
    /// region contains only the dictionary operations under test.
    /// </para>
    /// <para>
    /// The deadline stays at 10s and is not relaxed. It is now a real liveness assertion
    /// again: pure dictionary work across 4x100 iterations finishes in milliseconds, so 10s
    /// still fails immediately if someone introduces a lock into ConnectionManager, and no
    /// longer fails because the machine was loaded.
    /// </para>
    /// </summary>
    [Fact]
    public void ConcurrentAccess_NoDeadlock()
    {
        const int threads = 4;
        const int iterations = 100;

        var mgr = new ConnectionManager();

        // Every socket this test needs, created before the clock starts.
        var connections = new Connection[threads][];
        for (int t = 0; t < threads; t++)
        {
            connections[t] = new Connection[iterations];
            for (int i = 0; i < iterations; i++)
            {
                connections[t][i] = CreateFakeConnection($"t{t}_c{i}");
            }
        }

        var tasks = new List<Task>();
        for (int t = 0; t < threads; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    var id = $"t{threadId}_c{i}";
                    mgr.Add(connections[threadId][i]);
                    _ = mgr.Get(id);
                    _ = mgr.Count;
                    mgr.Remove(id);
                }
            }));
        }

        Assert.True(Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(10)),
            "Concurrent access timed out - possible deadlock");
    }
}
