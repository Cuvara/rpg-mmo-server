using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace GameServer.Net.Transport;

/// <summary>
/// A KCP-over-UDP listener that is wire-compatible with
/// <c>github.com/xtaci/kcp-go/v5</c>'s <c>ListenWithOptions</c>, as configured by
/// <c>backend/shared/transport</c>.
/// </summary>
/// <remarks>
/// <para>
/// KCP has no connection handshake. A session springs into existence when a
/// datagram arrives from an unknown endpoint carrying a well-formed KCP header;
/// the conversation id is read out of that first packet and adopted. Unlike kcp-go,
/// this listener only opens a session for a datagram whose first segment is a
/// <c>PUSH</c> with <c>sn == 0</c> — the first thing any dialer sends — so stray ACKs
/// and retransmissions from a peer whose session already ended cannot resurrect it.
/// </para>
/// <para>
/// One UDP socket serves every session. Demultiplexing is by remote endpoint,
/// with the conversation id as a consistency check: a packet from a known
/// endpoint carrying a different conv at <c>sn == 0</c> means the peer restarted, so
/// the old session is torn down and a new one takes its place.
/// </para>
/// <para>
/// <b>Flood protection</b> (<see cref="KcpListenerOptions"/>): a global session cap, a
/// per-source-IP cap, a listener-wide token bucket on new sessions and a per-session
/// token bucket on inbound datagrams. Oversized datagrams (above
/// <see cref="KcpTuning.MtuLimit"/>) are dropped unparsed rather than truncated. Every
/// refusal is counted in <see cref="Stats"/>.
/// </para>
/// </remarks>
public sealed class KcpListener : IDisposable
{
    /// <summary>
    /// Receive buffer size. Deliberately far above <see cref="KcpTuning.MtuLimit"/>: a
    /// buffer of exactly the limit lets the OS truncate a larger datagram silently, and the
    /// truncated prefix would then be parsed as if it were whole. With room for any UDP
    /// payload the true size is known and an oversized datagram is dropped and counted.
    /// </summary>
    private const int ReceiveBufferBytes = 64 * 1024;

    private const byte CmdPush = 81;

    /// <summary>
    /// Process-wide index of live sessions by conversation id, kept only while
    /// <see cref="TrackSessionsByConv"/> is set (by the in-process test client). Off in
    /// production, where nothing reads it and conv ids from unrelated peers may collide.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, KcpSession> s_byConv = new();

    /// <summary>Turns on the <see cref="FindLiveSession"/> index. Test-only.</summary>
    internal static volatile bool TrackSessionsByConv;

    /// <summary>Sessions in the conv index (all listeners in the process). Test diagnostics.</summary>
    internal static int LiveSessionCount => s_byConv.Count;

    private readonly Socket _socket;
    private readonly KcpCrypto? _crypto;
    private readonly int _headerSize;
    private readonly ILogger _logger;
    private readonly KcpListenerOptions _limits;
    private readonly ConcurrentDictionary<IPEndPoint, KcpSession> _sessions = new();
    private readonly ConcurrentDictionary<IPAddress, int> _perIp = new();
    private readonly Channel<KcpSession> _accepts =
        Channel.CreateBounded<KcpSession>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _recvThread;
    private readonly Thread _updateThread;
    // Touched only by the receive loop.
    private TokenBucket _newSessions;
    private int _disposed;

    /// <summary>The address the socket is actually bound to (resolves an ephemeral port 0).</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>True when a transport key is configured and every datagram is AES-256 encrypted.</summary>
    public bool IsEncrypted => _crypto != null;

    /// <summary>The limits this listener enforces.</summary>
    public KcpListenerOptions Limits => _limits;

    /// <summary>Flood-protection and drop counters.</summary>
    public KcpListenerStats Stats { get; } = new();

    /// <summary>Sessions currently in the table.</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>
    /// Binds a KCP listener with <see cref="KcpListenerOptions.Default"/> limits. A
    /// non-empty <paramref name="transportKey"/> turns on kcp-go-compatible AES-256; an
    /// empty one leaves the link in cleartext and the caller is expected to have warned.
    /// </summary>
    public KcpListener(IPEndPoint bind, string? transportKey, ILogger logger)
        : this(bind, transportKey, logger, KcpListenerOptions.Default)
    {
    }

    /// <summary>Binds a KCP listener enforcing <paramref name="limits"/>.</summary>
    public KcpListener(IPEndPoint bind, string? transportKey, ILogger logger, KcpListenerOptions limits)
    {
        _logger = logger;
        _limits = limits;
        _crypto = KcpCrypto.TryCreate(transportKey);
        _headerSize = _crypto != null ? KcpCrypto.HeaderSize : 0;
        _newSessions = new TokenBucket(limits.NewSessionsPerSecond, limits.NewSessionBurst);
        Stats.SetLiveProvider(() => _sessions.Count);

        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // Windows raises ConnectionReset on a UDP socket when a previous send drew
            // an ICMP port-unreachable. For a multiplexed listener that is noise from
            // one peer taking down the whole receive loop, so switch it off.
            const int SIO_UDP_CONNRESET = -1744830452;
            try { _socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch (SocketException) { /* best effort */ }
        }
        _socket.Bind(bind);
        TrySetBuffer(() => _socket.ReceiveBufferSize = SocketBufferBytes);
        TrySetBuffer(() => _socket.SendBufferSize = SocketBufferBytes);
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        WarnIfBuffersCapped();

        // Dedicated threads, not thread-pool tasks. Every session's input and every ARQ
        // timer runs through these two loops, so a thread pool starved by unrelated
        // blocking work (or slow to inject threads under a burst) would stall all realtime
        // traffic at once — retransmissions included. A TCP stack does this work in the
        // kernel; over UDP it is ours, and it gets its own threads.
        _recvThread = new Thread(() => ReceiveLoop(_cts.Token)) { IsBackground = true, Name = "kcp-recv", Priority = ThreadPriority.AboveNormal };
        _updateThread = new Thread(() => UpdateLoop(_cts.Token)) { IsBackground = true, Name = "kcp-update", Priority = ThreadPriority.AboveNormal };
        _recvThread.Start();
        _updateThread.Start();
    }

    /// <summary>
    /// Socket buffer size. One UDP socket carries every session's traffic, so it
    /// needs far more room than a per-peer stream socket or snapshot bursts get
    /// dropped by the kernel. Matches Go's <c>KCPSocketBuffer</c>.
    /// </summary>
    private const int SocketBufferBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The buffer request above is best effort, and Linux silently caps it at
    /// <c>net.core.rmem_max</c> / <c>wmem_max</c> (212992 bytes by default; reported back
    /// doubled). That cap is host-wide - a container cannot raise it - and a capped receive
    /// buffer is where a burst of datagrams for every session is lost (UDP RcvbufErrors),
    /// which KCP then sees as loss on every connection at once. Say so at startup.
    /// </summary>
    private void WarnIfBuffersCapped()
    {
        int rcv = 0, snd = 0;
        try { rcv = _socket.ReceiveBufferSize; snd = _socket.SendBufferSize; } catch (SocketException) { return; }
        if (rcv < SocketBufferBytes || snd < SocketBufferBytes)
        {
            _logger.LogWarning(
                "KCP socket buffers capped by the OS: receive={Rcv} send={Snd} bytes (requested {Want}). " +
                "On Linux raise the HOST sysctls net.core.rmem_max and net.core.wmem_max to at least {Want} " +
                "(a container cannot); see backend/docs/NETWORKING.md",
                rcv, snd, SocketBufferBytes, SocketBufferBytes);
        }
        else
        {
            _logger.LogInformation("KCP socket buffers: receive={Rcv} send={Snd} bytes", rcv, snd);
        }
    }

    private static void TrySetBuffer(Action set)
    {
        // Some sandboxes cap SO_RCVBUF/SO_SNDBUF below the request; an undersized
        // buffer only costs throughput, so never fail the listener over it.
        try { set(); } catch (SocketException) { } catch (ObjectDisposedException) { }
    }

    /// <summary>Waits for the next accepted session.</summary>
    public async Task<KcpSession> AcceptAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        return await _accepts.Reader.ReadAsync(linked.Token);
    }

    /// <summary>True when a live session for <paramref name="remote"/> is in the table.</summary>
    public bool HasSession(IPEndPoint remote) => _sessions.ContainsKey(remote);

    // ── Process-local lookup ─────────────────────────────────────────────────

    /// <summary>
    /// Finds a live session with conversation id <paramref name="conv"/> on any listener
    /// in this process (requires <see cref="TrackSessionsByConv"/>). KCP has no close signal
    /// on the wire, so an in-process peer (the test client) uses this to learn that the
    /// server ended its session — the moment a socket peer would have read EOF — and to
    /// deliver a FIN-equivalent of its own.
    /// </summary>
    internal static KcpSession? FindLiveSession(uint conv) =>
        s_byConv.TryGetValue(conv, out var session) && !session.IsClosed ? session : null;

    private void ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferBytes];
        var any = new IPEndPoint(
            _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            int received;
            EndPoint from = any;
            try
            {
                received = _socket.ReceiveFrom(buffer, SocketFlags.None, ref from);
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break; // the socket was closed by Dispose
                // A per-datagram ICMP error (or an over-long datagram on a platform that
                // reports it as an error) must not kill the shared receive loop.
                if (ex.SocketErrorCode == SocketError.MessageSize) Interlocked.Increment(ref Stats._droppedOversize);
                _logger.LogDebug(ex, "KCP receive error");
                continue;
            }

            long dt0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                HandleDatagram(buffer.AsSpan(0, received), (IPEndPoint)from);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KCP datagram handling failed for {Remote}", from);
            }
            Interlocked.Increment(ref Stats._datagramsReceived);
            Interlocked.Add(ref Stats._receiveBusyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - dt0);
        }
    }

    private void HandleDatagram(Span<byte> datagram, IPEndPoint remote)
    {
        if (datagram.Length > KcpTuning.MtuLimit)
        {
            // kcp-go never sends above its mtuLimit; something larger is not a KCP packet
            // from a conforming peer, and parsing a prefix of it would be worse than useless.
            Interlocked.Increment(ref Stats._droppedOversize);
            return;
        }

        Span<byte> data = datagram;
        if (_crypto != null)
        {
            data = _crypto.Open(datagram);
            // Empty means the checksum failed: a peer with the wrong key, or garbage.
            // Dropping silently is the fail-closed behaviour the Go tests assert.
            if (data.IsEmpty)
            {
                Interlocked.Increment(ref Stats._droppedBadCrypto);
                return;
            }
        }

        if (data.Length < Kcp.Overhead)
        {
            Interlocked.Increment(ref Stats._droppedUndersize);
            return;
        }

        // FEC is disabled on both sides, so the conversation id is the first field of
        // the KCP header. kcp-go marks FEC packets with 0xf1/0xf2 at offset 4, values
        // no KCP cmd can take — seeing one means the peer enabled FEC and we cannot
        // parse it, so drop rather than misinterpret.
        ushort fecFlag = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (fecFlag is 0x00f1 or 0x00f2 or 0x00f3)
        {
            if (Interlocked.Increment(ref Stats._droppedFec) == 1)
            {
                _logger.LogWarning("Dropping FEC/OOB KCP packet from {Remote}: this listener runs with FEC off " +
                                   "(dataShards=0), matching backend/shared/transport", remote);
            }
            return;
        }

        uint conv = BinaryPrimitives.ReadUInt32LittleEndian(data);
        byte cmd = data[4];
        uint sn = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        bool opensConversation = cmd == CmdPush && sn == 0;

        KcpSession? replacing = null;
        if (_sessions.TryGetValue(remote, out var existing))
        {
            if (conv == existing.Conv)
            {
                if (!existing.TryAdmitDatagram())
                {
                    Interlocked.Increment(ref Stats._droppedRate);
                    return;
                }
                if (!existing.OnDatagram(data)) Interlocked.Increment(ref Stats._droppedMalformed);
                return;
            }
            // Conversation mismatch from a known endpoint. Only a fresh session's first
            // packet (PUSH sn == 0) is trusted to replace it; anything else is a stray.
            if (!opensConversation)
            {
                Interlocked.Increment(ref Stats._droppedConvMismatch);
                return;
            }
            replacing = existing;
        }
        else if (!opensConversation)
        {
            // An ACK, a retransmission or a probe for a session this listener does not
            // have — typically a peer whose session already ended. Opening a session for
            // it would hand the server a stream that starts mid-frame.
            Interlocked.Increment(ref Stats._droppedNoSession);
            return;
        }

        // ── Admission: every check before any state is created ───────────────
        if (replacing == null)
        {
            if (_sessions.Count >= _limits.MaxSessions)
            {
                if (Interlocked.Increment(ref Stats._rejectedGlobalCap) % 1000 == 1)
                {
                    _logger.LogWarning("KCP session refused from {Remote}: listener is at its {Max}-session cap ({Stats})",
                        remote, _limits.MaxSessions, Stats);
                }
                return;
            }
            if (!(_limits.ExemptLoopbackFromPerIpCap && IPAddress.IsLoopback(remote.Address))
                && _perIp.TryGetValue(remote.Address, out int fromIp) && fromIp >= _limits.MaxSessionsPerIp)
            {
                if (Interlocked.Increment(ref Stats._rejectedPerIpCap) % 1000 == 1)
                {
                    _logger.LogWarning("KCP session refused from {Remote}: {Count} sessions already open from that address (cap {Max})",
                        remote, fromIp, _limits.MaxSessionsPerIp);
                }
                return;
            }
        }
        if (!_newSessions.TryTake())
        {
            if (Interlocked.Increment(ref Stats._rejectedRate) % 1000 == 1)
            {
                _logger.LogWarning("KCP session refused from {Remote}: new-session rate above {Rate}/s ({Stats})",
                    remote, _limits.NewSessionsPerSecond, Stats);
            }
            return;
        }

        if (replacing != null)
        {
            Interlocked.Increment(ref Stats._sessionsReplaced);
            replacing.Close(KcpCloseReason.Replaced);
        }

        var session = new KcpSession(conv, remote, packet => SendTo(packet, remote), _headerSize, _limits, Stats, _logger);

        // Indexed before the opener is fed: feeding it sends the ACK, and an in-process
        // peer that sees the ACK must also be able to find the session.
        if (TrackSessionsByConv) s_byConv[conv] = session;

        // Feed the opener before the session is published: one the state machine refuses
        // (a truncated segment, an unknown command after the first) creates nothing.
        session.TryAdmitDatagram();
        if (!session.OnDatagram(data))
        {
            Interlocked.Increment(ref Stats._droppedMalformed);
            if (TrackSessionsByConv) s_byConv.TryRemove(new KeyValuePair<uint, KcpSession>(conv, session));
            session.Dispose();
            return;
        }

        session.Closed += OnSessionClosed;
        if (!_sessions.TryAdd(remote, session))
        {
            session.Closed -= OnSessionClosed;
            if (TrackSessionsByConv) s_byConv.TryRemove(new KeyValuePair<uint, KcpSession>(conv, session));
            session.Dispose();
            return;
        }
        _perIp.AddOrUpdate(remote.Address, 1, (_, n) => n + 1);
        Interlocked.Increment(ref Stats._sessionsCreated);

        if (!_accepts.Writer.TryWrite(session))
        {
            // Accept backlog full: drop the session rather than queue unboundedly. The
            // peer's KCP will retransmit its sn=0 segment, which re-creates it later.
            Interlocked.Increment(ref Stats._rejectedBacklog);
            session.Dispose();
        }
    }

    private void OnSessionClosed(KcpSession s)
    {
        if (!_sessions.TryRemove(new KeyValuePair<IPEndPoint, KcpSession>(s.Remote, s))) return;
        if (TrackSessionsByConv) s_byConv.TryRemove(new KeyValuePair<uint, KcpSession>(s.Conv, s));
        while (true)
        {
            if (!_perIp.TryGetValue(s.Remote.Address, out int n)) return;
            if (n <= 1)
            {
                if (_perIp.TryRemove(new KeyValuePair<IPAddress, int>(s.Remote.Address, n))) return;
            }
            else if (_perIp.TryUpdate(s.Remote.Address, n - 1, n))
            {
                return;
            }
        }
    }

    private void SendTo(ReadOnlyMemory<byte> packet, IPEndPoint remote)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            if (_crypto != null)
            {
                // Seal in place: the session already reserved the header bytes.
                var owned = System.Runtime.InteropServices.MemoryMarshal.AsMemory(packet);
                _crypto.Seal(owned.Span);
            }
            _socket.SendTo(packet.Span, SocketFlags.None, remote);
        }
        catch (SocketException) { /* datagram loss is KCP's problem, not ours */ }
        catch (ObjectDisposedException) { }
    }

    private void UpdateLoop(CancellationToken ct)
    {
        // One loop drives every session's ARQ timer. A timer per session would cost a
        // scheduler entry per player for no benefit: the interval is uniform.
        while (!ct.WaitHandle.WaitOne(KcpTuning.Interval))
        {
            foreach (var session in _sessions.Values)
            {
                try { session.Tick(); }
                catch (Exception ex) { _logger.LogWarning(ex, "KCP session tick failed for {Remote}", session.Remote); }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _accepts.Writer.TryComplete();
        foreach (var session in _sessions.Values)
        {
            session.Close(KcpCloseReason.ListenerClosed);
            if (TrackSessionsByConv) s_byConv.TryRemove(new KeyValuePair<uint, KcpSession>(session.Conv, session));
        }
        // Sessions created but never accepted are owned by nobody else.
        while (_accepts.Reader.TryRead(out var pending)) pending.Close(KcpCloseReason.ListenerClosed);
        _sessions.Clear();
        _perIp.Clear();
        try { _socket.Close(); } catch { /* ignore */ }
        _socket.Dispose();
        try { _recvThread.Join(TimeSpan.FromSeconds(2)); _updateThread.Join(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _crypto?.Dispose();
        _cts.Dispose();
        if (Stats.DatagramsDropped > 0 || Stats.SessionsRejected > 0)
        {
            _logger.LogInformation("KCP listener {Local} closed: {Stats}", LocalEndPoint, Stats);
        }
    }
}
