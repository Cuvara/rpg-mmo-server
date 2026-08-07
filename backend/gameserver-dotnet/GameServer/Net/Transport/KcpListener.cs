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
/// KCP has no connection handshake. A session springs into existence the moment a
/// datagram arrives from an unknown endpoint carrying a well-formed KCP header;
/// the conversation id is read out of that first packet and adopted. That is
/// literally what kcp-go's <c>Listener.packetInput</c> does, and it is why no
/// hello exchange is needed for a Go dialer to reach this listener.
/// </para>
/// <para>
/// One UDP socket serves every session. Demultiplexing is by remote endpoint,
/// with the conversation id as a consistency check: a packet from a known
/// endpoint carrying a different conv means the peer restarted, so the old
/// session is torn down and a new one takes its place.
/// </para>
/// </remarks>
public sealed class KcpListener : IDisposable
{
    private readonly Socket _socket;
    private readonly KcpCrypto? _crypto;
    private readonly int _headerSize;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<IPEndPoint, KcpSession> _sessions = new();
    private readonly Channel<KcpSession> _accepts =
        Channel.CreateBounded<KcpSession>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _recvLoop;
    private readonly Task _updateLoop;
    private int _disposed;

    /// <summary>The address the socket is actually bound to (resolves an ephemeral port 0).</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>True when a transport key is configured and every datagram is AES-256 encrypted.</summary>
    public bool IsEncrypted => _crypto != null;

    /// <summary>
    /// Binds a KCP listener. A non-empty <paramref name="transportKey"/> turns on
    /// kcp-go-compatible AES-256; an empty one leaves the link in cleartext and the
    /// caller is expected to have warned about it.
    /// </summary>
    public KcpListener(IPEndPoint bind, string? transportKey, ILogger logger)
    {
        _logger = logger;
        _crypto = KcpCrypto.TryCreate(transportKey);
        _headerSize = _crypto != null ? KcpCrypto.HeaderSize : 0;

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

        _recvLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _updateLoop = Task.Run(() => UpdateLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Socket buffer size. One UDP socket carries every session's traffic, so it
    /// needs far more room than a per-connection TCP socket or snapshot bursts get
    /// dropped by the kernel. Matches Go's <c>KCPSocketBuffer</c>.
    /// </summary>
    private const int SocketBufferBytes = 4 * 1024 * 1024;

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

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[KcpTuning.MtuLimit];
        EndPoint any = new IPEndPoint(
            _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                // A per-datagram ICMP error must not kill the shared receive loop.
                _logger.LogDebug(ex, "KCP receive error");
                continue;
            }

            try
            {
                HandleDatagram(buffer.AsSpan(0, result.ReceivedBytes), (IPEndPoint)result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KCP datagram handling failed for {Remote}", result.RemoteEndPoint);
            }
        }
    }

    private void HandleDatagram(Span<byte> datagram, IPEndPoint remote)
    {
        Span<byte> data = datagram;
        if (_crypto != null)
        {
            data = _crypto.Open(datagram);
            // Empty means the checksum failed: a peer with the wrong key, or garbage.
            // Dropping silently is the fail-closed behaviour the Go tests assert.
            if (data.IsEmpty) return;
        }

        if (data.Length < Kcp.Overhead) return;

        // FEC is disabled on both sides, so the conversation id is the first field of
        // the KCP header. kcp-go marks FEC packets with 0xf1/0xf2 at offset 4, values
        // no KCP cmd can take — seeing one means the peer enabled FEC and we cannot
        // parse it, so drop rather than misinterpret.
        ushort fecFlag = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (fecFlag is 0x00f1 or 0x00f2 or 0x00f3)
        {
            _logger.LogWarning("Dropping FEC/OOB KCP packet from {Remote}: this listener runs with FEC off " +
                               "(dataShards=0), matching backend/shared/transport", remote);
            return;
        }

        uint conv = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint sn = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);

        if (_sessions.TryGetValue(remote, out var existing))
        {
            if (conv == existing.Conv)
            {
                existing.OnDatagram(data);
                return;
            }
            // Conversation mismatch from a known endpoint. Only a fresh session's first
            // packet (sn == 0) is trusted to replace it; anything else is a stray.
            if (sn != 0) return;
            existing.Close();
        }

        var session = new KcpSession(conv, remote, packet => SendTo(packet, remote), _headerSize);
        session.Closed += s => _sessions.TryRemove(new KeyValuePair<IPEndPoint, KcpSession>(s.Remote, s));

        if (!_sessions.TryAdd(remote, session))
        {
            session.Dispose();
            return;
        }

        session.OnDatagram(data);

        if (!_accepts.Writer.TryWrite(session))
        {
            // Accept backlog full: drop the session rather than queue unboundedly. The
            // peer's KCP will retransmit, and a later datagram re-creates it.
            _sessions.TryRemove(new KeyValuePair<IPEndPoint, KcpSession>(remote, session));
            session.Dispose();
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

    private async Task UpdateLoopAsync(CancellationToken ct)
    {
        // One loop drives every session's ARQ timer. A timer per session would cost a
        // scheduler entry per player for no benefit: the interval is uniform.
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(KcpTuning.Interval));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var session in _sessions.Values) session.Tick();
            }
        }
        catch (OperationCanceledException) { }
        finally { timer.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _accepts.Writer.TryComplete();
        foreach (var session in _sessions.Values) session.Close();
        _sessions.Clear();
        try { _socket.Close(); } catch { /* ignore */ }
        _socket.Dispose();
        try { Task.WaitAll([_recvLoop, _updateLoop], TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _crypto?.Dispose();
        _cts.Dispose();
    }
}
