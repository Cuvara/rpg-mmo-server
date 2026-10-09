using System.Diagnostics;

namespace GameServer.Net.Transport;

/// <summary>
/// Flood-protection and backpressure limits for a <see cref="KcpListener"/>.
/// </summary>
/// <remarks>
/// <para>
/// KCP has no handshake: one well-formed datagram from an unknown endpoint is enough to
/// make a session, a table entry and an accept. Without these bounds a single spoofing
/// host can fill the session table, the accept queue and the pending-handshake pool with
/// zero cost to itself. Every limit here drops or refuses <b>visibly</b> — each one has a
/// counter in <see cref="KcpListenerStats"/> and on <c>/metrics</c>.
/// </para>
/// <para>
/// None of these can lose bytes from an established reliable stream: datagram drops are
/// recovered by KCP's retransmission, and the receive cap throttles the peer through the
/// advertised window instead of discarding anything.
/// </para>
/// </remarks>
public sealed record KcpListenerOptions
{
    /// <summary>The defaults used in production unless an operator overrides them.</summary>
    public static KcpListenerOptions Default { get; } = new();

    /// <summary>
    /// Hard cap on live sessions (all states, pre-auth included). 4096 is roughly 20x a
    /// map server's player capacity, so it never binds on legitimate load, and bounds the
    /// per-session state a flood can make the process hold (each session is a KCP state
    /// machine plus up to <see cref="MaxPendingReceiveBytes"/> of buffered input).
    /// <c>GAMESERVER_KCP_MAX_SESSIONS</c>.
    /// </summary>
    public int MaxSessions { get; init; } = 4096;

    /// <summary>
    /// Cap on live sessions sharing one source IP. 16 leaves room for a household or a
    /// small NAT while stopping one host from owning the table. Carrier-grade NAT puts
    /// more players behind one address; raise it there.
    /// <c>GAMESERVER_KCP_MAX_SESSIONS_PER_IP</c>.
    /// </summary>
    public int MaxSessionsPerIp { get; init; } = 16;

    /// <summary>
    /// Whether loopback peers are exempt from <see cref="MaxSessionsPerIp"/>. A loopback
    /// source is a local process (a probe, a sidecar, an in-process test), not a remote
    /// host. The global cap and the rate limits still apply.
    /// </summary>
    public bool ExemptLoopbackFromPerIpCap { get; init; } = true;

    /// <summary>
    /// Sustained rate of new sessions per second, listener-wide (token bucket). 200/s
    /// admits a full map's worth of players reconnecting after a server restart in about
    /// a second. <c>GAMESERVER_KCP_NEW_SESSIONS_PER_SEC</c>; burst is twice the rate.
    /// </summary>
    public double NewSessionsPerSecond { get; init; } = 200;

    /// <summary>Burst size of the new-session token bucket.</summary>
    public double NewSessionBurst { get; init; } = 400;

    /// <summary>
    /// Sustained inbound datagrams per second per session (token bucket). A client sends
    /// inputs at 15-60Hz plus one ACK per received datagram (snapshots at up to 60Hz), so
    /// legitimate traffic stays well under 200/s; 500/s with a 1000 burst leaves room for
    /// retransmission bursts after a stall. Excess datagrams are DROPPED, never fatal:
    /// KCP retransmits them. <c>GAMESERVER_KCP_DATAGRAMS_PER_SEC</c>; burst is twice the rate.
    /// </summary>
    public double DatagramsPerSecondPerSession { get; init; } = 500;

    /// <summary>Burst size of the per-session datagram token bucket.</summary>
    public double DatagramBurstPerSession { get; init; } = 1000;

    /// <summary>
    /// Close a session after this long without an inbound datagram. KCP has no FIN, so
    /// without an idle sweep a client that vanished silently would stay resident forever.
    /// </summary>
    public int IdleTimeoutMs { get; init; } = KcpTuning.IdleTimeoutMs;

    /// <summary>
    /// Bytes of reassembled input a session may hold for its reader before it stops
    /// draining the ARQ. Once the cap is reached KCP's receive queue fills and the
    /// advertised window drops to zero, so the PEER stops sending — backpressure, never a
    /// drop. 256 KiB is above one maximum message (<see cref="KcpTuning.MaxMessageSize"/>).
    /// </summary>
    public int MaxPendingReceiveBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Queued-plus-in-flight segments at which a writer WAITS for ACKs before queueing
    /// more — the KCP equivalent of a full TCP send buffer, which is what lets the
    /// connection's drop-the-oldest snapshot lane engage for a slow peer.
    /// </summary>
    public int SendQueueSoftLimit { get; init; } = 2 * KcpTuning.SendWindow;

    /// <summary>
    /// Queued-plus-in-flight segments beyond which the session is closed as a slow or dead
    /// consumer — the hard bound on per-session send memory (~1.4 MB). It sits above the soft
    /// limit plus one maximum frame (<see cref="KcpTuning.MaxWriteBytes"/>, ~790 segments),
    /// so a writer that honours the soft limit can never reach it; only one that keeps
    /// writing without waiting while the peer does not acknowledge does.
    /// </summary>
    public int SendQueueHardLimit { get; init; } = 8 * KcpTuning.SendWindow;

    /// <summary>
    /// Builds options from operator overrides, keeping defaults for anything unset.
    /// Non-positive values are rejected by <paramref name="error"/> rather than clamped,
    /// so a typo cannot silently disable a limit.
    /// </summary>
    public static bool TryFromOverrides(
        int? maxSessions, int? maxSessionsPerIp, double? newSessionsPerSecond, double? datagramsPerSecond,
        out KcpListenerOptions options, out string? error)
    {
        options = Default;
        error = null;
        if (maxSessions is <= 0) { error = $"KCP max sessions must be > 0 (got {maxSessions})"; return false; }
        if (maxSessionsPerIp is <= 0) { error = $"KCP max sessions per IP must be > 0 (got {maxSessionsPerIp})"; return false; }
        if (newSessionsPerSecond is <= 0) { error = $"KCP new sessions per second must be > 0 (got {newSessionsPerSecond})"; return false; }
        if (datagramsPerSecond is <= 0) { error = $"KCP datagrams per second must be > 0 (got {datagramsPerSecond})"; return false; }

        options = Default with
        {
            MaxSessions = maxSessions ?? Default.MaxSessions,
            MaxSessionsPerIp = maxSessionsPerIp ?? Default.MaxSessionsPerIp,
            NewSessionsPerSecond = newSessionsPerSecond ?? Default.NewSessionsPerSecond,
            NewSessionBurst = newSessionsPerSecond is { } r ? 2 * r : Default.NewSessionBurst,
            DatagramsPerSecondPerSession = datagramsPerSecond ?? Default.DatagramsPerSecondPerSession,
            DatagramBurstPerSession = datagramsPerSecond is { } d ? 2 * d : Default.DatagramBurstPerSession,
        };
        return true;
    }

    /// <summary>One-line description for the startup log.</summary>
    public override string ToString() =>
        $"maxSessions={MaxSessions} perIp={MaxSessionsPerIp}{(ExemptLoopbackFromPerIpCap ? " (loopback exempt)" : "")} " +
        $"newSessions={NewSessionsPerSecond}/s burst {NewSessionBurst} " +
        $"datagrams={DatagramsPerSecondPerSession}/s/session burst {DatagramBurstPerSession} " +
        $"idle={IdleTimeoutMs}ms recvCap={MaxPendingReceiveBytes}B sendQueue={SendQueueSoftLimit}/{SendQueueHardLimit} segments";
}

/// <summary>
/// Counters for one <see cref="KcpListener"/>. Every refusal and drop the listener makes is
/// counted here; nothing is discarded without a number moving. Thread-safe; read by tests,
/// the periodic stats log and the <c>/metrics</c> scrape.
/// </summary>
public sealed class KcpListenerStats
{
    internal long _sessionsCreated;
    internal long _sessionsReplaced;
    internal long _rejectedGlobalCap;
    internal long _rejectedPerIpCap;
    internal long _rejectedRate;
    internal long _rejectedBacklog;
    internal long _droppedOversize;
    internal long _droppedUndersize;
    internal long _droppedRate;
    internal long _droppedBadCrypto;
    internal long _droppedFec;
    internal long _droppedNoSession;
    internal long _droppedConvMismatch;
    internal long _droppedMalformed;
    internal long _closedIdle;
    internal long _closedDeadLink;
    internal long _closedSlowConsumer;
    internal long _writesRejected;
    private Func<int>? _liveProvider;

    internal void SetLiveProvider(Func<int> provider) => _liveProvider = provider;

    /// <summary>Sessions currently in the listener's table.</summary>
    public int SessionsLive => _liveProvider?.Invoke() ?? 0;
    /// <summary>Sessions ever created.</summary>
    public long SessionsCreated => Interlocked.Read(ref _sessionsCreated);
    /// <summary>Sessions replaced by a new conversation from the same endpoint (peer restart).</summary>
    public long SessionsReplaced => Interlocked.Read(ref _sessionsReplaced);
    /// <summary>New sessions refused because <see cref="KcpListenerOptions.MaxSessions"/> was reached.</summary>
    public long SessionsRejectedGlobalCap => Interlocked.Read(ref _rejectedGlobalCap);
    /// <summary>New sessions refused because the source IP had <see cref="KcpListenerOptions.MaxSessionsPerIp"/>.</summary>
    public long SessionsRejectedPerIpCap => Interlocked.Read(ref _rejectedPerIpCap);
    /// <summary>New sessions refused by the new-session token bucket.</summary>
    public long SessionsRejectedRate => Interlocked.Read(ref _rejectedRate);
    /// <summary>New sessions dropped because the accept backlog was full.</summary>
    public long SessionsRejectedBacklog => Interlocked.Read(ref _rejectedBacklog);
    /// <summary>Datagrams larger than <see cref="KcpTuning.MtuLimit"/>, dropped unparsed.</summary>
    public long DatagramsDroppedOversize => Interlocked.Read(ref _droppedOversize);
    /// <summary>Datagrams shorter than one KCP header (after decryption).</summary>
    public long DatagramsDroppedUndersize => Interlocked.Read(ref _droppedUndersize);
    /// <summary>Datagrams dropped by a session's inbound token bucket.</summary>
    public long DatagramsDroppedRate => Interlocked.Read(ref _droppedRate);
    /// <summary>Datagrams whose crypto checksum failed (wrong key or garbage).</summary>
    public long DatagramsDroppedBadCrypto => Interlocked.Read(ref _droppedBadCrypto);
    /// <summary>FEC-framed datagrams, which this listener (FEC off) cannot parse.</summary>
    public long DatagramsDroppedFec => Interlocked.Read(ref _droppedFec);
    /// <summary>Datagrams from an unknown endpoint that do not open a conversation (not PUSH sn=0).</summary>
    public long DatagramsDroppedNoSession => Interlocked.Read(ref _droppedNoSession);
    /// <summary>Datagrams from a known endpoint carrying another conversation id mid-stream.</summary>
    public long DatagramsDroppedConvMismatch => Interlocked.Read(ref _droppedConvMismatch);
    /// <summary>Datagrams the KCP state machine refused (bad command, truncated segment).</summary>
    public long DatagramsDroppedMalformed => Interlocked.Read(ref _droppedMalformed);
    /// <summary>Sessions closed by the idle timeout.</summary>
    public long SessionsClosedIdle => Interlocked.Read(ref _closedIdle);
    /// <summary>Sessions closed because a segment hit the dead-link retransmission limit.</summary>
    public long SessionsClosedDeadLink => Interlocked.Read(ref _closedDeadLink);
    /// <summary>Sessions closed because their send queue exceeded the hard limit.</summary>
    public long SessionsClosedSlowConsumer => Interlocked.Read(ref _closedSlowConsumer);
    /// <summary>Application writes refused (frame too large for KCP); each closes its session.</summary>
    public long WritesRejected => Interlocked.Read(ref _writesRejected);

    /// <summary>Total new sessions refused for any reason.</summary>
    public long SessionsRejected => SessionsRejectedGlobalCap + SessionsRejectedPerIpCap + SessionsRejectedRate + SessionsRejectedBacklog;

    /// <summary>Total datagrams dropped for any reason.</summary>
    public long DatagramsDropped =>
        DatagramsDroppedOversize + DatagramsDroppedUndersize + DatagramsDroppedRate + DatagramsDroppedBadCrypto +
        DatagramsDroppedFec + DatagramsDroppedNoSession + DatagramsDroppedConvMismatch + DatagramsDroppedMalformed;

    /// <summary>Compact one-line form for logs and test failure messages.</summary>
    public override string ToString() =>
        $"live={SessionsLive} created={SessionsCreated} replaced={SessionsReplaced} " +
        $"rejected[globalCap={SessionsRejectedGlobalCap} perIp={SessionsRejectedPerIpCap} rate={SessionsRejectedRate} backlog={SessionsRejectedBacklog}] " +
        $"dropped[oversize={DatagramsDroppedOversize} undersize={DatagramsDroppedUndersize} rate={DatagramsDroppedRate} " +
        $"badCrypto={DatagramsDroppedBadCrypto} fec={DatagramsDroppedFec} noSession={DatagramsDroppedNoSession} " +
        $"convMismatch={DatagramsDroppedConvMismatch} malformed={DatagramsDroppedMalformed}] " +
        $"closed[idle={SessionsClosedIdle} deadLink={SessionsClosedDeadLink} slowConsumer={SessionsClosedSlowConsumer}] " +
        $"writesRejected={WritesRejected}";
}

/// <summary>
/// A token bucket on the monotonic clock. Not thread-safe: each instance is touched by
/// exactly one thread (the listener's receive loop).
/// </summary>
internal struct TokenBucket
{
    private readonly double _ratePerTick;
    private readonly double _burst;
    private double _tokens;
    private long _lastTicks;

    public TokenBucket(double ratePerSecond, double burst)
    {
        _ratePerTick = ratePerSecond / Stopwatch.Frequency;
        _burst = Math.Max(1, burst);
        _tokens = _burst;
        _lastTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>Takes one token if available.</summary>
    public bool TryTake()
    {
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _lastTicks;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_burst, _tokens + elapsed * _ratePerTick);
            _lastTicks = now;
        }
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}
