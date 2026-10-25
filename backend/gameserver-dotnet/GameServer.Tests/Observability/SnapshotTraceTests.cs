using System.Globalization;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Tests.Infrastructure;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace GameServer.Tests.Observability;

/// <summary>
/// The opt-in snapshot delivery trace (<c>GAMESERVER_SNAPSHOT_TRACE</c>, #473): its CSV format
/// (contract C1, read by the rpg-factory testlab analyzer), what a real connection writes into
/// it, and that it costs the gather path nothing when it is off.
/// </summary>
public class SnapshotTraceTests(ITestOutputHelper output)
{
    private const int Columns = 15;

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Header_IsTheContractColumnSet()
    {
        // Pinned literally: the analyzer parses by name, and a renamed column is a broken
        // contract even when every test of this class still agrees with itself.
        Assert.Equal(
            "conn_id,user_id,world_tick,gather_us,enqueue_us,write_start_us,write_end_us,bytes," +
            "queue_depth,dropped_oldest_total,kcp_wait_snd,kcp_snd_wnd,kcp_rmt_wnd,kcp_retrans_total,kcp_rto_ms",
            SnapshotTrace.Header);
        Assert.Equal(Columns, SnapshotTrace.Header.Split(',').Length);
    }

    [Fact]
    public void Rows_AreWrittenInOrder_WithMinusOneForWhatWasNotRecorded()
    {
        var sw = new StringWriter();
        var trace = new SnapshotTrace(sw, capacity: 8, startFlusher: false);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        trace.Record(new SnapshotTraceRow
        {
            ConnId = 7, UserId = "user-a", WorldTick = 1200,
            GatherTs = t0, EnqueueTs = t0, WriteStartTs = t0, WriteEndTs = t0,
            Bytes = 512, QueueDepth = 3, DroppedOldestTotal = 9,
            Kcp = new KcpSessionCounters(4, 128, 127, 2, 30),
        });
        // A row with no gather stamp and no KCP session: every missing field is -1, never 0.
        trace.Record(new SnapshotTraceRow
        {
            ConnId = 8, UserId = "a,\"b\"", WorldTick = 1204,
            WriteStartTs = t0, WriteEndTs = t0, Bytes = 64, QueueDepth = -1,
            Kcp = KcpSessionCounters.Unavailable,
        });
        trace.Dispose();

        string[] lines = Lines(sw.ToString());
        Assert.Equal(3, lines.Length);
        Assert.Equal(SnapshotTrace.Header, lines[0]);

        string us = ProcessClock.ToMicroseconds(t0).ToString(CultureInfo.InvariantCulture);
        Assert.Equal($"7,user-a,1200,{us},{us},{us},{us},512,3,9,4,128,127,2,30", lines[1]);
        Assert.Equal($"8,\"a,\"\"b\"\"\",1204,-1,-1,{us},{us},64,-1,0,-1,-1,-1,-1,-1", lines[2]);
        Assert.Equal(2, trace.RowsWritten);
        Assert.Equal(0, trace.RowsDropped);
    }

    [Fact]
    public void AFullRing_DropsAndCounts_NeverGrows()
    {
        var sw = new StringWriter();
        var trace = new SnapshotTrace(sw, capacity: 4, startFlusher: false);
        for (int i = 0; i < 10; i++)
            trace.Record(new SnapshotTraceRow { ConnId = 1, UserId = "u", WorldTick = (ulong)i, Kcp = KcpSessionCounters.Unavailable });
        trace.FlushPending();
        // The ring empties on a flush, so recording resumes.
        trace.Record(new SnapshotTraceRow { ConnId = 1, UserId = "u", WorldTick = 99, Kcp = KcpSessionCounters.Unavailable });
        trace.Dispose();

        Assert.Equal(6, trace.RowsDropped);
        Assert.Equal(5, trace.RowsWritten);
        string[] lines = Lines(sw.ToString());
        Assert.Equal(new[] { "0", "1", "2", "3", "99" }, lines.Skip(1).Select(l => l.Split(',')[2]));
    }

    [Fact]
    public void ProcessClock_IsMonotonicMicroseconds()
    {
        long a = ProcessClock.NowMicroseconds();
        Thread.Sleep(20);
        long b = ProcessClock.NowMicroseconds();
        Assert.True(a >= 0, $"negative time since the origin: {a}");
        Assert.InRange(b - a, 15_000, 5_000_000);
    }

    /// <summary>
    /// A real connection over a loopback KCP session, gathered and written the way the tick
    /// and the write task do it: every snapshot the client receives has exactly one
    /// well-formed row, carrying the tick the client saw, the four timestamps in causal order,
    /// and real KCP counters (not -1).
    /// </summary>
    [Fact]
    public async Task ARealConnection_WritesOneWellFormedRowPerSentSnapshot()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"snapshot-trace-{Guid.NewGuid():N}.csv");
        using var pair = await KcpPair.CreateAsync();
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("traced", x: 0, y: 0));
        world.AddEntity(TestHelpers.CreatePlayer("other", x: 2, y: 2));

        var trace = SnapshotTrace.Open(path);
        var conn = new Connection("traced", pair.ServerSide, NullLogger.Instance, WireEncoding.Proto)
        {
            PeerProtocolVersion = 3,
            SnapshotTrace = trace,
        };
        Task writer = conn.WriteLoopAsync();
        var stream = pair.ClientStream;
        var received = new List<ulong>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            for (ulong tick = 4; tick <= 40; tick += 4)
            {
                world.ReadAll(reader => conn.GatherSnapshotView(reader, radius: 50f, tick: tick, keyframeInterval: 30));
                // Read each snapshot before gathering the next, so none is coalesced away and
                // the row count is exactly the frame count.
                Envelope env = (await WireProtocol.DecodeAsync(stream, cts.Token))!;
                Assert.Equal((uint)RpgMmo.Wire.V1.MsgType.Snapshot, env.Type);
                received.Add(WireProtocol.GetPayload<RpgMmo.Wire.V1.SnapshotMessage>(env).Tick);
            }
        }
        finally
        {
            conn.Close();
            await writer;
            trace.Dispose();
        }

        string[] lines = Lines(await File.ReadAllTextAsync(path));
        File.Delete(path);
        foreach (string l in lines.Take(4)) output.WriteLine(l);

        Assert.Equal(SnapshotTrace.Header, lines[0]);
        Assert.Equal(received.Count, lines.Length - 1);

        long prevWriteEnd = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            string[] f = lines[i].Split(',');
            Assert.Equal(Columns, f.Length);
            long[] v = f.Select((x, j) => j == 1 ? 0 : long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(conn.ConnectionId, v[0]);
            Assert.Equal("traced", f[1]);
            Assert.Equal((long)received[i - 1], v[2]);

            long gather = v[3], enqueue = v[4], writeStart = v[5], writeEnd = v[6];
            Assert.True(gather > 0, $"row {i}: gather_us {gather} not recorded");
            Assert.True(gather <= enqueue && enqueue <= writeStart && writeStart <= writeEnd,
                $"row {i}: times out of causal order: {gather},{enqueue},{writeStart},{writeEnd}");
            Assert.True(writeEnd >= prevWriteEnd, $"row {i}: write_end_us went backwards");
            prevWriteEnd = writeEnd;

            Assert.True(v[7] > 0, $"row {i}: bytes {v[7]}");
            Assert.InRange(v[8], 0, Connection.DataLaneCapacity);
            Assert.True(v[9] >= 0, $"row {i}: dropped_oldest_total {v[9]}");
            // Real KCP counters, not the -1 of "unavailable".
            Assert.True(v[10] >= 0, $"row {i}: kcp_wait_snd {v[10]}");
            Assert.Equal(KcpTuning.SendWindow, v[11]);
            Assert.True(v[12] >= 0, $"row {i}: kcp_rmt_wnd {v[12]}");
            Assert.True(v[13] >= 0, $"row {i}: kcp_retrans_total {v[13]}");
            Assert.True(v[14] > 0, $"row {i}: kcp_rto_ms {v[14]}");
        }
        Assert.Equal(0, trace.RowsDropped);
    }

    /// <summary>
    /// Trace off, the gather path allocates nothing; trace on, its producer side allocates
    /// nothing either (the row goes into a preallocated ring of structs). Extends the
    /// snapshot path's allocation guard (<c>SnapshotAllocationTests</c>) to the connection's
    /// half of it, which that class does not reach.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheGatherPath_AllocatesNothing_TraceOnOrOff(bool traced)
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        for (int i = 0; i < 20; i++) world.AddEntity(TestHelpers.CreatePlayer($"n{i}", x: i, y: 1));

        var sw = new StringWriter();
        using var trace = new SnapshotTrace(sw, capacity: 1024, startFlusher: false);
        var conn = new Connection("p1", new NullTransport(), NullLogger.Instance, WireEncoding.Proto)
        {
            SnapshotTrace = traced ? trace : null,
        };

        var state = new GatherState { Conn = conn };
        Action<WorldReader> gather = state.Gather; // one delegate, built outside the measurement
        bool measurePhases = false;
        long gatherBytes = 0, claimBytes = 0;

        void Round()
        {
            state.Tick += 4;
            long beforeGather = measurePhases ? GC.GetAllocatedBytesForCurrentThread() : 0;
            world.ReadAll(gather);
            long afterGather = measurePhases ? GC.GetAllocatedBytesForCurrentThread() : 0;
            conn.TakePendingSnapshot(out _, out _, out _, out _, out _, out _);
            if (measurePhases)
            {
                long afterClaim = GC.GetAllocatedBytesForCurrentThread();
                gatherBytes += afterGather - beforeGather;
                claimBytes += afterClaim - afterGather;
            }
        }

        // Repeat the original warm-up/measurement window in one process. The first window
        // preserves the CI failure shape; later windows test for intermittent allocations.
        const int ProbeTrials = 200;
        const int WarmupCalls = 100;
        long totalAllocated = 0;
        // Background GCs on purpose, not by luck of the schedule (SC-003, see ThreadAllocation).
        using var pressure = BackgroundGcPressure.Start();
        for (int trial = 0; trial < ProbeTrials; trial++)
        {
            measurePhases = false;
            // Dynamic TieredPGO can defer profile instrumentation while gather tiers up.
            // Match SnapshotAllocationTests' 100-call warm-up so measurement starts past
            // that transition, without changing the zero-allocation assertion below.
            for (int i = 0; i < WarmupCalls; i++) Round();

            gatherBytes = 0;
            claimBytes = 0;
            measurePhases = true;
            long before = ThreadAllocation.Begin();
            for (int i = 0; i < 200; i++) Round();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            long outsideGatherAndClaim = allocated - gatherBytes - claimBytes;
            totalAllocated += allocated;

            if (allocated != 0)
                output.WriteLine($"trial {trial}: total={allocated}, gather={gatherBytes}, claim={claimBytes}, outside={outsideGatherAndClaim}");
            Assert.Equal(0L, allocated);
            Assert.Equal(0L, gatherBytes);
            Assert.Equal(0L, claimBytes);
        }

        output.WriteLine($"traced={traced}: {ProbeTrials} windows x 200 gathers, total={totalAllocated} bytes, dropped_oldest={conn.SendDroppedOldest}");
        Assert.True(conn.SendDroppedOldest > 0, "the data lane never filled, so the drop path was not measured");
    }

    private sealed class GatherState
    {
        public required Connection Conn;
        public ulong Tick;
        public void Gather(WorldReader reader) => Conn.GatherSnapshotView(reader, radius: 50f, tick: Tick, keyframeInterval: 30);
    }

    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = Stream.Null;
        public string RemoteEndPoint => "trace-test";
        public void Close() { }
        public void Dispose() { }
    }
}
