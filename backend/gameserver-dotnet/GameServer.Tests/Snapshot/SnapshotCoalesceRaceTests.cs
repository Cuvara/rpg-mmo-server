using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// Pins the reclaim latch in <see cref="Connection.GatherSnapshotView"/> (#247).
///
/// <para>The coalescing branch used to leave <c>_snapshotPending == true</c>, release
/// the lock, and only then refill the staged buffer. A <c>TakePendingSnapshot</c> from
/// the write task landing in that window claimed the very buffer the gather was
/// rewriting, so the encoder read torn rows — frames mixing tick N and N+1, with
/// <c>_lastSent</c> advanced from the mixed view, freezing entities until the next
/// keyframe. The fix latches the job back to unclaimed before the lock releases; a
/// claim inside the window must now miss (the surplus-marker path), and the second
/// lock republishes when the refill is done.</para>
/// </summary>
public class SnapshotCoalesceRaceTests
{
    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = new MemoryStream();
        public string RemoteEndPoint => "null";
        public void Close() { }
        public void Dispose() { }
    }

    [Fact]
    public void ClaimInsideTheCoalesceWindow_MissesInsteadOfTakingTheBufferBeingRefilled()
    {
        var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0f, y: 0f, speed: 4f));

        var conn = new Connection("p1", new NullTransport(), NullLogger.Instance,
            WireEncoding.Proto);

        // Tick 1 stages a snapshot nobody claims.
        world.ReadAll(reader =>
            conn.GatherSnapshotView(reader, radius: 50f, tick: 1, keyframeInterval: 30));

        bool claimedInWindow = true;
        bool sawWindow = false;

        // Tick 2 coalesces. At the exact boundary between the reclaim lock and the
        // buffer refill, drive the write task's claim: with the latch it must miss.
        conn.BetweenGatherLocksForTest = () =>
        {
            sawWindow = true;
            claimedInWindow = conn.TakePendingSnapshot(
                out _, out _, out _, out _, out _);
        };
        world.ReadAll(reader =>
            conn.GatherSnapshotView(reader, radius: 50f, tick: 2, keyframeInterval: 30));
        conn.BetweenGatherLocksForTest = null;

        Assert.True(sawWindow, "the test seam never ran — the coalesce path was not taken");
        Assert.False(claimedInWindow,
            "a claim inside the reclaim window took the buffer the gather was refilling");

        // The republish after the refill must hand out the coalesced job normally.
        Assert.True(conn.TakePendingSnapshot(out _, out _, out ulong tick, out _, out _));
        Assert.Equal(2UL, tick);
        Assert.Equal(1L, conn.SnapshotsCoalesced);
    }
}
