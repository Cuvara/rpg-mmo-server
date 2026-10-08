using System.IO;
using GameServer.Input;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Server;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Envelope = GameServer.Net.Envelope;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// <c>ack_applied_tick</c> (SnapshotMessage field 7) names the SERVER tick that applied the
/// input <c>ack_tick</c> acknowledges, i.e. the tick whose input drain accepted it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the value must be the drain tick and nothing else.</b> A client reconciles a snapshot
/// at server tick T against its prediction history at <c>T + (ack_tick - ack_applied_tick)</c>.
/// Any other choice (the snapshot tick, the client's own stamp, the tick of the last movement
/// step) shifts that comparison by whole ticks, and each tick of shift is a reconciliation error
/// the client then "corrects" into rubber-banding. So the tests below stamp inputs with client
/// ticks deliberately far from the server's tick line: a field that leaked either tick number
/// would be off by hundreds, not by one.
/// </para>
/// <para>
/// <b>Protocol 3 only.</b> The field is additive and a protocol 2 receiver would skip it, but the
/// protocol 2 bytes are pinned (<see cref="V2WireIdentityTests"/>), so a protocol 2 peer is never
/// sent it. Both arms are asserted.
/// </para>
/// </remarks>
public class AckAppliedTickTests
{
    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = Stream.Null;
        public string RemoteEndPoint => "ack-applied-test";
        public void Close() { }
        public void Dispose() { }
    }

    private sealed class Rig : IDisposable
    {
        public readonly EcsWorld World = new();
        public readonly TickLoop Loop;
        public readonly Connection Conn;

        public Rig(uint peerVersion, WireEncoding encoding = WireEncoding.Proto)
        {
            // Uniform rate: every base tick drains input AND broadcasts, so the tick a
            // snapshot reports and the tick that drained an input are both observable per
            // TickOnce. The split default would hide three drains in four between broadcasts.
            var rates = SimulationRates.Uniform(60);
            var connections = new ConnectionManager();
            World.AddEntity(TestHelpers.CreatePlayer("p1", x: 10, y: 10));
            var handler = new InputHandler(
                World, NullLogger.Instance, null, rates.MovementHz, MapBounds.Default,
                onRejected: null, onAttackAccepted: null, events: null, content: null);
            Loop = new TickLoop(World, handler, null, connections, rates,
                GameConstants.DefaultAoiRadius, NullLogger.Instance);
            Conn = new Connection("p1", new NullTransport(), NullLogger.Instance, encoding)
            {
                PeerProtocolVersion = peerVersion,
            };
            connections.Add(Conn);
        }

        /// <summary>One base tick, then claim and encode exactly what the write task would.</summary>
        public SnapshotMessage TickAndEncode()
        {
            Loop.TickOnce();
            Assert.True(Conn.TakePendingSnapshot(
                out var buffer, out int count, out ulong tick, out ulong ackTick, out int keyframeInterval,
                out var anchor, out var events, out int eventCount, out int observerKey,
                out var v3, out ulong ackAppliedTick));
            return Conn.DeltaState.Encode(
                tick, ackTick, buffer.AsSpan(0, count), keyframeInterval,
                intern: Conn.Encoding == WireEncoding.Proto, observer: anchor,
                events: events.AsSpan(0, eventCount), observerKey: observerKey,
                v3: v3, ackAppliedTick: ackAppliedTick);
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void AckAppliedTick_IsTheTickThatDrainedTheAckedInput()
    {
        using var rig = new Rig(peerVersion: 3);

        // Before any input: nothing acknowledged, nothing applied.
        for (int i = 0; i < 3; i++)
        {
            SnapshotMessage idle = rig.TickAndEncode();
            Assert.Equal(0ul, idle.AckTick);
            Assert.Equal(0ul, idle.AckAppliedTick);
        }

        // Client tick 500 against a server near tick 3: the two tick lines are far apart on
        // purpose, so a field carrying either the client stamp or the snapshot tick fails.
        rig.World.PushInput("p1", new InputData(500, 1f, 0f, null));
        SnapshotMessage applied = rig.TickAndEncode();
        ulong drainTick = rig.Loop.CurrentTick;
        Assert.Equal(500ul, applied.AckTick);
        Assert.Equal(drainTick, applied.AckAppliedTick);
        Assert.Equal(applied.Tick, applied.AckAppliedTick);

        // Held movement keeps stepping the player on later ticks, but no new input was
        // accepted: the pair stays (500, drainTick) while the snapshot tick moves on.
        for (int i = 0; i < 4; i++)
        {
            SnapshotMessage later = rig.TickAndEncode();
            Assert.Equal(500ul, later.AckTick);
            Assert.Equal(drainTick, later.AckAppliedTick);
            Assert.True(later.Tick > later.AckAppliedTick);
        }

        // Two inputs drained by the same tick: the newest is acked, and it was applied on that
        // drain tick, not on the tick of the earlier one.
        rig.World.PushInput("p1", new InputData(503, 1f, 0f, null));
        rig.World.PushInput("p1", new InputData(504, 0f, 1f, null));
        SnapshotMessage batch = rig.TickAndEncode();
        Assert.Equal(504ul, batch.AckTick);
        Assert.Equal(rig.Loop.CurrentTick, batch.AckAppliedTick);
        Assert.NotEqual(drainTick, batch.AckAppliedTick);
    }

    [Fact]
    public void ARejectedInput_DoesNotMoveTheAppliedTick()
    {
        using var rig = new Rig(peerVersion: 3);
        rig.World.PushInput("p1", new InputData(50, 1f, 0f, null));
        SnapshotMessage first = rig.TickAndEncode();
        ulong drainTick = first.AckAppliedTick;
        Assert.NotEqual(0ul, drainTick);

        // Stale (<= the acked tick): rejected, so it applied nothing and must not claim a tick.
        rig.World.PushInput("p1", new InputData(49, 0f, 1f, null));
        SnapshotMessage second = rig.TickAndEncode();
        Assert.Equal(50ul, second.AckTick);
        Assert.Equal(drainTick, second.AckAppliedTick);
    }

    [Fact]
    public void AProtocol2Peer_IsNeverSentTheField()
    {
        using var rig = new Rig(peerVersion: 2);
        rig.World.PushInput("p1", new InputData(500, 1f, 0f, null));
        SnapshotMessage msg = rig.TickAndEncode();

        // The control: the input WAS acknowledged, so a zero below is the gate and not an
        // input that never landed.
        Assert.Equal(500ul, msg.AckTick);
        Assert.Equal(0ul, msg.AckAppliedTick);
    }

    [Theory]
    [InlineData(WireEncoding.Proto)]
    [InlineData(WireEncoding.Json)]
    public void TheFieldRoundTripsInBothEncodings(WireEncoding encoding)
    {
        using var rig = new Rig(peerVersion: 3, encoding);
        rig.World.PushInput("p1", new InputData(500, 1f, 0f, null));
        SnapshotMessage sent = rig.TickAndEncode();
        Assert.NotEqual(0ul, sent.AckAppliedTick);

        byte[] frame = WireProtocol.Encode(WireProtocol.NewEnvelope(MsgType.Snapshot, sent, encoding));
        using var stream = new MemoryStream(frame);
        Envelope? env = WireProtocol.DecodeAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
        Assert.NotNull(env);
        SnapshotMessage back = WireProtocol.GetPayload<SnapshotMessage>(env!);

        Assert.Equal(sent.Tick, back.Tick);
        Assert.Equal(sent.AckTick, back.AckTick);
        Assert.Equal(sent.AckAppliedTick, back.AckAppliedTick);
    }

    [Fact]
    public void TheJsonKeyIsOmittedWhenZero()
    {
        // Zero is "not sent" in both encodings; writing it would assert an applied tick of 0.
        var msg = new SnapshotMessage { Tick = 7, AckTick = 3 };
        string json = System.Text.Encoding.UTF8.GetString(JsonWriter.Write(msg));
        Assert.DoesNotContain("ack_applied_tick", json);

        msg.AckAppliedTick = 6;
        json = System.Text.Encoding.UTF8.GetString(JsonWriter.Write(msg));
        Assert.Contains("\"ack_applied_tick\":6", json);
        Assert.Equal(6ul, JsonReader.ReadSnapshotMessage(JsonWriter.Write(msg)).AckAppliedTick);
    }
}
