using System.Diagnostics;
using System.Text;
using GameServer.Net.Transport;

namespace GameServer.Observability;

/// <summary>
/// The process's monotonic origin, so every <c>*_us</c> figure the server writes is
/// "microseconds since this process started" on one clock.
/// </summary>
/// <remarks>
/// <para><see cref="Stopwatch"/>, never <c>DateTime.UtcNow</c>: this host's realtime clock
/// runs fast and has stepped backwards (#153).</para>
///
/// <para>The origin is captured on first touch of this class. <c>Program.cs</c> touches it on
/// its first line, so "process start" means the first statement of the entry point, a few
/// milliseconds after the runtime itself started. Every reader compares these values with each
/// other (differences), never with another process's clock.</para>
/// </remarks>
public static class ProcessClock
{
    /// <summary>Stopwatch timestamp of the process's first statement.</summary>
    public static readonly long Origin = Stopwatch.GetTimestamp();

    // Integer division where the frequency allows it (1 GHz on Linux, 10 MHz on Windows):
    // exact, and immune to the overflow a "ticks * 1e6 / frequency" form hits after ~2.5 h
    // at 1 GHz.
    private static readonly long s_ticksPerUs =
        Stopwatch.Frequency % 1_000_000 == 0 ? Stopwatch.Frequency / 1_000_000 : 0;

    private static readonly double s_usPerTick = 1_000_000.0 / Stopwatch.Frequency;

    /// <summary>Microseconds from <see cref="Origin"/> to <paramref name="timestamp"/>.</summary>
    public static long ToMicroseconds(long timestamp)
    {
        long d = timestamp - Origin;
        return s_ticksPerUs > 0 ? d / s_ticksPerUs : (long)(d * s_usPerTick);
    }

    /// <summary>Microseconds since <see cref="Origin"/>, now.</summary>
    public static long NowMicroseconds() => ToMicroseconds(Stopwatch.GetTimestamp());
}

/// <summary>
/// One sent snapshot on one connection, as the write task saw it. Raw Stopwatch timestamps;
/// conversion to microseconds happens on the flush thread. 0 means "not recorded" and is
/// written as -1.
/// </summary>
public struct SnapshotTraceRow
{
    /// <summary>Process-unique connection number (<c>Connection.ConnectionId</c>).</summary>
    public long ConnId;

    /// <summary>The connection's user id. A reference, never a copy.</summary>
    public string UserId;

    /// <summary>The tick the client sees in this snapshot (<c>SnapshotMessage.Tick</c>).</summary>
    public ulong WorldTick;

    /// <summary>When the tick thread began gathering this connection's view.</summary>
    public long GatherTs;

    /// <summary>When the gathered view was published and its marker enqueued.</summary>
    public long EnqueueTs;

    /// <summary>When the write task started writing the encoded frame to the transport.</summary>
    public long WriteStartTs;

    /// <summary>When the transport write (KCP send + flush) returned.</summary>
    public long WriteEndTs;

    /// <summary>Frame bytes, envelope and length prefix included.</summary>
    public int Bytes;

    /// <summary>Items already in the data lane when this snapshot's marker was enqueued.</summary>
    public int QueueDepth;

    /// <summary>Cumulative items the data lane dropped (DropOldest) on this connection.</summary>
    public long DroppedOldestTotal;

    /// <summary>KCP session counters read at write end; all -1 when the transport is not KCP.</summary>
    public KcpSessionCounters Kcp;
}

/// <summary>
/// Opt-in snapshot delivery trace (<c>GAMESERVER_SNAPSHOT_TRACE</c> = file path, #473): one CSV
/// line per (connection, sent snapshot). Splits a client-observed arrival gap into the part
/// the server spent (gather, queue, encode, KCP send) and the part the path spent.
/// </summary>
/// <remarks>
/// <para><b>The format is a contract</b> (C1, read by the rpg-factory testlab analyzer). The
/// columns are <see cref="Header"/>; change them only together with that analyzer.</para>
///
/// <para><b>Off the tick thread, bounded.</b> Rows are recorded by each connection's write
/// task into a fixed ring of structs under a short lock — no allocation per row — and a
/// dedicated background thread formats and flushes them every
/// <see cref="FlushIntervalMs"/>. A full ring drops the NEW row and counts it
/// (<see cref="RowsDropped"/>); it never blocks a writer and never grows.</para>
///
/// <para><b>Wire bytes are untouched</b>: the trace reads timestamps and counters around the
/// write and never sees the frame.</para>
/// </remarks>
public sealed class SnapshotTrace : IDisposable
{
    /// <summary>The environment variable naming the trace file. Unset or empty = off.</summary>
    public const string EnvVar = GameServer.ServerEnv.SnapshotTrace;

    /// <summary>CSV header, the first line of every trace file.</summary>
    public const string Header =
        "conn_id,user_id,world_tick,gather_us,enqueue_us,write_start_us,write_end_us,bytes," +
        "queue_depth,dropped_oldest_total,kcp_wait_snd,kcp_snd_wnd,kcp_rmt_wnd,kcp_retrans_total,kcp_rto_ms";

    /// <summary>
    /// Rows the ring holds between flushes. 65 536 rows is ~70 s of 3 clients at 15 Hz with
    /// the flusher stalled entirely, or ~22 s at 200 clients; about 6 MB.
    /// </summary>
    public const int DefaultCapacity = 1 << 16;

    /// <summary>Flush cadence of the background thread.</summary>
    public const int FlushIntervalMs = 250;

    private readonly SnapshotTraceRow[] _ring;
    private readonly SnapshotTraceRow[] _batch;
    private readonly object _lock = new();
    private int _head;
    private int _count;
    private long _rowsDropped;
    private long _rowsWritten;
    private volatile string? _failure;

    private readonly TextWriter _writer;
    private readonly char[] _line = new char[512];
    private readonly Thread? _flusher;
    private readonly ManualResetEventSlim _stop = new(false);
    private int _disposed;

    /// <summary>The trace file's path, or null for a writer-backed (test) trace.</summary>
    public string? Path { get; }

    /// <summary>Rows dropped because the ring was full or the file failed.</summary>
    public long RowsDropped => Interlocked.Read(ref _rowsDropped);

    /// <summary>Rows written to the file so far.</summary>
    public long RowsWritten => Interlocked.Read(ref _rowsWritten);

    /// <summary>Why writing stopped (an I/O error), or null while healthy.</summary>
    public string? Failure => _failure;

    /// <summary>
    /// Create (truncate) <paramref name="path"/>, write the header and start the flusher.
    /// Throws when the file cannot be opened, so a bad path fails at startup rather than
    /// producing an empty trace nobody notices.
    /// </summary>
    public static SnapshotTrace Open(string path, int capacity = DefaultCapacity)
    {
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024)
        {
            NewLine = "\n",
        };
        return new SnapshotTrace(writer, capacity, startFlusher: true, path);
    }

    /// <summary>A trace over any writer. <paramref name="startFlusher"/> false = flush only via
    /// <see cref="FlushPending"/> and <see cref="Dispose"/> (deterministic tests).</summary>
    internal SnapshotTrace(TextWriter writer, int capacity, bool startFlusher, string? path = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        // Pin the origin before any row can be stamped. Program.cs touches it first anyway;
        // without this, a host that did not would get its origin at the first flush and
        // every earlier stamp would read negative.
        _ = ProcessClock.Origin;
        _writer = writer;
        Path = path;
        _ring = new SnapshotTraceRow[capacity];
        _batch = new SnapshotTraceRow[capacity];
        _writer.Write(Header);
        _writer.Write('\n');
        _writer.Flush();
        if (startFlusher)
        {
            _flusher = new Thread(FlushLoop) { IsBackground = true, Name = "snapshot-trace" };
            _flusher.Start();
        }
    }

    /// <summary>Record one row. Any thread; never blocks on I/O, never allocates.</summary>
    public void Record(in SnapshotTraceRow row)
    {
        lock (_lock)
        {
            if (_count == _ring.Length || _failure != null)
            {
                _rowsDropped++;
                return;
            }
            int slot = _head + _count;
            if (slot >= _ring.Length) slot -= _ring.Length;
            _ring[slot] = row;
            _count++;
        }
    }

    private void FlushLoop()
    {
        while (!_stop.Wait(FlushIntervalMs)) FlushPending();
    }

    /// <summary>Write every recorded row to the file and flush it. Flusher thread or tests.</summary>
    internal void FlushPending()
    {
        lock (_writer)
        {
            int n;
            lock (_lock)
            {
                n = _count;
                int first = Math.Min(n, _ring.Length - _head);
                Array.Copy(_ring, _head, _batch, 0, first);
                if (n > first) Array.Copy(_ring, 0, _batch, first, n - first);
                // Release the user id references the ring was holding.
                Array.Clear(_ring, _head, first);
                if (n > first) Array.Clear(_ring, 0, n - first);
                _head = (_head + n) % _ring.Length;
                _count = 0;
            }
            if (n == 0 || _failure != null)
            {
                if (_failure != null) Interlocked.Add(ref _rowsDropped, n);
                return;
            }

            try
            {
                for (int i = 0; i < n; i++) WriteRow(in _batch[i]);
                _writer.Flush();
                Interlocked.Add(ref _rowsWritten, n);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                _failure = ex.Message;
                Interlocked.Add(ref _rowsDropped, n);
            }
            Array.Clear(_batch, 0, n);
        }
    }

    private void WriteRow(in SnapshotTraceRow r)
    {
        Span<char> b = _line;
        int p = 0;
        Long(b, ref p, r.ConnId);
        b[p++] = ',';
        _writer.Write(b[..p]);
        WriteUserId(r.UserId);

        p = 0;
        b[p++] = ',';
        ULong(b, ref p, r.WorldTick); b[p++] = ',';
        Long(b, ref p, Us(r.GatherTs)); b[p++] = ',';
        Long(b, ref p, Us(r.EnqueueTs)); b[p++] = ',';
        Long(b, ref p, Us(r.WriteStartTs)); b[p++] = ',';
        Long(b, ref p, Us(r.WriteEndTs)); b[p++] = ',';
        Long(b, ref p, r.Bytes); b[p++] = ',';
        Long(b, ref p, r.QueueDepth); b[p++] = ',';
        Long(b, ref p, r.DroppedOldestTotal); b[p++] = ',';
        Long(b, ref p, r.Kcp.WaitSnd); b[p++] = ',';
        Long(b, ref p, r.Kcp.SendWindow); b[p++] = ',';
        Long(b, ref p, r.Kcp.RemoteWindow); b[p++] = ',';
        Long(b, ref p, r.Kcp.RetransmittedSegments); b[p++] = ',';
        Long(b, ref p, r.Kcp.RtoMs);
        b[p++] = '\n';
        _writer.Write(b[..p]);
    }

    /// <summary>The user id, quoted with quotes doubled only if it holds a CSV metacharacter.</summary>
    private void WriteUserId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (id.AsSpan().IndexOfAny(",\"\n\r") < 0)
        {
            _writer.Write(id);
            return;
        }
        _writer.Write('"');
        foreach (char c in id)
        {
            if (c == '"') _writer.Write('"');
            _writer.Write(c);
        }
        _writer.Write('"');
    }

    private static long Us(long timestamp) => timestamp == 0 ? -1 : ProcessClock.ToMicroseconds(timestamp);

    private static void Long(Span<char> b, ref int p, long v)
    {
        v.TryFormat(b[p..], out int w, default, System.Globalization.CultureInfo.InvariantCulture);
        p += w;
    }

    private static void ULong(Span<char> b, ref int p, ulong v)
    {
        v.TryFormat(b[p..], out int w, default, System.Globalization.CultureInfo.InvariantCulture);
        p += w;
    }

    /// <summary>Stop the flusher, write what is left and close the file. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        _flusher?.Join(TimeSpan.FromSeconds(5));
        FlushPending();
        try { _writer.Dispose(); } catch (IOException) { /* best effort at shutdown */ }
        _stop.Dispose();
    }
}
