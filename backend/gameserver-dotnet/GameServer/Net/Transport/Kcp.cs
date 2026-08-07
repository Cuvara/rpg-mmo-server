using System.Buffers.Binary;

namespace GameServer.Net.Transport;

/// <summary>
/// A KCP (ARQ) state machine, ported from <c>github.com/xtaci/kcp-go/v5</c>'s
/// <c>kcp.go</c> — itself a port of the reference <c>ikcp.c</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is pure protocol: no sockets, no threads. Bytes arrive via
/// <see cref="Input"/>, leave via the <c>output</c> callback, and the caller is
/// responsible for driving <see cref="Update"/> on a timer. That is the same
/// split kcp-go uses, and it is what makes the implementation testable without a
/// network.
/// </para>
/// <para>
/// Why a port rather than a library: the hard requirement is bytes-on-the-wire
/// compatibility with kcp-go, and the mature C# option (kcp2k, from Mirror) is
/// deliberately not wire-compatible — it prepends its own channel byte, runs a
/// hello/cookie handshake kcp-go knows nothing about, and reserves conv 0. See
/// <c>docs/DESIGN.md</c> for the full evaluation. The protocol itself is small
/// and frozen, so porting it is a smaller and far more verifiable commitment
/// than bending a foreign handshake into shape.
/// </para>
/// <para>
/// Deviations from kcp-go are limited to data structures (plain lists instead of
/// ring buffers and a heap) and the removal of FEC, SNMP counters and trace
/// logging. None of those are observable on the wire. FEC is off on the Go side
/// too (<c>KCPDataShards = 0</c>), so a kcp-go peer configured by
/// <c>backend/shared/transport</c> never emits an FEC header.
/// </para>
/// </remarks>
public sealed class Kcp
{
    /// <summary>Per-segment header size: conv(4) cmd(1) frg(1) wnd(2) ts(4) sn(4) una(4) len(4).</summary>
    public const int Overhead = 24;

    // Command types in the segment header.
    private const byte CmdPush = 81;
    private const byte CmdAck = 82;
    private const byte CmdWask = 83;
    private const byte CmdWins = 84;

    // Probe flags.
    private const uint AskSend = 1;
    private const uint AskTell = 2;

    // RTO bounds (ms).
    private const uint RtoNdl = 30;
    private const uint RtoMin = 100;
    private const uint RtoDef = 200;
    private const uint RtoMax = 60000;

    private const uint WndSnd = 32;
    private const uint WndRcv = 32;
    private const int MtuDef = 1400;
    private const uint Interval0 = 100;
    private const uint DeadLink = 20;
    private const uint ThreshInit = 2;
    private const uint ThreshMin = 2;
    private const uint ProbeInit = 500;
    private const uint ProbeLimit = 120000;

    /// <summary>Flush selector, mirroring kcp-go's <c>FlushType</c>.</summary>
    private enum FlushType
    {
        /// <summary>Emit queued ACKs only — used as a low-latency ACK clock.</summary>
        AckOnly,
        /// <summary>Emit ACKs, probes and data, including retransmissions.</summary>
        Full
    }

    /// <summary>One KCP segment: header fields plus its payload.</summary>
    private sealed class Segment
    {
        public uint Conv;
        public byte Cmd;
        public byte Frg;
        public ushort Wnd;
        public uint Ts;
        public uint Sn;
        public uint Una;
        public uint Rto;
        public uint Xmit;
        public uint Resendts;
        public uint Fastack;
        public bool Acked;
        public byte[] Data = [];

        public void EncodeHeader(Span<byte> ptr)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(ptr, Conv);
            ptr[4] = Cmd;
            ptr[5] = Frg;
            BinaryPrimitives.WriteUInt16LittleEndian(ptr[6..], Wnd);
            BinaryPrimitives.WriteUInt32LittleEndian(ptr[8..], Ts);
            BinaryPrimitives.WriteUInt32LittleEndian(ptr[12..], Sn);
            BinaryPrimitives.WriteUInt32LittleEndian(ptr[16..], Una);
            BinaryPrimitives.WriteUInt32LittleEndian(ptr[20..], (uint)Data.Length);
        }
    }

    private readonly record struct AckItem(uint Sn, uint Ts);

    private readonly uint _conv;
    private readonly Action<byte[], int> _output;

    private uint _mtu = MtuDef;
    private uint _mss = MtuDef - Overhead;
    private uint _state;

    private uint _sndUna, _sndNxt, _rcvNxt;
    private uint _ssthresh = ThreshInit;
    private int _rxRttvar, _rxSrtt;
    private uint _rxRto = RtoDef, _rxMinrto = RtoMin;
    private uint _sndWnd = WndSnd, _rcvWnd = WndRcv, _rmtWnd = WndRcv;
    private uint _cwnd, _incr;
    private uint _probe, _tsProbe, _probeWait;
    private uint _interval = Interval0, _tsFlush = Interval0;
    private uint _nodelay, _updated;
    private readonly uint _deadLink = DeadLink;
    private int _fastresend;
    private int _nocwnd;

    private readonly List<Segment> _sndQueue = [];
    private readonly List<Segment> _sndBuf = [];
    private readonly List<Segment> _rcvQueue = [];
    /// <summary>Out-of-order receive buffer, kept sorted ascending by sn.</summary>
    private readonly List<Segment> _rcvBuf = [];
    private readonly List<AckItem> _acklist = [];

    private byte[] _buffer;

    /// <summary>Stream mode: 1 concatenates writes across segments, 0 preserves message boundaries.</summary>
    public int Stream { get; set; }

    /// <summary>True once the peer has failed <see cref="DeadLink"/> retransmissions of a segment.</summary>
    public bool DeadLinkReached => _state == 0xFFFFFFFFu;

    /// <summary>The conversation id both peers must agree on.</summary>
    public uint Conv => _conv;

    /// <summary>Current flush interval in milliseconds.</summary>
    public uint IntervalMs => _interval;

    /// <summary>
    /// Creates a state machine for conversation <paramref name="conv"/>. The
    /// <paramref name="output"/> callback receives a datagram to put on the wire;
    /// it is called synchronously from <see cref="Input"/>, <see cref="Update"/>
    /// and <see cref="Flush"/>, and only the first <c>size</c> bytes are valid.
    /// </summary>
    public Kcp(uint conv, Action<byte[], int> output)
    {
        _conv = conv;
        _output = output;
        _buffer = new byte[(_mtu + Overhead) * 3];
    }

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Monotonic millisecond clock. Only differences within one peer are ever
    /// compared, so the epoch does not need to agree across the wire — kcp-go
    /// uses process start for the same reason.
    /// </summary>
    private static uint CurrentMs() => (uint)Clock.ElapsedMilliseconds;

    /// <summary>Wrap-safe timestamp comparison (kcp-go's <c>_itimediff</c>).</summary>
    private static int Diff(uint later, uint earlier) => (int)(later - earlier);

    // ── Configuration ────────────────────────────────────────────────────────

    /// <summary>Applies the nodelay/interval/resend/nc tuning tuple.</summary>
    public void NoDelay(int nodelay, int interval, int resend, int nc)
    {
        if (nodelay >= 0)
        {
            _nodelay = (uint)nodelay;
            _rxMinrto = nodelay != 0 ? RtoNdl : RtoMin;
        }
        if (interval >= 0)
        {
            if (interval > 5000) interval = 5000;
            else if (interval < 10) interval = 10;
            _interval = (uint)interval;
        }
        if (resend >= 0) _fastresend = resend;
        if (nc >= 0) _nocwnd = nc;
    }

    /// <summary>Sets the send and receive window sizes, in packets.</summary>
    public void WndSize(int sndwnd, int rcvwnd)
    {
        if (sndwnd > 0) _sndWnd = (uint)sndwnd;
        if (rcvwnd > 0) _rcvWnd = (uint)rcvwnd;
    }

    /// <summary>Sets the MTU (excluding UDP/IP headers). Returns false if it is too small to hold a header.</summary>
    public bool SetMtu(int mtu)
    {
        if (mtu <= Overhead) return false;
        _mtu = (uint)mtu;
        _mss = _mtu - Overhead;
        _buffer = new byte[(mtu + Overhead) * 3];
        return true;
    }

    /// <summary>Number of packets queued or in flight.</summary>
    public int WaitSnd => _sndBuf.Count + _sndQueue.Count;

    // ── Application data in and out ──────────────────────────────────────────

    /// <summary>Bytes of the next complete message, or -1 when nothing is ready.</summary>
    public int PeekSize()
    {
        if (_rcvQueue.Count == 0) return -1;
        var seg = _rcvQueue[0];
        if (seg.Frg == 0) return seg.Data.Length;
        if (_rcvQueue.Count < seg.Frg + 1) return -1;

        int length = 0;
        foreach (var s in _rcvQueue)
        {
            length += s.Data.Length;
            if (s.Frg == 0) break;
        }
        return length;
    }

    /// <summary>
    /// Copies the next complete message into <paramref name="buffer"/>.
    /// Returns the byte count, -1 when nothing is ready, or -2 when the buffer is
    /// too small for the pending message.
    /// </summary>
    public int Recv(Span<byte> buffer)
    {
        int peeksize = PeekSize();
        if (peeksize < 0) return -1;
        if (peeksize > buffer.Length) return -2;

        bool fastRecover = _rcvQueue.Count >= (int)_rcvWnd;

        int n = 0;
        while (_rcvQueue.Count > 0)
        {
            var seg = _rcvQueue[0];
            _rcvQueue.RemoveAt(0);
            seg.Data.CopyTo(buffer[n..]);
            n += seg.Data.Length;
            if (seg.Frg == 0) break;
        }

        MoveRcvBufToQueue();

        // Window has re-opened after being full: tell the peer promptly rather than
        // waiting for it to probe, which would cost a full probe timeout.
        if (_rcvQueue.Count < (int)_rcvWnd && fastRecover) _probe |= AskTell;
        return n;
    }

    /// <summary>
    /// Queues application bytes for transmission, fragmenting across segments.
    /// Returns 0 on success, -1 for an empty buffer, -2 when the payload needs
    /// more than 255 fragments.
    /// </summary>
    public int Send(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0) return -1;

        // Stream mode: top up the last queued segment before starting a new one, so
        // a burst of small writes does not become a burst of small datagrams.
        if (Stream != 0 && _sndQueue.Count > 0)
        {
            var last = _sndQueue[^1];
            if (last.Data.Length < (int)_mss)
            {
                int capacity = (int)_mss - last.Data.Length;
                int extend = Math.Min(buffer.Length, capacity);
                var grown = new byte[last.Data.Length + extend];
                last.Data.CopyTo(grown, 0);
                buffer[..extend].CopyTo(grown.AsSpan(last.Data.Length));
                last.Data = grown;
                buffer = buffer[extend..];
            }
            if (buffer.Length == 0) return 0;
        }

        int count = buffer.Length <= (int)_mss
            ? 1
            : (buffer.Length + (int)_mss - 1) / (int)_mss;
        if (count > 255) return -2;
        if (count == 0) count = 1;

        for (int i = 0; i < count; i++)
        {
            int size = Math.Min(buffer.Length, (int)_mss);
            var seg = new Segment { Data = buffer[..size].ToArray() };
            seg.Frg = Stream == 0 ? (byte)(count - i - 1) : (byte)0;
            _sndQueue.Add(seg);
            buffer = buffer[size..];
        }
        return 0;
    }

    // ── ACK / RTT bookkeeping ────────────────────────────────────────────────

    private void UpdateAck(int rtt)
    {
        if (_rxSrtt == 0)
        {
            _rxSrtt = rtt;
            _rxRttvar = rtt >> 1;
        }
        else
        {
            int delta = rtt - _rxSrtt;
            _rxSrtt += delta >> 3;
            if (delta < 0) delta = -delta;
            // A sample far below the expected range gets an 8x reduced weight, so a
            // single fast ACK cannot collapse the RTO and cause spurious retransmits.
            if (rtt < _rxSrtt - _rxRttvar) _rxRttvar += (delta - _rxRttvar) >> 5;
            else _rxRttvar += (delta - _rxRttvar) >> 2;
        }
        uint rto = (uint)_rxSrtt + Math.Max(_interval, (uint)_rxRttvar << 2);
        _rxRto = Math.Min(Math.Max(_rxMinrto, rto), RtoMax);
    }

    private void ShrinkBuf() => _sndUna = _sndBuf.Count > 0 ? _sndBuf[0].Sn : _sndNxt;

    private void ParseAck(uint sn)
    {
        if (Diff(sn, _sndUna) < 0 || Diff(sn, _sndNxt) >= 0) return;
        foreach (var seg in _sndBuf)
        {
            if (sn == seg.Sn)
            {
                // Mark rather than remove: the segment is dropped when una advances
                // past it, which avoids shifting everything behind it.
                seg.Acked = true;
                seg.Data = [];
                break;
            }
            if (Diff(sn, seg.Sn) < 0) break;
        }
    }

    private bool ParseFastack(uint sn, uint ts)
    {
        bool shouldFastAck = false;
        if (Diff(sn, _sndUna) < 0 || Diff(sn, _sndNxt) >= 0) return false;

        foreach (var seg in _sndBuf)
        {
            if (Diff(sn, seg.Sn) < 0) break;
            if (sn != seg.Sn && Diff(seg.Ts, ts) <= 0)
            {
                if (seg.Fastack != 0xFFFFFFFFu)
                {
                    seg.Fastack++;
                    if (seg.Fastack >= (uint)_fastresend) shouldFastAck = true;
                }
            }
        }
        return shouldFastAck;
    }

    private int ParseUna(uint una)
    {
        int count = 0;
        foreach (var seg in _sndBuf)
        {
            if (Diff(una, seg.Sn) > 0) count++;
            else break;
        }
        if (count > 0) _sndBuf.RemoveRange(0, count);
        return count;
    }

    private void MoveRcvBufToQueue()
    {
        while (_rcvBuf.Count > 0)
        {
            var seg = _rcvBuf[0];
            if (seg.Sn != _rcvNxt || _rcvQueue.Count >= (int)_rcvWnd) break;
            _rcvBuf.RemoveAt(0);
            _rcvQueue.Add(seg);
            _rcvNxt++;
        }
    }

    /// <summary>Inserts a received data segment in sn order; returns true if it was a duplicate.</summary>
    private bool ParseData(Segment newseg)
    {
        uint sn = newseg.Sn;
        if (Diff(sn, _rcvNxt + _rcvWnd) >= 0 || Diff(sn, _rcvNxt) < 0) return true;

        int insertAt = _rcvBuf.Count;
        bool repeat = false;
        for (int i = _rcvBuf.Count - 1; i >= 0; i--)
        {
            if (_rcvBuf[i].Sn == sn) { repeat = true; break; }
            if (Diff(sn, _rcvBuf[i].Sn) > 0) { insertAt = i + 1; break; }
            insertAt = i;
        }

        if (!repeat) _rcvBuf.Insert(insertAt, newseg);
        MoveRcvBufToQueue();
        return repeat;
    }

    // ── Wire input ───────────────────────────────────────────────────────────

    /// <summary>
    /// Feeds one decrypted datagram (which may contain several concatenated
    /// segments) into the state machine. Returns 0 on success, or a negative
    /// value when the datagram is malformed or belongs to another conversation.
    /// </summary>
    /// <param name="ackNoDelay">
    /// Flush pending ACKs immediately instead of at the next interval. The game
    /// profile turns this on: at a 10-15Hz tick an ACK delayed by a flush interval
    /// is a directly observable RTT increase.
    /// </param>
    public int Input(ReadOnlySpan<byte> data, bool ackNoDelay)
    {
        uint prevUna = _sndUna;
        if (data.Length < Overhead) return -1;

        uint latest = 0;
        bool updateRtt = false;
        bool flushSegments = false;

        while (true)
        {
            if (data.Length < Overhead) break;

            uint conv = BinaryPrimitives.ReadUInt32LittleEndian(data);
            byte cmd = data[4];
            byte frg = data[5];
            ushort wnd = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            uint ts = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            uint sn = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            uint una = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
            data = data[Overhead..];

            if (conv != _conv) return -1;
            if ((uint)data.Length < length) return -2;
            if (cmd != CmdPush && cmd != CmdAck && cmd != CmdWask && cmd != CmdWins) return -3;

            _rmtWnd = wnd;
            if (ParseUna(una) > 0) flushSegments = true;
            ShrinkBuf();

            switch (cmd)
            {
                case CmdAck:
                    ParseAck(sn);
                    if (ParseFastack(sn, ts)) flushSegments = true;
                    updateRtt = true;
                    latest = ts;
                    break;

                case CmdPush:
                    if (Diff(sn, _rcvNxt + _rcvWnd) < 0)
                    {
                        _acklist.Add(new AckItem(sn, ts));
                        if (Diff(sn, _rcvNxt) >= 0)
                        {
                            ParseData(new Segment
                            {
                                Conv = conv, Cmd = cmd, Frg = frg, Wnd = wnd,
                                Ts = ts, Sn = sn, Una = una,
                                Data = data[..(int)length].ToArray()
                            });
                        }
                    }
                    break;

                case CmdWask:
                    _probe |= AskTell;
                    break;

                case CmdWins:
                    break;
            }

            data = data[(int)length..];
        }

        if (updateRtt)
        {
            uint current = CurrentMs();
            if (Diff(current, latest) >= 0) UpdateAck(Diff(current, latest));
        }

        // Reno-style cwnd growth on cumulative-ACK progress. Skipped entirely when
        // congestion control is disabled, which is what the game profile does.
        if (_nocwnd == 0 && Diff(_sndUna, prevUna) > 0 && _cwnd < _rmtWnd)
        {
            uint mss = _mss;
            if (_cwnd < _ssthresh)
            {
                _cwnd++;
                _incr += mss;
            }
            else
            {
                if (_incr < mss) _incr = mss;
                _incr += (mss * mss) / _incr + (mss / 16);
                if ((_cwnd + 1) * mss <= _incr) _cwnd = mss > 0 ? (_incr + mss - 1) / mss : _incr + mss - 1;
            }
            if (_cwnd > _rmtWnd)
            {
                _cwnd = _rmtWnd;
                _incr = _rmtWnd * mss;
            }
        }

        if (flushSegments) Flush(FlushType.Full);
        else if (_acklist.Count >= (int)(_mtu / Overhead)) Flush(FlushType.AckOnly);
        else if (ackNoDelay && _acklist.Count > 0) Flush(FlushType.AckOnly);

        return 0;
    }

    private ushort WndUnused() =>
        _rcvQueue.Count < (int)_rcvWnd ? (ushort)((int)_rcvWnd - _rcvQueue.Count) : (ushort)0;

    // ── Flush ────────────────────────────────────────────────────────────────

    /// <summary>Emits any pending ACKs, probes and (re)transmissions.</summary>
    public uint Flush() => Flush(FlushType.Full);

    private uint Flush(FlushType flushType)
    {
        var seg = new Segment
        {
            Conv = _conv,
            Cmd = CmdAck,
            Wnd = WndUnused(),
            Una = _rcvNxt
        };

        var buffer = _buffer;
        int used = 0;

        // Datagrams are collected here and handed to the output callback only after
        // the send buffer has been walked. Emitting mid-walk would let the callback
        // re-enter this instance (a loopback peer, or any synchronous transport) and
        // mutate the very list being enumerated.
        var pending = new List<byte[]>(2);

        // Closes off the current datagram when the next write would overflow the MTU.
        // KCP packs several segments into one datagram; this is where that packing ends.
        void MakeSpace(int space)
        {
            if (used + space > (int)_mtu)
            {
                pending.Add(buffer[..used]);
                used = 0;
            }
        }

        // Phase 1: pending ACKs.
        if (flushType is FlushType.AckOnly or FlushType.Full)
        {
            for (int i = 0; i < _acklist.Count; i++)
            {
                MakeSpace(Overhead);
                // Drop ACKs the peer has already cumulatively covered, except the last
                // one — it is what unblocks a peer waiting on a single confirmation.
                if (Diff(_acklist[i].Sn, _rcvNxt) >= 0 || i == _acklist.Count - 1)
                {
                    seg.Sn = _acklist[i].Sn;
                    seg.Ts = _acklist[i].Ts;
                    seg.EncodeHeader(buffer.AsSpan(used));
                    used += Overhead;
                }
            }
            _acklist.Clear();
        }

        // Phase 2: schedule a window probe while the peer advertises a zero window.
        if (_rmtWnd == 0)
        {
            uint now = CurrentMs();
            if (_probeWait == 0)
            {
                _probeWait = ProbeInit;
                _tsProbe = now + _probeWait;
            }
            else if (Diff(now, _tsProbe) >= 0)
            {
                if (_probeWait < ProbeInit) _probeWait = ProbeInit;
                _probeWait += _probeWait / 2;
                if (_probeWait > ProbeLimit) _probeWait = ProbeLimit;
                _tsProbe = now + _probeWait;
                _probe |= AskSend;
            }
        }
        else
        {
            _tsProbe = 0;
            _probeWait = 0;
        }

        // Phase 3: emit the probe commands.
        if ((_probe & AskSend) != 0)
        {
            seg.Cmd = CmdWask;
            MakeSpace(Overhead);
            seg.EncodeHeader(buffer.AsSpan(used));
            used += Overhead;
        }
        if ((_probe & AskTell) != 0)
        {
            seg.Cmd = CmdWins;
            MakeSpace(Overhead);
            seg.EncodeHeader(buffer.AsSpan(used));
            used += Overhead;
        }
        _probe = 0;

        // Phase 4: slide the send window.
        uint cwnd = Math.Min(_sndWnd, _rmtWnd);
        if (_nocwnd == 0) cwnd = Math.Min(_cwnd, cwnd);

        int newSegsCount = 0;
        while (Diff(_sndNxt, _sndUna + cwnd) < 0 && _sndQueue.Count > 0)
        {
            var newseg = _sndQueue[0];
            _sndQueue.RemoveAt(0);
            newseg.Conv = _conv;
            newseg.Cmd = CmdPush;
            newseg.Sn = _sndNxt;
            _sndBuf.Add(newseg);
            _sndNxt++;
            newSegsCount++;
        }

        uint resent = _fastresend > 0 ? (uint)_fastresend : 0xFFFFFFFFu;

        // Phase 5: (re)transmit from the send buffer.
        uint current = CurrentMs();
        ulong change = 0, lostSegs = 0;
        uint nextUpdate = _interval;

        if (flushType == FlushType.Full)
        {
            foreach (var segment in _sndBuf)
            {
                if (segment.Acked) continue;

                bool needsend = false;
                if (segment.Xmit == 0)
                {
                    needsend = true;
                    segment.Rto = _rxRto;
                    segment.Resendts = current + segment.Rto;
                }
                else if (segment.Fastack >= resent && segment.Fastack != 0xFFFFFFFFu)
                {
                    needsend = true;
                    segment.Fastack = 0xFFFFFFFFu; // wait for an RTO before counting again
                    segment.Rto = _rxRto;
                    segment.Resendts = current + segment.Rto;
                    change++;
                }
                else if (segment.Fastack > 0 && segment.Fastack != 0xFFFFFFFFu && newSegsCount == 0)
                {
                    // Early retransmit: some ACK progress but not enough duplicates, and
                    // nothing new to send that could trigger more.
                    needsend = true;
                    segment.Fastack = 0xFFFFFFFFu;
                    segment.Rto = _rxRto;
                    segment.Resendts = current + segment.Rto;
                    change++;
                }
                else if (Diff(current, segment.Resendts) >= 0)
                {
                    needsend = true;
                    segment.Rto += _nodelay == 0 ? _rxRto : _rxRto / 2;
                    segment.Fastack = 0;
                    segment.Resendts = current + segment.Rto;
                    lostSegs++;
                }

                if (needsend)
                {
                    current = CurrentMs();
                    segment.Xmit++;
                    segment.Ts = current;
                    segment.Wnd = seg.Wnd;
                    segment.Una = seg.Una;

                    MakeSpace(Overhead + segment.Data.Length);
                    segment.EncodeHeader(buffer.AsSpan(used));
                    used += Overhead;
                    segment.Data.CopyTo(buffer.AsSpan(used));
                    used += segment.Data.Length;

                    if (segment.Xmit >= _deadLink) _state = 0xFFFFFFFFu;
                }

                int rto = Diff(segment.Resendts, current);
                if (rto > 0 && (uint)rto < nextUpdate) nextUpdate = (uint)rto;
            }
        }

        if (used > 0) pending.Add(buffer[..used]);

        // Phase 6: congestion window response to loss.
        if (_nocwnd == 0)
        {
            if (change > 0)
            {
                uint inflight = _sndNxt - _sndUna;
                _ssthresh = Math.Max(inflight / 2, ThreshMin);
                _cwnd = _ssthresh + resent;
                _incr = _cwnd * _mss;
            }
            if (lostSegs > 0)
            {
                _ssthresh = Math.Max(cwnd / 2, ThreshMin);
                _cwnd = 1;
                _incr = _mss;
            }
            if (_cwnd < 1)
            {
                _cwnd = 1;
                _incr = _mss;
            }
        }

        // Every state update is complete before anything leaves: a callback that
        // synchronously feeds a reply back into Input finds a consistent machine.
        foreach (var datagram in pending) _output(datagram, datagram.Length);

        return nextUpdate;
    }

    /// <summary>
    /// Drives the timer-based half of the protocol. Call every 10-100ms; the
    /// interval configured through <see cref="NoDelay"/> decides how often this
    /// actually flushes.
    /// </summary>
    public void Update()
    {
        uint current = CurrentMs();
        if (_updated == 0)
        {
            _updated = 1;
            _tsFlush = current;
        }

        int slap = Diff(current, _tsFlush);
        if (slap >= 10000 || slap < -10000)
        {
            _tsFlush = current;
            slap = 0;
        }

        if (slap >= 0)
        {
            _tsFlush += _interval;
            if (Diff(current, _tsFlush) >= 0) _tsFlush = current + _interval;
            Flush(FlushType.Full);
        }
    }

    /// <summary>
    /// Milliseconds until <see cref="Update"/> next needs to run. Lets the session
    /// loop sleep instead of spinning at the flush interval when nothing is due.
    /// </summary>
    public uint CheckDelay()
    {
        uint current = CurrentMs();
        if (_updated == 0) return 0;

        uint tsFlush = _tsFlush;
        if (Diff(current, tsFlush) >= 10000 || Diff(current, tsFlush) < -10000) tsFlush = current;
        if (Diff(current, tsFlush) >= 0) return 0;

        int tmFlush = Diff(tsFlush, current);
        int tmPacket = 0x7FFFFFFF;
        foreach (var segment in _sndBuf)
        {
            int diff = Diff(segment.Resendts, current);
            if (diff <= 0) return 0;
            if (diff < tmPacket) tmPacket = diff;
        }

        uint minimal = (uint)Math.Min(tmPacket, tmFlush);
        if (minimal >= _interval) minimal = _interval;
        return minimal;
    }
}
