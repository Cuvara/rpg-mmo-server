using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Input;
using GameServer.Net;
using GameServer.Server;
using GameServer.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using Xunit.Abstractions;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// End-to-end netcode tests: run the real tick loop over a real TCP connection, decode
/// the frames a client would see, and reconstruct state with the shared
/// <see cref="SnapshotMerger"/>. Also measures snapshot bandwidth delta-vs-full.
/// </summary>
public class DeltaSnapshotWireTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<IDisposable> _cleanup = new();

    public DeltaSnapshotWireTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        for (int i = _cleanup.Count - 1; i >= 0; i--)
        {
            try { _cleanup[i].Dispose(); } catch { /* teardown */ }
        }
    }

    /// <summary>Server-side Connection plus the client socket on the other end of loopback.</summary>
    private (Connection conn, NetworkStream clientStream) ConnectedPair(string userId)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        var serverSide = listener.AcceptTcpClient();
        listener.Stop();

        var conn = new Connection(userId, serverSide, NullLogger.Instance);
        _cleanup.Add(conn);
        _cleanup.Add(client);
        return (conn, client.GetStream());
    }

    private static (TickLoop loop, EcsWorld world, ConnectionManager connections) BuildLoop(
        int keyframeInterval)
    {
        var world = new EcsWorld();
        var connections = new ConnectionManager();
        var handler = new InputHandler(world, NullLogger.Instance, null, GameConstants.DefaultTickRate);
        var loop = new TickLoop(world, handler, connections, GameConstants.DefaultTickRate,
            GameConstants.DefaultAoiRadius, NullLogger.Instance, null, keyframeInterval);
        return (loop, world, connections);
    }

    private static async Task<List<(SnapshotMessage msg, int payloadBytes)>> ReadSnapshotsAsync(
        NetworkStream stream, int count)
    {
        var result = new List<(SnapshotMessage, int)>(count);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (result.Count < count)
            {
                var env = await WireProtocol.DecodeAsync(stream, cts.Token);
                if (env == null) break;
                if ((MsgType)env.Type != MsgType.Snapshot) continue;

                var msg = WireProtocol.GetPayload<SnapshotMessage>(env);
                string raw = System.Text.Encoding.UTF8.GetString(env.Payload);
                result.Add((msg, System.Text.Encoding.UTF8.GetByteCount(raw)));
            }
        }
        catch (OperationCanceledException)
        {
            // Fewer snapshots than ticks is legitimate: a writer that cannot keep up
            // coalesces staged snapshots to the newest. Return what arrived.
        }
        return result;
    }

    /// <summary>
    /// The whole point of the delta protocol: a client that applies deltas onto the last
    /// keyframe must hold exactly the state the server holds after N ticks.
    /// </summary>
    [Fact]
    public async Task ClientApplyingDeltas_ReconstructsServerStateExactly()
    {
        const int ticks = 60;
        var (loop, world, connections) = BuildLoop(GameConstants.DefaultKeyframeInterval);
        using var worldScope = world;

        var (conn, clientStream) = ConnectedPair("p1");
        connections.Add(conn);
        var writePump = conn.WriteLoopAsync();

        world.AddEntity(TestHelpers.CreatePlayer("p1", 0, 0, speed: 5f));
        for (int i = 0; i < 8; i++) world.AddEntity(TestHelpers.CreateMob($"mob{i}", 3 + i, 4));
        // Far mob: outside the AOI radius, must never appear.
        world.AddEntity(TestHelpers.CreateMob("mob_far", GameConstants.DefaultAoiRadius * 4, 0));

        var reader = ReadSnapshotsAsync(clientStream, ticks);

        for (int i = 1; i <= ticks; i++)
        {
            world.PushInput("p1", new InputData((ulong)i, 1f, 0f, null));
            loop.TickOnce();
            if (i == 30) world.RemoveEntity("mob0"); // despawn mid-stream
            // Let the write pump drain: the send channel is bounded (64, DropOldest),
            // so a test that ticks flat out could legitimately lose snapshots.
            await Task.Delay(1);
        }

        var snapshots = await reader;

        // Not `== ticks`. Since snapshot encoding moved to the connection's write task,
        // a writer that lags coalesces staged snapshots to the newest, so ticks and
        // snapshots are no longer 1:1 under load. That is the designed back-pressure and
        // it is lossless — which is exactly what the reconstruction below proves, and is
        // a stronger statement than counting frames. The old shape kept the count at
        // `ticks` by queueing already-encoded envelopes, so a lagging client received a
        // backlog of stale positions instead of fewer fresh ones.
        Assert.InRange(snapshots.Count, 1, ticks);

        var merger = new SnapshotMerger();
        foreach (var (msg, _) in snapshots)
        {
            merger.Apply(SnapshotDeltaStateTests.ToData(msg));
        }

        // Compare against the server's authoritative AOI set for p1.
        var playerPos = world.GetEntity("p1")!.Value.Position;
        var expected = world.GetEntitiesInRange(playerPos, GameConstants.DefaultAoiRadius);

        Assert.Equal(expected.Count, merger.Count);
        foreach (var e in expected)
        {
            Assert.True(merger.TryGet(e.Id, out var got), $"client is missing {e.Id}");
            Assert.Equal(e.Position.X, got.X);
            Assert.Equal(e.Position.Y, got.Y);
            Assert.Equal(e.Hp, got.Hp);
            Assert.Equal(e.MaxHp, got.MaxHp);
            Assert.Equal(e.Type, got.Type);
        }
        Assert.False(merger.TryGet("mob0", out _), "despawned entity survived on the client");
        Assert.False(merger.TryGet("mob_far", out _), "entity outside AOI leaked to the client");

        // Every input was accepted, so the last ack must be the last input tick.
        Assert.Equal((ulong)ticks, snapshots[^1].msg.AckTick);
        Assert.True(merger.Keyframes >= 2, "expected join keyframe plus at least one periodic one");
        Assert.True(merger.Deltas > 0);

        conn.Close();
        await writePump;
    }

    /// <summary>ack_tick is the receiving player's own last accepted input tick, nobody else's.</summary>
    [Fact]
    public async Task AckTick_IsPerPlayer()
    {
        var (loop, world, connections) = BuildLoop(GameConstants.DefaultKeyframeInterval);
        using var worldScope = world;

        var (connA, streamA) = ConnectedPair("pa");
        var (connB, streamB) = ConnectedPair("pb");
        connections.Add(connA);
        connections.Add(connB);
        var pumpA = connA.WriteLoopAsync();
        var pumpB = connB.WriteLoopAsync();

        world.AddEntity(TestHelpers.CreatePlayer("pa", 0, 0, speed: 5f));
        world.AddEntity(TestHelpers.CreatePlayer("pb", 1, 0, speed: 5f));

        var readerA = ReadSnapshotsAsync(streamA, 5);
        var readerB = ReadSnapshotsAsync(streamB, 5);

        for (int i = 1; i <= 5; i++)
        {
            world.PushInput("pa", new InputData((ulong)i, 1f, 0f, null));
            // pb only sends every other tick, and its ticks run behind pa's.
            if (i % 2 == 0) world.PushInput("pb", new InputData((ulong)(i / 2), 0f, 1f, null));
            loop.TickOnce();
            await Task.Delay(1);
        }

        var snapsA = await readerA;
        var snapsB = await readerB;

        Assert.Equal(5u, snapsA[^1].msg.AckTick);
        Assert.Equal(2u, snapsB[^1].msg.AckTick);

        connA.Close();
        connB.Close();
        await pumpA;
        await pumpB;
    }

    /// <summary>
    /// Bandwidth evidence: identical simulation, delta encoding vs full snapshots every tick.
    /// </summary>
    [Fact]
    public async Task DeltaEncoding_UsesLessBandwidthThanFullSnapshots()
    {
        const int ticks = 100;

        async Task<(int total, int count)> RunAsync(int keyframeInterval)
        {
            var (loop, world, connections) = BuildLoop(keyframeInterval);
            using var worldScope = world;
            var (conn, stream) = ConnectedPair("p1");
            connections.Add(conn);
            var pump = conn.WriteLoopAsync();

            world.AddEntity(TestHelpers.CreatePlayer("p1", 0, 0, speed: 5f));
            for (int i = 0; i < 8; i++) world.AddEntity(TestHelpers.CreateMob($"mob{i}", 3 + i, 4));

            var reader = ReadSnapshotsAsync(stream, ticks);
            for (int i = 1; i <= ticks; i++)
            {
                world.PushInput("p1", new InputData((ulong)i, 1f, 0f, null));
                loop.TickOnce();
                await Task.Delay(1);
            }
            var snaps = await reader;

            conn.Close();
            await pump;
            return (snaps.Sum(s => s.payloadBytes), snaps.Count);
        }

        var full = await RunAsync(0);   // 0 = delta disabled, full snapshot every tick
        var delta = await RunAsync(GameConstants.DefaultKeyframeInterval);

        // Not `== ticks`. Since encoding moved to the connection's write task, a writer
        // that lags coalesces staged snapshots to the newest, so the two runs can deliver
        // slightly different counts. Comparing raw totals across runs of different length
        // would then measure the coalescing rather than the encoding, which is why the
        // comparison below is per snapshot.
        Assert.InRange(full.count, ticks / 2, ticks);
        Assert.InRange(delta.count, ticks / 2, ticks);

        double fullPer = full.total / (double)full.count;
        double deltaPer = delta.total / (double)delta.count;

        _out.WriteLine($"1 moving player + 8 static mobs, {ticks} ticks @15Hz:");
        _out.WriteLine($"  full  : {full.total} B over {full.count} snapshots, {fullPer:F1} B/snapshot");
        _out.WriteLine($"  delta : {delta.total} B over {delta.count} snapshots, {deltaPer:F1} B/snapshot");
        _out.WriteLine($"  saving: {100.0 * (fullPer - deltaPer) / fullPer:F1}%");

        Assert.True(deltaPer < fullPer / 2,
            $"delta ({deltaPer:F1} B/snapshot) should be well under half of full ({fullPer:F1} B/snapshot)");
    }
}
