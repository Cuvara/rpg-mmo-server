using System.Net;
using System.Threading.Channels;

namespace GameServer.Net.Transport;

/// <summary>
/// One KCP conversation with a remote peer: the ARQ state machine from
/// <see cref="Kcp"/> plus the buffering needed to expose it as a byte stream.
/// </summary>
/// <remarks>
/// Sessions do not own a socket. A <see cref="KcpListener"/> multiplexes every
/// session over one UDP socket — that is how kcp-go's listener works, and it is
/// why a peer is identified by its remote endpoint plus the conversation id.
/// </remarks>
public sealed class KcpSession : IDisposable
{
    private readonly Kcp _kcp;
    private readonly object _lock = new();
    private readonly Action<ReadOnlyMemory<byte>> _send;
    private readonly Channel<byte[]> _received =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly byte[] _recvScratch = new byte[KcpTuning.MaxMessageSize];
    private readonly CancellationTokenSource _cts = new();
    private int _closed;
    private long _lastActivityTicks = Environment.TickCount64;

    /// <summary>The peer this session talks to.</summary>
    public IPEndPoint Remote { get; }

    /// <summary>The conversation id shared with the peer.</summary>
    public uint Conv => _kcp.Conv;

    /// <summary>Raised once when the session stops, so the listener can drop it from its table.</summary>
    public event Action<KcpSession>? Closed;

    /// <summary>
    /// Creates a session for <paramref name="conv"/>, sending datagrams through
    /// <paramref name="send"/> — which the listener wires to its shared socket.
    /// </summary>
    public KcpSession(uint conv, IPEndPoint remote, Action<ReadOnlyMemory<byte>> send, int headerSize)
    {
        Remote = remote;
        _send = send;

        _kcp = new Kcp(conv, (buf, size) =>
        {
            // Reserve room for the crypt header the caller fills in; below the KCP
            // layer the datagram is opaque, which is exactly kcp-go's split.
            var packet = new byte[headerSize + size];
            buf.AsSpan(0, size).CopyTo(packet.AsSpan(headerSize));
            _send(packet);
        });

        KcpTuning.Apply(_kcp, headerSize);
    }

    /// <summary>Feeds a decrypted datagram from the listener's receive loop into the ARQ.</summary>
    internal void OnDatagram(ReadOnlySpan<byte> data)
    {
        _lastActivityTicks = Environment.TickCount64;
        lock (_lock)
        {
            if (_kcp.Input(data, ackNoDelay: true) < 0) return;
            DrainLocked();
        }
    }

    /// <summary>Runs the periodic ARQ update. Called by the listener's update loop.</summary>
    internal void Tick()
    {
        lock (_lock)
        {
            _kcp.Update();
            if (_kcp.DeadLinkReached)
            {
                Close();
                return;
            }
        }

        if (Environment.TickCount64 - _lastActivityTicks > KcpTuning.IdleTimeoutMs) Close();
    }

    /// <summary>Moves every complete message out of the ARQ and into the read channel.</summary>
    private void DrainLocked()
    {
        while (true)
        {
            int n = _kcp.Recv(_recvScratch);
            if (n < 0) break;
            if (n == 0) continue;
            _received.Writer.TryWrite(_recvScratch.AsSpan(0, n).ToArray());
        }
    }

    /// <summary>
    /// Queues application bytes and flushes immediately. Immediate flush mirrors
    /// the Go side's <c>SetWriteDelay(false)</c>: waiting for the next update would
    /// add up to a full interval of latency to every frame.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (Volatile.Read(ref _closed) != 0) throw new IOException("KCP session closed");
        lock (_lock)
        {
            _kcp.Send(data);
            _kcp.Flush();
        }
    }

    /// <summary>Waits for the next chunk of application bytes, or null when the session ends.</summary>
    public async ValueTask<byte[]?> ReadChunkAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try
        {
            return await _received.Reader.ReadAsync(linked.Token);
        }
        catch (ChannelClosedException) { return null; }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return null; }
    }

    /// <summary>Closes the session. Idempotent.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _received.Writer.TryComplete();
        _cts.Cancel();
        Closed?.Invoke(this);
    }

    public void Dispose()
    {
        Close();
        _cts.Dispose();
    }
}

/// <summary>
/// The KCP tuning profile. Every value here must equal the Go constants in
/// <c>backend/shared/transport/transport.go</c>; a mismatch does not break the
/// wire format but does change latency and window behaviour asymmetrically,
/// which is far harder to notice than an outright failure.
/// </summary>
public static class KcpTuning
{
    /// <summary>NoDelay ARQ on.</summary>
    public const int NoDelay = 1;
    /// <summary>Internal update interval, ms.</summary>
    public const int Interval = 10;
    /// <summary>Fast retransmit after N duplicate ACKs.</summary>
    public const int Resend = 2;
    /// <summary>1 = congestion control disabled.</summary>
    public const int NoCongestion = 1;
    /// <summary>Send window, packets.</summary>
    public const int SendWindow = 128;
    /// <summary>Receive window, packets.</summary>
    public const int RecvWindow = 128;
    /// <summary>MTU, bytes. kcp-go's default; stays under common path MTUs.</summary>
    public const int Mtu = 1350;

    /// <summary>Hard cap on a single reassembled KCP message, matching kcp-go's window x mss.</summary>
    public const int MaxMessageSize = RecvWindow * Mtu;

    /// <summary>Largest datagram the receive loop will accept (kcp-go's <c>mtuLimit</c>).</summary>
    public const int MtuLimit = 1500;

    /// <summary>
    /// Drop a session after this long without an inbound datagram. KCP has no FIN,
    /// so without an idle sweep a client that vanishes leaves its session (and its
    /// world entity) resident forever.
    /// </summary>
    public const int IdleTimeoutMs = 60_000;

    /// <summary>Applies the profile to a fresh state machine.</summary>
    public static void Apply(Kcp kcp, int headerSize)
    {
        kcp.Stream = 1;
        kcp.NoDelay(NoDelay, Interval, Resend, NoCongestion);
        kcp.WndSize(SendWindow, RecvWindow);
        kcp.SetMtu(Mtu - headerSize);
    }
}
