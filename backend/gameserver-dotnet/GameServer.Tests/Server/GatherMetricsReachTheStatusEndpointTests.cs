using System;
using GameServer.Input;
using GameServer.Net;
using GameServer.Observability;
using GameServer.Server;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// The gather counters must survive the trip from the gather to <see cref="GameMetrics"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists because they did not. <c>snapshot_entities_gathered</c>,
/// <c>snapshot_max_gather</c> and <c>snapshot_anchor_missing</c> were accumulated during the
/// gather and then zeroed a few lines later, by a tidy per-tick reset block that sat between
/// the thing writing them and the call recording them. They could only ever report <b>0</b>.
/// </para>
/// <para>
/// <b>Every existing test still passed</b>, because they assert at the <see cref="Connection"/>
/// boundary — <c>LastGatherCount</c>, and the bool <c>GatherSnapshotView</c> returns — and
/// that boundary was correct the whole time. Nothing asserted what reached the metrics, which
/// is the only place an operator can see any of it.
/// </para>
/// <para>
/// It was found by reading <c>/status</c> on a live server carrying 165 enemies and 3 players:
/// 15MB of snapshots sent, every gather counter reading zero. A counter whose failure mode is
/// a healthy-looking zero is exactly the thing these counters were added to expose, so having
/// it happen to them is the reason this test asserts end to end rather than in the middle.
/// </para>
/// </remarks>
public class GatherMetricsReachTheStatusEndpointTests
{
    private static (EcsWorld world, TickLoop loop, GameMetrics metrics, ConnectionManager conns)
        Fixture(int players)
    {
        var ecs = new EcsWorld();
        var conns = new ConnectionManager();
        var metrics = new GameMetrics("map_test", $"test.{Guid.NewGuid():N}");
        var handler = new InputHandler(ecs, NullLogger.Instance, null, GameConstants.DefaultTickRate, null);
        var loop = new TickLoop(
            ecs, handler, conns, GameConstants.DefaultTickRate,
            GameConstants.DefaultAoiRadius, NullLogger.Instance, metrics);

        for (int i = 0; i < players; i++)
        {
            ecs.AddEntity(TestHelpers.CreatePlayer($"p{i}", x: i * 2f, y: 0f));
        }

        return (ecs, loop, metrics, conns);
    }

    /// <summary>
    /// A world with entities in interest must report a non-zero gather. The assertion is
    /// <c>&gt; 0</c> rather than an exact count on purpose: the exact number depends on the
    /// keyframe schedule and viewer count, and pinning it would make this test fail for
    /// reasons that are not the defect. Zero is the defect.
    /// </summary>
    [Fact]
    public void AGatherWithEntitiesInInterestIsCounted()
    {
        var (ecs, loop, metrics, conns) = Fixture(players: 2);
        using (ecs)
        {
            AttachViewer(conns, "p0");

            for (int i = 0; i < 10; i++) loop.TickOnce();

            Assert.True(metrics.SnapshotEntitiesGathered > 0,
                "snapshot_entities_gathered read 0 after ten ticks with two entities in " +
                "interest — the counter is not reaching the metrics, which is how it looked " +
                "on a live server sending 15MB of snapshots");

            Assert.True(metrics.MaxGather > 0,
                "snapshot_max_gather read 0 while entities were being gathered");
        }
    }

    /// <summary>
    /// The control. Without it, "the counter moved" is also satisfied by a counter that
    /// counts something unrelated, and a test that can only ever pass proves nothing.
    /// </summary>
    [Fact]
    public void NoViewersMeansNothingGathered()
    {
        var (ecs, loop, metrics, _) = Fixture(players: 2);
        using (ecs)
        {
            for (int i = 0; i < 10; i++) loop.TickOnce();

            Assert.Equal(0, metrics.SnapshotEntitiesGathered);
            Assert.Equal(0, metrics.MaxGather);
        }
    }

    private static void AttachViewer(ConnectionManager conns, string userId)
    {
        var conn = new Connection(userId, new NullTransport(), NullLogger.Instance, WireEncoding.Proto);
        conns.Add(conn);
    }

    private sealed class NullTransport : GameServer.Net.Transport.ITransportConnection
    {
        public System.IO.Stream Stream { get; } = System.IO.Stream.Null;
        public string RemoteEndPoint => "gather-metrics-test";
        public void Close() { }
        public void Dispose() { }
    }
}
