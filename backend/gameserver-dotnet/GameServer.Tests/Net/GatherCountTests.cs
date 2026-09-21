using System.IO;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Net;

/// <summary>
/// The per-connection gather count (#161) — what the server considered in-interest.
/// </summary>
/// <remarks>
/// Its value is as the server-side half of a pair. A client reporting fewer entities than
/// this localises a loss to the encode/decode chain; a client reporting the same number
/// means the area of interest genuinely held that many. Neither statement was available
/// before, so "the wire is delivering N" was an inference on both sides at once.
/// </remarks>
public class GatherCountTests
{
    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = Stream.Null;
        public string RemoteEndPoint => "gather-test";
        public void Close() { }
        public void Dispose() { }
    }

    private static Connection NewConnection(string userId) =>
        new(userId, new NullTransport(), NullLogger.Instance, WireEncoding.Proto);

    [Fact]
    public void TheCountIsWhatTheAreaOfInterestHeld_NotTheWholeWorld()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        world.AddEntity(TestHelpers.CreatePlayer("near", x: 5, y: 5));
        // Far outside a radius of 50, so a count that included it would be counting the
        // world rather than the interest.
        world.AddEntity(TestHelpers.CreatePlayer("far", x: 900, y: 900));

        var conn = NewConnection("p1");
        world.ReadAll(reader => conn.GatherSnapshotView(
            reader, radius: 50f, tick: 1, keyframeInterval: 30));

        Assert.Equal(2, conn.LastGatherCount);
    }

    /// <summary>
    /// A skipped gather must not leave a stale count behind. A count that keeps reporting
    /// the last good value reads as a healthy view during exactly the failure it would
    /// otherwise reveal — the same shape as the anchor defect in #385.
    /// </summary>
    [Fact]
    public void ASkippedGatherLeavesTheCountUntouchedAtZero()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("someone", x: 0, y: 0));

        var conn = NewConnection("ghost");
        bool gathered = true;
        world.ReadAll(reader => gathered = conn.GatherSnapshotView(
            reader, radius: 50f, tick: 1, keyframeInterval: 30));

        Assert.False(gathered);
        Assert.Equal(0, conn.LastGatherCount);
    }

    [Fact]
    public void TheCountTracksTheLatestGather()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        var conn = NewConnection("p1");

        world.ReadAll(reader => conn.GatherSnapshotView(
            reader, radius: 50f, tick: 1, keyframeInterval: 30));
        Assert.Equal(1, conn.LastGatherCount);

        world.AddEntity(TestHelpers.CreatePlayer("joined", x: 2, y: 2));
        world.ReadAll(reader => conn.GatherSnapshotView(
            reader, radius: 50f, tick: 2, keyframeInterval: 30));

        Assert.Equal(2, conn.LastGatherCount);
    }
}
