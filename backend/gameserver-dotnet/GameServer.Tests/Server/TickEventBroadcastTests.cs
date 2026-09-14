using System.IO;
using GameServer.Input;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// Events must survive from the tick that produced them to the tick that broadcasts them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test that was missing, and its absence cost a shipped-looking feature.</b>
/// Input runs on the CRITICAL group, every base tick — 60 Hz by default. Snapshots ship on
/// the WORLD group, every fourth one — 15 Hz. The event buffer was cleared at the top of
/// each base tick, so three ticks in four an attack landed, the victim's HP fell, and the
/// damage event was discarded before any connection could be handed it.
/// </para>
/// <para>
/// Every unit test on both sides passed throughout: the server's own suite proved it
/// produced the event, the client package's suite proved it decoded one, and the encoder's
/// suite proved it would write one. Each half was correct in isolation. It took a real
/// socket against a real server to see that the halves were never joined — which is exactly
/// what a test at this seam is for, and why the split rates are the point of the fixture
/// rather than an incidental detail.
/// </para>
/// </remarks>
public class TickEventBroadcastTests
{
    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = Stream.Null;
        public string RemoteEndPoint => "tick-event-test";
        public void Close() { }
        public void Dispose() { }
    }

    /// <summary>
    /// Critical at 60, world at 15 — the shipped default, and the configuration in which the
    /// bug lived. A uniform-rate fixture broadcasts on every tick and cannot see it.
    /// </summary>
    private static SimulationRates SplitRates => SimulationRates.Default;

    /// <summary>
    /// Every phase of the broadcast cycle, not just the one the fixture happens to start on.
    /// </summary>
    /// <remarks>
    /// The first version of this test pushed its input before the first tick and passed with
    /// the bug reinstated — because that tick happened to be a broadcast tick, where clearing
    /// at the top of the tick is harmless. The bug only bites when the input is drained on one
    /// of the three ticks in four that do not broadcast, so the phase cannot be left to chance:
    /// the offset is swept, and every one of them has to deliver.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnEventReachesTheConnectionFromAnyPhaseOfTheBroadcastCycle(int phase)
    {
        Assert.True(SplitRates.CriticalHz > SplitRates.WorldHz,
            "this fixture is meaningless at a uniform rate: every tick would be a broadcast tick");

        using var world = new EcsWorld();
        var connections = new ConnectionManager();
        var events = new TickEventBuffer();

        world.AddEntity(TestHelpers.CreatePlayer("attacker", x: 0, y: 0, atk: 10));
        world.AddEntity(TestHelpers.CreatePlayer("victim", x: 1, y: 0, def: 5));

        var handler = new InputHandler(
            world, NullLogger.Instance, null, SplitRates.MovementHz, MapBounds.Default,
            onRejected: null, onAttackAccepted: null, events: events, content: null);

        var loop = new TickLoop(
            world, handler, events, connections, SplitRates,
            GameConstants.DefaultAoiRadius, NullLogger.Instance);

        var conn = new Connection("attacker", new NullTransport(), NullLogger.Instance, WireEncoding.Proto);
        connections.Add(conn);

        var staged = 0;

        void Pump()
        {
            loop.TickOnce();
            if (conn.TakePendingSnapshot(
                    out _, out _, out _, out _, out _, out _,
                    out _, out int eventCount, out _))
            {
                staged += eventCount;
            }
        }

        // Advance into the requested phase before the attack lands.
        for (var i = 0; i < phase; i++) Pump();

        world.PushInput("attacker", new InputData(1, 0f, 0f, "victim"));

        // One full broadcast interval, plus room for the input to be drained.
        for (var i = 0; i < SplitRates.WorldEvery * 2 + 1; i++) Pump();

        Assert.True(staged > 0,
            $"phase {phase}: no event was ever staged on the connection, although the attack " +
            "landed. The buffer is being cleared between the tick that produces an event and " +
            "the tick that broadcasts one.");
    }

    /// <summary>
    /// The other half of the same contract: an event is delivered ONCE. Clearing at the
    /// broadcast boundary rather than per tick must not turn into never clearing.
    /// </summary>
    [Fact]
    public void AnEventIsNotDeliveredTwice()
    {
        using var world = new EcsWorld();
        var connections = new ConnectionManager();
        var events = new TickEventBuffer();

        world.AddEntity(TestHelpers.CreatePlayer("attacker", x: 0, y: 0, atk: 10));
        world.AddEntity(TestHelpers.CreatePlayer("victim", x: 1, y: 0, def: 5));

        var handler = new InputHandler(
            world, NullLogger.Instance, null, SplitRates.MovementHz, MapBounds.Default,
            onRejected: null, onAttackAccepted: null, events: events, content: null);

        var loop = new TickLoop(
            world, handler, events, connections, SplitRates,
            GameConstants.DefaultAoiRadius, NullLogger.Instance);

        var conn = new Connection("attacker", new NullTransport(), NullLogger.Instance, WireEncoding.Proto);
        connections.Add(conn);

        world.PushInput("attacker", new InputData(1, 0f, 0f, "victim"));

        var total = 0;
        for (var i = 0; i < SplitRates.WorldEvery * 6; i++)
        {
            loop.TickOnce();
            if (conn.TakePendingSnapshot(
                    out _, out _, out _, out _, out _, out _,
                    out _, out int eventCount, out _))
            {
                total += eventCount;
            }
        }

        // One attack produces one damage event. A buffer that stopped clearing would restage
        // it on every broadcast, and a player would see the same hit over and over.
        Assert.Equal(1, total);
    }

    [Fact]
    public void AServerWithNoViewersDoesNotAccumulateEvents()
    {
        using var world = new EcsWorld();
        var connections = new ConnectionManager();
        var events = new TickEventBuffer();

        world.AddEntity(TestHelpers.CreatePlayer("attacker", x: 0, y: 0, atk: 10));
        world.AddEntity(TestHelpers.CreatePlayer("victim", x: 1, y: 0, def: 5));

        var handler = new InputHandler(
            world, NullLogger.Instance, null, SplitRates.MovementHz, MapBounds.Default,
            onRejected: null, onAttackAccepted: null, events: events, content: null);

        var loop = new TickLoop(
            world, handler, events, connections, SplitRates,
            GameConstants.DefaultAoiRadius, NullLogger.Instance);

        // Nobody connected. The clear still has to be reached, or a headless server —
        // a load generator's target, a map with no players on it — grows the buffer until
        // it hits Capacity and starts reporting drops that mean nothing.
        for (var i = 0; i < SplitRates.WorldEvery * 4; i++)
        {
            world.PushInput("attacker", new InputData((ulong)(i + 1), 0f, 0f, "victim"));
            loop.TickOnce();
        }

        Assert.True(events.Count < TickEventBuffer.Capacity,
            $"buffer grew to {events.Count} with no viewers; the clear is not reached on the " +
            "viewerless path");
        Assert.Equal(0, events.Dropped);
    }
}
