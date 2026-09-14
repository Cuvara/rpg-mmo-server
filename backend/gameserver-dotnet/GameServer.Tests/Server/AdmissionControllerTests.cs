using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Server;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// <see cref="AdmissionController"/> and <see cref="HandshakeGate"/> in isolation: the
/// reservation arithmetic and the bounded pool, without a socket. The live-path
/// counterparts are <see cref="AtomicAdmissionTests"/> and <see cref="HandshakeHardeningTests"/>.
/// </summary>
public class AdmissionControllerTests
{
    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = Stream.Null;
        public string RemoteEndPoint => "test";
        public void Close() { }
        public void Dispose() { }
    }

    private static Connection Conn(string userId) =>
        new(userId, new NullTransport(), NullLogger.Instance);

    /// <summary>
    /// The race the controller exists to close: many reservations taken at once against
    /// one free slot admit exactly one. Threads, not tasks, so the reservations really do
    /// overlap; a Barrier releases them together.
    /// </summary>
    [Fact]
    public void ConcurrentReservations_AdmitExactlyCapacity()
    {
        const int capacity = 5;
        const int contenders = 64;
        var connections = new ConnectionManager();
        var admission = new AdmissionController(connections, capacity);

        int admitted = 0;
        using var gate = new Barrier(contenders);
        var threads = new Thread[contenders];
        for (int t = 0; t < contenders; t++)
        {
            string user = $"user-{t}";
            threads[t] = new Thread(() =>
            {
                gate.SignalAndWait();
                if (admission.TryReserve(user, out _)) Interlocked.Increment(ref admitted);
            });
            threads[t].Start();
        }
        foreach (var th in threads) th.Join();

        Assert.Equal(capacity, admitted);
        Assert.Equal(capacity, admission.PendingReservations);
        Assert.Equal(capacity, admission.Occupancy);
    }

    [Fact]
    public void Release_GivesTheSlotBack()
    {
        var connections = new ConnectionManager();
        var admission = new AdmissionController(connections, 1);

        Assert.True(admission.TryReserve("a", out _));
        Assert.False(admission.TryReserve("b", out int seen));
        Assert.Equal(1, seen);

        admission.Release("a");
        Assert.True(admission.TryReserve("b", out _));
        // Idempotent: a second release of the same user must not free b's slot.
        admission.Release("a");
        Assert.False(admission.TryReserve("c", out _));
    }

    [Fact]
    public void Commit_RetiresTheReservation_AndRegistersTheConnection()
    {
        var connections = new ConnectionManager();
        var admission = new AdmissionController(connections, 1);

        Assert.True(admission.TryReserve("a", out _));
        var conn = Conn("a");
        Assert.True(admission.Commit(conn));

        Assert.Equal(0, admission.PendingReservations);
        Assert.Same(conn, connections.Get("a"));
        Assert.Equal(1, admission.Occupancy);
        Assert.False(admission.TryReserve("b", out _));
    }

    /// <summary>
    /// A user who already holds a live connection is replacing it, not joining beside it:
    /// the reservation costs nothing even at capacity, and after the commit the count is
    /// still one.
    /// </summary>
    [Fact]
    public void ReplacingALiveConnection_ConsumesNoSecondSlot()
    {
        var connections = new ConnectionManager();
        var admission = new AdmissionController(connections, 1);

        Assert.True(admission.TryReserve("a", out _));
        Assert.True(admission.Commit(Conn("a")));
        Assert.Equal(1, admission.Occupancy);

        // Full — but the same user gets in.
        Assert.False(admission.TryReserve("b", out _));
        Assert.True(admission.TryReserve("a", out _));
        Assert.Equal(1, admission.Occupancy);

        var fresh = Conn("a");
        Assert.True(admission.Commit(fresh));
        Assert.Same(fresh, connections.Get("a"));
        Assert.Equal(1, connections.Count);
        Assert.Equal(1, admission.Occupancy);
    }

    /// <summary>
    /// The one way a replacement can fail: its target disconnects mid-join and someone else
    /// takes the freed slot. The commit must refuse rather than exceed capacity — and
    /// nothing may be left reserved afterwards.
    /// </summary>
    [Fact]
    public void Replacement_WhoseTargetLeftAndSlotWasTaken_IsRefusedAtCommit()
    {
        var connections = new ConnectionManager();
        var admission = new AdmissionController(connections, 1);

        Assert.True(admission.TryReserve("a", out _));
        var old = Conn("a");
        Assert.True(admission.Commit(old));

        Assert.True(admission.TryReserve("a", out _));   // replacement, no slot
        Assert.True(connections.RemoveIfCurrent(old));   // target disconnects mid-join
        Assert.True(admission.TryReserve("b", out _));   // b takes the freed slot
        Assert.True(admission.Commit(Conn("b")));

        Assert.False(admission.Commit(Conn("a")));
        Assert.Null(connections.Get("a"));
        Assert.Equal(1, connections.Count);
        Assert.Equal(0, admission.PendingReservations);
    }

    /// <summary>A replacement whose target left, with the slot still free, simply takes it.</summary>
    [Fact]
    public void Replacement_WhoseTargetLeft_TakesTheFreedSlot()
    {
        var connections = new ConnectionManager();
        var admission = new AdmissionController(connections, 1);

        Assert.True(admission.TryReserve("a", out _));
        var old = Conn("a");
        Assert.True(admission.Commit(old));
        Assert.True(admission.TryReserve("a", out _));
        Assert.True(connections.RemoveIfCurrent(old));

        Assert.True(admission.Commit(Conn("a")));
        Assert.Equal(1, admission.Occupancy);
    }

    [Fact]
    public void HandshakeGate_EnforcesItsBound_AndReleases()
    {
        var gate = new HandshakeGate(2);
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        Assert.Equal(2, gate.Pending);

        gate.Exit();
        Assert.Equal(1, gate.Pending);
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        // A refused entry must not have moved the count.
        Assert.Equal(2, gate.Pending);
    }

    [Fact]
    public void HandshakeGate_ConcurrentEntries_NeverExceedMax()
    {
        const int max = 8;
        const int contenders = 64;
        var gate = new HandshakeGate(max);
        int entered = 0;
        using var barrier = new Barrier(contenders);
        var threads = new Thread[contenders];
        for (int t = 0; t < contenders; t++)
        {
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                if (gate.TryEnter()) Interlocked.Increment(ref entered);
            });
            threads[t].Start();
        }
        foreach (var th in threads) th.Join();

        Assert.Equal(max, entered);
        Assert.Equal(max, gate.Pending);
    }
}
