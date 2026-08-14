using System.Security.Cryptography;
using GameServer.Input;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Server;
using GameServer.World;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// End-to-end guards for the off-tick snapshot pipeline (stage 4).
///
/// <para>Encoding moved from the tick thread to each connection's own write task. Three
/// things about that are not provable by inspection and are proved here instead: that the
/// bytes are unchanged, that they leave in tick order, and that the back-pressure policy
/// loses nothing. Unlike <see cref="SnapshotByteIdentityTests"/>, which reproduces bytes
/// from public inputs, these capture what the write task <b>actually wrote to the
/// stream</b> — the production path, threading included.</para>
/// </summary>
public class SnapshotPipelineTests
{
    private const int TickRate = 15;

    /// <summary>
    /// A transport whose stream records every byte, and which can be stalled to simulate
    /// a client the writer cannot keep up with.
    /// </summary>
    private sealed class RecordingTransport : ITransportConnection
    {
        private readonly RecordingStream _stream = new();
        public Stream Stream => _stream;
        public string RemoteEndPoint => "recorder";
        public void Close() { }
        public void Dispose() { }

        public byte[] Written { get { lock (_stream.Sync) return _stream.Buffer.ToArray(); } }
        public void Stall() => _stream.Gate.Reset();
        public void Release() => _stream.Gate.Set();

        private sealed class RecordingStream : Stream
        {
            public readonly MemoryStream Buffer = new();
            public readonly object Sync = new();
            public readonly ManualResetEventSlim Gate = new(true);

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) { }

            public override void Write(byte[] b, int o, int c)
            {
                Gate.Wait();
                lock (Sync) Buffer.Write(b, o, c);
            }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> source, CancellationToken ct = default)
            {
                Gate.Wait(ct);
                lock (Sync) Buffer.Write(source.Span);
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Count complete length-prefixed frames, without parsing them. Works for
    /// both encodings, so the drain barrier does not have to know which one is in use.</summary>
    private static int CountFrames(byte[] recorded)
    {
        int offset = 0, frames = 0;
        while (offset + 4 <= recorded.Length)
        {
            int length = (recorded[offset] << 24) | (recorded[offset + 1] << 16) |
                         (recorded[offset + 2] << 8) | recorded[offset + 3];
            offset += 4;
            if (length < 0 || offset + length > recorded.Length) break;
            offset += length;
            frames++;
        }
        return frames;
    }

    /// <summary>Split a recorded byte stream back into snapshot messages, in order.</summary>
    private static List<SnapshotMessage> ParseSnapshots(byte[] recorded)
    {
        var result = new List<SnapshotMessage>();
        int offset = 0;
        while (offset + 4 <= recorded.Length)
        {
            int length = (recorded[offset] << 24) | (recorded[offset + 1] << 16) |
                         (recorded[offset + 2] << 8) | recorded[offset + 3];
            offset += 4;
            if (length < 0 || offset + length > recorded.Length) break;

            var body = new byte[length];
            Array.Copy(recorded, offset, body, 0, length);
            offset += length;

            GameServer.Net.Envelope env = WireProtocol.DecodeBody(body);
            if (env.Type == (byte)MsgType.Snapshot)
            {
                result.Add(SnapshotMessage.Parser.ParseFrom(env.Payload));
            }
        }
        return result;
    }

    private sealed class Rig : IDisposable
    {
        public EcsWorld World { get; }
        public TickLoop Loop { get; }
        public ConnectionManager Connections { get; } = new();
        public List<Connection> Conns { get; } = new();
        public List<RecordingTransport> Transports { get; } = new();
        private readonly List<Task> _writers = new();

        public Rig(int players, WireEncoding encoding)
        {
            World = new EcsWorld();
            var handler = new InputHandler(World, NullLogger.Instance, null, TickRate, MapBounds.Default);
            Loop = new TickLoop(World, handler, Connections, TickRate,
                GameConstants.DefaultAoiRadius, NullLogger.Instance, metrics: null,
                keyframeInterval: GameConstants.DefaultKeyframeInterval, simulationPhase: null);

            for (int i = 0; i < players; i++)
            {
                string id = $"p{i}";
                World.AddEntity(TestHelpers.CreatePlayer(id, x: i * 1.5f, y: 0f, speed: 4f));
                var transport = new RecordingTransport();
                var conn = new Connection(id, transport, NullLogger.Instance, encoding);
                Transports.Add(transport);
                Conns.Add(conn);
                Connections.Add(conn);
                _writers.Add(Task.Run(conn.WriteLoopAsync));
            }
        }

        public void Tick(int count)
        {
            for (int t = 0; t < count; t++)
            {
                for (int i = 0; i < Conns.Count; i++)
                {
                    World.PushInput(Conns[i].UserId,
                        new InputData(Loop.CurrentTick + 1, 1f, 0f, null));
                }
                Loop.TickOnce();
            }
        }

        /// <summary>Wait until every write task has drained its queue.</summary>
        public void Drain(int expectedPerConn)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                bool all = true;
                for (int i = 0; i < Transports.Count; i++)
                {
                    if (CountFrames(Transports[i].Written) < expectedPerConn) all = false;
                }
                if (all) return;
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Tick once and wait for every write task to emit that tick's frame, so nothing
        /// coalesces. Used where a 1:1 tick-to-frame correspondence is the thing under
        /// test; coalescing under load is covered separately and deliberately.
        /// </summary>
        public void TickAndSettle(int expectedFrames)
        {
            Loop.TickOnce();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                bool all = true;
                foreach (RecordingTransport t in Transports)
                {
                    if (CountFrames(t.Written) < expectedFrames) all = false;
                }
                if (all) return;
                Thread.Sleep(1);
            }
            throw new TimeoutException($"write tasks did not reach {expectedFrames} frames");
        }

        public void Dispose()
        {
            foreach (var c in Conns) c.Close();
            World.Dispose();
        }
    }

    // ── Ordering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshots for one connection must arrive in tick order. Encoding on another thread
    /// is exactly what could break this, and a reordering would look to a client like
    /// entities teleporting backwards — a failure no existing test would catch.
    ///
    /// <para>It holds structurally: one write task per connection reading one channel,
    /// so frames are written in the order they were dequeued. This proves it rather than
    /// asserting it.</para>
    /// </summary>
    [Fact]
    public void SnapshotsArriveInStrictTickOrderPerConnection()
    {
        using var rig = new Rig(players: 4, WireEncoding.Proto);
        rig.Tick(60);
        rig.Drain(expectedPerConn: 40);

        for (int i = 0; i < rig.Transports.Count; i++)
        {
            List<SnapshotMessage> snaps = ParseSnapshots(rig.Transports[i].Written);
            Assert.NotEmpty(snaps);

            ulong previous = 0;
            foreach (SnapshotMessage s in snaps)
            {
                Assert.True(s.Tick > previous,
                    $"connection {i}: tick {s.Tick} arrived after {previous}");
                previous = s.Tick;
            }
        }
    }

    [Fact]
    public void AckTickIsMonotonicPerConnection()
    {
        using var rig = new Rig(players: 3, WireEncoding.Proto);
        rig.Tick(50);
        rig.Drain(expectedPerConn: 30);

        foreach (RecordingTransport t in rig.Transports)
        {
            ulong previous = 0;
            foreach (SnapshotMessage s in ParseSnapshots(t.Written))
            {
                Assert.True(s.AckTick >= previous, $"ack went backwards: {s.AckTick} < {previous}");
                previous = s.AckTick;
            }
        }
    }

    // ── Back-pressure ────────────────────────────────────────────────────────

    /// <summary>
    /// The stated back-pressure policy: when the writer cannot keep up, staged snapshots
    /// <b>coalesce to the newest</b>, and that loses nothing.
    ///
    /// <para>It is lossless because encoding is lazy. A staged snapshot that is never
    /// claimed never runs the delta encoder, so it never advances <c>_lastSent</c>, so
    /// the next snapshot that is encoded carries every change since the last one actually
    /// sent. This test stalls a client for many ticks, releases it, and checks that the
    /// state a client would reconstruct by merging what it received equals the world —
    /// no entity left at a stale position, none missing.</para>
    ///
    /// <para>The old design was the other way round and did lose data: it encoded on the
    /// tick, advanced <c>_lastSent</c>, then handed the envelope to a bounded channel that
    /// drops the oldest under load, so those updates were gone until the next keyframe.</para>
    /// </summary>
    [Fact]
    public void StalledClient_CoalescesToNewest_AndLosesNoState()
    {
        using var rig = new Rig(players: 2, WireEncoding.Proto);

        // Establish the stream, then stall one client while the world moves on.
        rig.Tick(5);
        rig.Drain(expectedPerConn: 1);

        rig.Transports[0].Stall();
        rig.Tick(80);
        rig.Transports[0].Release();

        // Give the writer time to drain whatever survived coalescing.
        Thread.Sleep(500);
        rig.Tick(2);
        Thread.Sleep(500);

        List<SnapshotMessage> snaps = ParseSnapshots(rig.Transports[0].Written);
        Assert.NotEmpty(snaps);

        // Merge everything the client received, keyframes resetting the view.
        var merged = new Dictionary<string, (float X, float Y)>();
        var handles = new Dictionary<uint, string>();
        foreach (SnapshotMessage s in snaps)
        {
            if (s.Full) { merged.Clear(); handles.Clear(); }
            foreach (EntitySnapshot e in s.Entities)
            {
                string id = e.Id;
                if (!string.IsNullOrEmpty(id)) { if (e.Handle != 0) handles[e.Handle] = id; }
                else if (e.Handle != 0 && handles.TryGetValue(e.Handle, out string? known)) id = known;
                if (string.IsNullOrEmpty(id)) continue;
                merged[id] = (e.X, e.Y);
            }
            foreach (string removed in s.Removed) merged.Remove(removed);
        }

        // Every entity the client can see must be at its true, current position.
        rig.World.TryGetSnapshotAnchor("p0", out Vec2 anchor, out _);
        List<EntityState> truth = rig.World.GetEntitiesInRange(anchor, GameConstants.DefaultAoiRadius);

        foreach (EntityState e in truth)
        {
            Assert.True(merged.ContainsKey(e.Id), $"{e.Id} never reached the stalled client");
            Assert.Equal(e.Position.X, merged[e.Id].X, precision: 3);
            Assert.Equal(e.Position.Y, merged[e.Id].Y, precision: 3);
        }
    }

    /// <summary>
    /// A stalled client must not stall the tick. The whole point of moving encoding off
    /// the tick is that a slow socket cannot hold the simulation.
    /// </summary>
    [Fact]
    public void StalledClient_DoesNotBlockTheTickLoop()
    {
        using var rig = new Rig(players: 2, WireEncoding.Proto);
        rig.Tick(3);

        rig.Transports[0].Stall();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        rig.Tick(120);
        sw.Stop();

        rig.Transports[0].Release();

        Assert.Equal(123UL, rig.Loop.CurrentTick);
        Assert.True(sw.Elapsed.TotalSeconds < 5,
            $"120 ticks took {sw.Elapsed.TotalSeconds:F1}s with a stalled client");
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A connection closed mid-flight must not throw out of the tick, and the tick must
    /// keep running. Closing completes the send channel, so the write task ends and the
    /// staged snapshot is simply never claimed.
    /// </summary>
    [Fact]
    public void ClosingAConnectionMidBroadcastIsSafe()
    {
        using var rig = new Rig(players: 4, WireEncoding.Proto);
        rig.Tick(5);

        rig.Conns[1].Close();
        rig.Conns[2].Dispose();

        Exception? thrown = Record.Exception(() => rig.Tick(20));

        Assert.Null(thrown);
        Assert.Equal(25UL, rig.Loop.CurrentTick);
    }

    // ── Bytes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the write task actually put on the stream must equal what the reference
    /// encoder produces from the same world, byte for byte — Protobuf, including entity-id
    /// interning, whose handle numbering depends on AOI arrival order.
    /// </summary>
    [Theory]
    [InlineData(WireEncoding.Proto)]
    [InlineData(WireEncoding.Json)]
    public void ProductionBytesEqualTheReferenceEncoder(WireEncoding encoding)
    {
        const int players = 3, ticks = 45;

        using var rig = new Rig(players, encoding);
        var reference = new SnapshotDeltaState[players];
        for (int i = 0; i < players; i++)
        {
            reference[i] = new SnapshotDeltaState(SnapshotDeltaState.PhaseFor($"p{i}"));
        }

        var expected = new List<byte[]>[players];
        for (int i = 0; i < players; i++) expected[i] = new List<byte[]>();

        for (int t = 0; t < ticks; t++)
        {
            for (int i = 0; i < players; i++)
            {
                rig.World.PushInput($"p{i}", new InputData(rig.Loop.CurrentTick + 1, 1f, 0f, null));
            }
            rig.TickAndSettle(t + 1);

            for (int i = 0; i < players; i++)
            {
                rig.World.TryGetSnapshotAnchor($"p{i}", out Vec2 anchor, out ulong ack);
                List<EntityState> nearby =
                    rig.World.GetEntitiesInRange(anchor, GameConstants.DefaultAoiRadius);
                SnapshotMessage msg = reference[i].Encode(
                    rig.Loop.CurrentTick, ack, nearby, GameConstants.DefaultKeyframeInterval,
                    intern: encoding == WireEncoding.Proto);
                expected[i].Add(WireProtocol.Encode(
                    WireProtocol.NewEnvelope(MsgType.Snapshot, msg, encoding)));
            }
        }

        for (int i = 0; i < players; i++)
        {
            byte[] actual = rig.Transports[i].Written;
            byte[] want = expected[i].SelectMany(f => f).ToArray();

            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(want)),
                Convert.ToHexString(SHA256.HashData(actual)));
        }
    }
}
