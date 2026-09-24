using System.IO;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Snapshot;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Net;

/// <summary>
/// Events staged on a connection must survive snapshot coalescing.
/// </summary>
/// <remarks>
/// <para>
/// Coalescing is documented as lossless, and for STATE it is: a newer gather already
/// describes everything an unclaimed older one would have, so overwriting it loses nothing.
/// Events are the opposite. They are the occurrences that produced that state, and the newer
/// gather does not contain the older one's — so staging them the same way would silently
/// drop a damage number every time a connection fell a tick behind, which is precisely the
/// load under which a player is most likely to be in combat.
/// </para>
/// <para>
/// Nothing else in the suite would catch that: the server would emit the event, the encoder
/// would be willing to write it, and it would simply never be handed to either.
/// </para>
/// </remarks>
public class ConnectionEventStagingTests
{
    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = Stream.Null;
        public string RemoteEndPoint => "staging-test";
        public void Close() { }
        public void Dispose() { }
    }

    private static Connection NewConnection(string userId = "p1") =>
        new(userId, new NullTransport(), NullLogger.Instance, WireEncoding.Proto);

    private static (EcsWorld world, Connection conn) Fixture()
    {
        var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        return (world, NewConnection());
    }

    private static TickEventBuffer BufferWith(int amount)
    {
        var buffer = new TickEventBuffer();
        buffer.Add(GameEventData.Damage("p1", "mob-1", amount), 1, 2);
        return buffer;
    }

    [Fact]
    public void EventsStagedByAGather_AreHandedOutWhenTheSnapshotIsClaimed()
    {
        var (world, conn) = Fixture();
        using (world)
        {
            world.ReadAll(reader => conn.GatherSnapshotView(
                reader, GameConstants.DefaultAoiRadius, tick: 1,
                keyframeInterval: 30, tickEvents: BufferWith(11)));

            Assert.True(conn.TakePendingSnapshot(
                out _, out _, out _, out _, out _, out _,
                out var events, out int eventCount, out _));

            Assert.Equal(1, eventCount);
            Assert.Equal(11, events[0].Data.Amount);
        }
    }

    /// <summary>
    /// The test this file exists for. Two gathers, no claim in between — the second
    /// overwrites the first's snapshot. Both ticks' events must still be delivered.
    /// </summary>
    [Fact]
    public void EventsAccumulateAcrossACoalescedGather_RatherThanBeingOverwritten()
    {
        var (world, conn) = Fixture();
        using (world)
        {
            world.ReadAll(reader => conn.GatherSnapshotView(
                reader, GameConstants.DefaultAoiRadius, 1, 30, BufferWith(11)));

            // No TakePendingSnapshot here: this is the coalescing case.
            world.ReadAll(reader => conn.GatherSnapshotView(
                reader, GameConstants.DefaultAoiRadius, 2, 30, BufferWith(22)));

            Assert.True(conn.TakePendingSnapshot(
                out _, out _, out ulong tick, out _, out _, out _,
                out var events, out int eventCount, out _));

            // Newest state wins, as before.
            Assert.Equal(2ul, tick);

            // Both occurrences survive.
            Assert.Equal(2, eventCount);
            Assert.Equal(11, events[0].Data.Amount);
            Assert.Equal(22, events[1].Data.Amount);
        }
    }

    [Fact]
    public void ClaimingDrainsTheStagedEvents_SoTheNextSnapshotDoesNotRepeatThem()
    {
        var (world, conn) = Fixture();
        using (world)
        {
            world.ReadAll(reader => conn.GatherSnapshotView(
                reader, GameConstants.DefaultAoiRadius, 1, 30, BufferWith(11)));
            conn.TakePendingSnapshot(out _, out _, out _, out _, out _, out _, out _, out _, out _);

            world.ReadAll(reader => conn.GatherSnapshotView(
                reader, GameConstants.DefaultAoiRadius, 2, 30, tickEvents: null));

            Assert.True(conn.TakePendingSnapshot(
                out _, out _, out _, out _, out _, out _, out _, out int eventCount, out _));

            Assert.Equal(0, eventCount);
        }
    }

    /// <summary>
    /// A surplus marker claims nothing, so it must not consume the staged events either —
    /// otherwise a race between two markers would silently eat a tick's occurrences.
    /// </summary>
    [Fact]
    public void ASurplusClaim_DoesNotDiscardStagedEvents()
    {
        var (world, conn) = Fixture();
        using (world)
        {
            world.ReadAll(reader => conn.GatherSnapshotView(
                reader, GameConstants.DefaultAoiRadius, 1, 30, BufferWith(11)));
            conn.TakePendingSnapshot(out _, out _, out _, out _, out _, out _, out _, out _, out _);

            // Second claim with nothing staged: the surplus-marker path.
            Assert.False(conn.TakePendingSnapshot(
                out _, out _, out _, out _, out _, out _, out _, out int eventCount, out _));
            Assert.Equal(0, eventCount);
        }
    }

    [Fact]
    public void TheObserverKeyIsLatched_SoPrivateEventsCanBeAddressed()
    {
        var (world, conn) = Fixture();
        using (world)
        {
            world.ReadAll(reader => conn.GatherSnapshotView(
                reader, GameConstants.DefaultAoiRadius, 1, 30, tickEvents: null));

            conn.TakePendingSnapshot(
                out _, out _, out _, out _, out _, out _, out _, out _, out int observerKey);

            Assert.NotEqual(PendingGameEvent.NoKey, observerKey);
        }
    }

    /// <summary>
    /// A connection whose write task has stalled must not accumulate without limit. The
    /// OLDEST are dropped, not the newest: a client catching up needs the recent world.
    /// </summary>
    [Fact]
    public void StagedEventsAreBounded_AndTheOldestAreWhatIsDropped()
    {
        var (world, conn) = Fixture();
        using (world)
        {
            var overflow = new TickEventBuffer();
            for (int i = 0; i < Connection.MaxStagedEvents + 5; i++)
            {
                overflow.Add(GameEventData.Damage("p1", "mob-1", i), 1, 2);
            }

            world.ReadAll(reader => conn.GatherSnapshotView(
                reader, GameConstants.DefaultAoiRadius, 1, 30, overflow));

            conn.TakePendingSnapshot(
                out _, out _, out _, out _, out _, out _,
                out var events, out int eventCount, out _);

            Assert.Equal(Connection.MaxStagedEvents, eventCount);
            Assert.Equal(5, conn.SnapshotEventsDropped);
            // The first surviving entry is the 6th produced, not the 1st.
            Assert.Equal(5, events[0].Data.Amount);
        }
    }
}
