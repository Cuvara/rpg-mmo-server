using System;
using System.Text.Json;
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
/// #401: on a world tick with no viewers, the snapshot counters must record a zero —
/// not the last connected client's tick, again.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> Every per-tick snapshot delta was reset inside
/// <c>if (_viewerCount &gt; 0)</c> while the <c>RecordSnapshot*</c> calls at the bottom of
/// the tick ran unconditionally. With nobody connected the deltas therefore kept the values
/// the last tick WITH a viewer left behind, and were recorded again — at the world rate,
/// for the life of the process.</para>
///
/// <para><b>How it looked.</b> <c>players_online: 0</c>, no established socket on the game
/// port, and <c>snapshot_bytes</c> climbing by exactly 1140 and
/// <c>snapshot_entities_gathered</c> by exactly 279 every world tick — ~16.4 KB/s of
/// snapshots that were never gathered, never encoded and never written. A second map that
/// had never carried a client read 0 for both: same bots, same enemies, same build. The one
/// counter that stayed still was <c>snapshots_sent</c>, because <c>_snapshotsThisTick</c> is
/// reset at the top of the tick rather than inside the guard, and that disagreement between
/// two counters recorded three lines apart is what identified the mechanism.</para>
///
/// <para><b>Why the obvious test would not have caught it.</b> Asserting
/// <c>players_online == 0</c> after a disconnect passes with the defect live —
/// <c>players_online</c> was never wrong. So does asserting that a server which never had a
/// viewer counts nothing (<c>NoViewersMeansNothingGathered</c> in
/// <see cref="GatherMetricsReachTheStatusEndpointTests"/>): with no viewer ever, the stale
/// value being replayed is itself zero. The assertion has to be on a counter that moved
/// while a viewer was attached and must then stop, which is why these tests tick twice and
/// compare the second arm against the first.</para>
/// </remarks>
public class SnapshotCountersStopWithTheLastViewerTests
{
    /// <summary>Base ticks per world tick at the default 60/15 configuration.</summary>
    private const int BaseTicksPerWorldTick = 4;

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
    /// The defect, as a change against a control arm: the gather total must move while a
    /// viewer is attached (arm 1, which also proves the instrument is not simply dead), and
    /// must then stop dead once the viewer is gone (arm 2). The two arms differ in exactly
    /// one thing — whether a connection is registered.
    /// </summary>
    [Fact]
    public void GatherTotalStopsMovingWhenTheLastViewerLeaves()
    {
        var (ecs, loop, metrics, conns) = Fixture(players: 2);
        using (ecs)
        {
            var conn = AttachViewer(conns, "p0");

            const int worldTicks = 10;
            for (int i = 0; i < worldTicks * BaseTicksPerWorldTick; i++) loop.TickOnce();

            long gatheredWhileConnected = metrics.SnapshotEntitiesGathered;

            // Arm 1. A zero here would make arm 2 vacuous: a counter that never moved
            // trivially stops moving (MEASUREMENT.md section 1).
            Assert.True(gatheredWhileConnected > 0,
                "snapshot_entities_gathered did not move while a viewer was attached, so " +
                "the second arm of this test would prove nothing");

            Assert.True(conns.RemoveIfCurrent(conn),
                "the viewer was not the registered connection, so this test never removed one");
            Assert.Equal(0, conns.Count);

            for (int i = 0; i < worldTicks * BaseTicksPerWorldTick; i++) loop.TickOnce();

            // Arm 2. Equality, not "did not grow much": the defect added the departed
            // viewer's last gather once per world tick, forever.
            Assert.Equal(gatheredWhileConnected, metrics.SnapshotEntitiesGathered);
        }
    }

    /// <summary>
    /// The same property at the OTHER reset site in the same block. Kept separate from the
    /// gather assertion because the two are recorded by different calls
    /// (<c>RecordSnapshotAnchorMissing</c> vs <c>RecordSnapshotGather</c>), so a fix that
    /// moved one reset and not the other fails one test and not both.
    /// </summary>
    /// <remarks>
    /// <b>Why this counter and not <c>snapshot_bytes</c>, which is what #401 was reported
    /// against.</b> Snapshot bytes are counted at the socket, by the connection's write
    /// task; this fixture has no write pump, so <c>metrics.SnapshotBytes</c> is 0 both
    /// before and after the viewer leaves and an "it stopped moving" assertion on it passes
    /// whatever the tick loop does. That test was written, seen to pass, and then seen to
    /// SURVIVE the mutation that reverts this fix — a test that cannot fail. It is replaced
    /// by this one, which asserts on a counter the tick thread itself writes. Bytes, frames
    /// written, coalesced stagings and the shed counters are reset by the same unconditional
    /// block these two cover, and by no other line.
    /// </remarks>
    [Fact]
    public void AnchorMissingTotalStopsMovingWhenTheLastViewerLeaves()
    {
        // A viewer whose own entity does not exist: the gather cannot resolve an anchor for
        // it, which is what snapshot_anchor_missing counts (#385).
        var (ecs, loop, metrics, conns) = Fixture(players: 2);
        using (ecs)
        {
            var conn = AttachViewer(conns, "no-such-entity");

            const int worldTicks = 10;
            for (int i = 0; i < worldTicks * BaseTicksPerWorldTick; i++) loop.TickOnce();

            long missingWhileConnected = metrics.SnapshotAnchorMissing;

            Assert.True(missingWhileConnected > 0,
                "snapshot_anchor_missing did not move while an anchorless viewer was " +
                "attached, so the second arm of this test would prove nothing");

            Assert.True(conns.RemoveIfCurrent(conn));

            for (int i = 0; i < worldTicks * BaseTicksPerWorldTick; i++) loop.TickOnce();

            Assert.Equal(missingWhileConnected, metrics.SnapshotAnchorMissing);
        }
    }

    /// <summary>
    /// The connection count is a live read of the registry, not a copy of
    /// <c>players_online</c>. Asserted by moving the registry while the player counter is
    /// deliberately left alone: if the field were derived from <c>players_online</c> the two
    /// would agree here, and the disagreement that #401 needed would be unobservable.
    /// </summary>
    [Fact]
    public void ConnectionCountTracksTheRegistryAndNotPlayersOnline()
    {
        var conns = new ConnectionManager();
        var metrics = new GameMetrics("map_test", $"test.{Guid.NewGuid():N}");
        metrics.SetConnectionCountProvider(() => conns.Count);

        Assert.Equal(0, metrics.ConnectionCount);

        var conn = AttachViewer(conns, "p0");

        // players_online is untouched on purpose — this is the phantom-connection shape.
        Assert.Equal(1, metrics.ConnectionCount);
        Assert.Equal(0, metrics.PlayersOnline);
        Assert.NotEqual(metrics.PlayersOnline, metrics.ConnectionCount);

        Assert.True(conns.RemoveIfCurrent(conn));
        Assert.Equal(0, metrics.ConnectionCount);
    }

    /// <summary>
    /// The field has to reach the JSON an operator actually reads, under the name the docs
    /// give it, and next to <c>players_online</c> rather than in place of it.
    /// </summary>
    [Fact]
    public void StatusPublishesConnectionsSeparatelyFromPlayersOnline()
    {
        var status = new ServerStatus { PlayersOnline = 0, Connections = 1 };

        string json = JsonSerializer.Serialize(status);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("connections", out var connections),
            "/status has no `connections` field — its absence is what made #401 take three " +
            "30s samples and a source read instead of one request");
        Assert.Equal(1, connections.GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("players_online").GetInt32());
    }

    private static Connection AttachViewer(ConnectionManager conns, string userId)
    {
        var conn = new Connection(userId, new NullTransport(), NullLogger.Instance, WireEncoding.Proto);
        conns.Add(conn);
        return conn;
    }

    private sealed class NullTransport : GameServer.Net.Transport.ITransportConnection
    {
        public System.IO.Stream Stream { get; } = System.IO.Stream.Null;
        public string RemoteEndPoint => "snapshot-counter-stall-test";
        public void Close() { }
        public void Dispose() { }
    }
}
