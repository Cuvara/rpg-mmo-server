namespace GameServer.Net.Transport;

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
    /// Largest single application write a session accepts: one maximum wire frame, the
    /// 4-byte length prefix plus <see cref="WireProtocol.MaxMessageSize"/>. Anything larger
    /// cannot be a valid frame, and is refused with the session closed rather than queued.
    /// </summary>
    public const int MaxWriteBytes = 4 + WireProtocol.MaxMessageSize;

    /// <summary>
    /// Writes are handed to <see cref="Kcp.Send"/> in pieces of at most this many bytes, the
    /// way kcp-go's <c>UDPSession.Write</c> splits by MSS. One <c>Send</c> may not span more
    /// than 255 fragments; 64 KiB is ~50 segments, so a maximum frame never trips that limit.
    /// </summary>
    public const int WriteChunkBytes = 64 * 1024;

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
