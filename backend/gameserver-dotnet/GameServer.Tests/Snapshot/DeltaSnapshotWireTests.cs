using System.Text.Json;
using GameServer.Tests.Infrastructure;
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

    /// <summary>Server-side Connection plus the client end of a loopback KCP session.</summary>
    private (Connection conn, Stream clientStream) ConnectedPair(string userId)
    {
        var pair = KcpPair.Create();
        var conn = new Connection(userId, pair.ServerSide, NullLogger.Instance);
        _cleanup.Add(pair);
        _cleanup.Add(conn);
        return (conn, pair.ClientStream);
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

    /// <summary>
    /// Reads snapshot frames in the background from the moment it is created, and lets the
    /// test wait for the one it is waiting FOR rather than for a count or a clock.
    /// </summary>
    /// <remarks>
    /// <para>The reader used to be <c>ReadSnapshotsAsync(stream, count)</c>: read until
    /// <c>count</c> snapshots or a 5s deadline, whichever came first. Both stops were wrong
    /// for this suite. The count is not reachable once a lagging writer coalesces (that is
    /// the designed back-pressure), so the reader sat out its full deadline; and the 5s ran
    /// from the moment the reader was created, so it covered the whole tick-producing phase
    /// as well as the drain. Sixty or a hundred <c>Task.Delay(1)</c> iterations are about a
    /// second on a quiet host and several on a contended one, so the deadline could expire
    /// while the test was still ticking and the client then reconstructed a state several
    /// ticks old (#515: expected X=20, actual 17.9999962, which is exactly six ticks at
    /// 5 units/s and 15Hz). That is a test race, not a netcode defect: the data the client
    /// lacked had not been produced yet when the reader gave up.</para>
    /// <para>Waiting until a snapshot stamped with the final server tick has arrived is the
    /// convergence condition itself. Coalescing can drop intermediate frames but never the
    /// newest, so the wait is reachable under any load, and its budget starts only after
    /// the last tick.</para>
    /// </remarks>
    private sealed class SnapshotCollector : IDisposable
    {
        private readonly List<(SnapshotMessage msg, int payloadBytes)> _snapshots = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;

        public SnapshotCollector(Stream stream) => _pump = Task.Run(() => PumpAsync(stream));

        private async Task PumpAsync(Stream stream)
        {
            try
            {
                while (true)
                {
                    var env = await WireProtocol.DecodeAsync(stream, _cts.Token);
                    if (env == null) return;
                    if ((MsgType)env.Type != MsgType.Snapshot) continue;

                    var msg = WireProtocol.GetPayload<SnapshotMessage>(env);
                    string raw = System.Text.Encoding.UTF8.GetString(env.Payload);
                    lock (_snapshots) _snapshots.Add((msg, System.Text.Encoding.UTF8.GetByteCount(raw)));
                }
            }
            catch (OperationCanceledException) { /* disposed */ }
            catch (IOException) { /* the connection closed under the pump */ }
        }

        /// <summary>
        /// Wait until a snapshot stamped with <paramref name="finalTick"/> or later has
        /// arrived, then return everything received. Fails loudly, with what did arrive, if it
        /// never does within <paramref name="budget"/>.
        /// </summary>
        public async Task<List<(SnapshotMessage msg, int payloadBytes)>> UntilTickAsync(
            ulong finalTick, TimeSpan? budget = null)
        {
            var limit = budget ?? TimeSpan.FromSeconds(30);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                lock (_snapshots)
                {
                    if (_snapshots.Count > 0 && _snapshots[^1].msg.Tick >= finalTick)
                        return new List<(SnapshotMessage, int)>(_snapshots);
                }
                if (sw.Elapsed >= limit)
                {
                    ulong last;
                    int count;
                    lock (_snapshots)
                    {
                        count = _snapshots.Count;
                        last = count == 0 ? 0 : _snapshots[^1].msg.Tick;
                    }
                    Assert.Fail(
                        $"no snapshot for tick {finalTick} within {limit.TotalSeconds:F0}s of the last " +
                        $"tick: {count} received, newest is tick {last} ({KcpTestClient.Ambient()})");
                }
                await Task.Delay(10);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _pump.Wait(TimeSpan.FromSeconds(5)); } catch { /* teardown */ }
            _cts.Dispose();
        }
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

        using var reader = new SnapshotCollector(clientStream);

        for (int i = 1; i <= ticks; i++)
        {
            world.PushInput("p1", new InputData((ulong)i, 1f, 0f, null));
            loop.TickOnce();
            if (i == 30) world.RemoveEntity("mob0"); // despawn mid-stream
            // Let the write pump drain: the send channel is bounded (64, DropOldest),
            // so a test that ticks flat out could legitimately lose snapshots.
            await Task.Delay(1);
        }

        var snapshots = await reader.UntilTickAsync(loop.CurrentTick);

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

        using var readerA = new SnapshotCollector(streamA);
        using var readerB = new SnapshotCollector(streamB);

        for (int i = 1; i <= 5; i++)
        {
            world.PushInput("pa", new InputData((ulong)i, 1f, 0f, null));
            // pb only sends every other tick, and its ticks run behind pa's.
            if (i % 2 == 0) world.PushInput("pb", new InputData((ulong)(i / 2), 0f, 1f, null));
            loop.TickOnce();
            await Task.Delay(1);
        }

        var snapsA = await readerA.UntilTickAsync(loop.CurrentTick);
        var snapsB = await readerB.UntilTickAsync(loop.CurrentTick);

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

            using var reader = new SnapshotCollector(stream);
            for (int i = 1; i <= ticks; i++)
            {
                world.PushInput("p1", new InputData((ulong)i, 1f, 0f, null));
                loop.TickOnce();
                await Task.Delay(1);
            }
            var snaps = await reader.UntilTickAsync(loop.CurrentTick);

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
