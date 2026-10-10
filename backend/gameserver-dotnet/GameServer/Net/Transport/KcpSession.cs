using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Net.Transport;

/// <summary>Why a <see cref="KcpSession"/> stopped.</summary>
public enum KcpCloseReason
{
    /// <summary>Closed by the application (connection ended, handshake refused, shutdown).</summary>
    Local,
    /// <summary>No inbound datagram for <see cref="KcpListenerOptions.IdleTimeoutMs"/>.</summary>
    Idle,
    /// <summary>A segment reached KCP's dead-link retransmission limit.</summary>
    DeadLink,
    /// <summary>The send queue passed <see cref="KcpListenerOptions.SendQueueHardLimit"/>.</summary>
    SlowConsumer,
    /// <summary>An application frame could not be queued (too large for KCP).</summary>
    WriteRejected,
    /// <summary>A new conversation from the same endpoint replaced this one.</summary>
    Replaced,
    /// <summary>The listener was disposed.</summary>
    ListenerClosed,
}

/// <summary>
/// One KCP conversation with a remote peer: the ARQ state machine from
/// <see cref="Kcp"/> plus the buffering needed to expose it as a byte stream.
/// </summary>
/// <remarks>
/// <para>
/// Sessions do not own a socket. A <see cref="KcpListener"/> multiplexes every
/// session over one UDP socket — that is how kcp-go's listener works, and it is
/// why a peer is identified by its remote endpoint plus the conversation id.
/// </para>
/// <para>
/// <b>Bounded in both directions.</b> Inbound, reassembled bytes wait for the reader in a
/// queue capped at <see cref="KcpListenerOptions.MaxPendingReceiveBytes"/>; past it the
/// session stops draining the ARQ, KCP's receive window closes and the peer is throttled —
/// nothing is ever dropped from the reliable stream. Outbound, a writer waits once
/// <see cref="KcpListenerOptions.SendQueueSoftLimit"/> segments are queued or in flight
/// (the equivalent of a full TCP send buffer), and the session is closed as a slow or dead
/// consumer if the queue ever passes <see cref="KcpListenerOptions.SendQueueHardLimit"/>.
/// </para>
/// </remarks>
public sealed class KcpSession : IDisposable
{
    private readonly Kcp _kcp;
    private readonly object _lock = new();
    private readonly Action<ReadOnlyMemory<byte>> _send;
    private readonly KcpListenerOptions _limits;
    private readonly KcpListenerStats? _stats;
    private readonly ILogger _logger;
    // Unbounded as a container, bounded by _pendingRecvBytes: DrainLocked stops moving
    // messages out of the ARQ once the cap is reached, so this never holds more than the
    // cap plus one message. A bounded channel would instead have to block the shared
    // receive loop or drop reliable bytes, and both are wrong.
    private readonly Channel<byte[]> _received =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly byte[] _recvScratch = new byte[KcpTuning.MaxMessageSize];
    private readonly CancellationTokenSource _cts = new();
    private long _pendingRecvBytes;
    private bool _drainPaused;
    private TaskCompletionSource? _sendSpace;
    private TokenBucket _inbound;
    // Three-state close lifecycle; see Connection.cs for why a single flag is not
    // enough (it conflates "close begun" with "close complete", and Dispose()
    // needs the latter before it can free the CancellationTokenSource).
    private const int StateOpen = 0;
    private const int StateClosing = 1;
    private const int StateClosed = 2;
    private int _closeState;
    private long _lastActivityTicks = Environment.TickCount64;

    /// <summary>The peer this session talks to.</summary>
    public IPEndPoint Remote { get; }

    /// <summary>The conversation id shared with the peer.</summary>
    public uint Conv => _kcp.Conv;

    /// <summary>Why the session closed; meaningful once <see cref="IsClosed"/> is true.</summary>
    public KcpCloseReason CloseReason { get; private set; }

    /// <summary>True once <see cref="Close()"/> has begun.</summary>
    public bool IsClosed => Volatile.Read(ref _closeState) != StateOpen;

    /// <summary>Segments queued or in flight (KCP's <c>WaitSnd</c>).</summary>
    public int WaitSnd { get { lock (_lock) return _kcp.WaitSnd; } }

    /// <summary>Reassembled bytes waiting for the reader.</summary>
    public long PendingReceiveBytes => Interlocked.Read(ref _pendingRecvBytes);

    /// <summary>Raised once when the session stops, so the listener can drop it from its table.</summary>
    public event Action<KcpSession>? Closed;

    /// <summary>
    /// Creates a session for <paramref name="conv"/> with the default limits, sending
    /// datagrams through <paramref name="send"/> — which the listener wires to its shared socket.
    /// </summary>
    public KcpSession(uint conv, IPEndPoint remote, Action<ReadOnlyMemory<byte>> send, int headerSize)
        : this(conv, remote, send, headerSize, KcpListenerOptions.Default, null, NullLogger.Instance)
    {
    }

    /// <summary>Creates a session bound by <paramref name="limits"/>, counting into <paramref name="stats"/>.</summary>
    public KcpSession(uint conv, IPEndPoint remote, Action<ReadOnlyMemory<byte>> send, int headerSize,
        KcpListenerOptions limits, KcpListenerStats? stats, ILogger logger)
    {
        Remote = remote;
        _send = send;
        _limits = limits;
        _stats = stats;
        _logger = logger;
        _inbound = new TokenBucket(limits.DatagramsPerSecondPerSession, limits.DatagramBurstPerSession);

        // One reusable datagram buffer per session. KCP only calls output from inside
        // Input/Update/Flush, which all run under _lock, and the listener's SendTo seals and
        // sends synchronously (Socket.SendTo copies), so the bytes are dead once _send
        // returns. Allocating a fresh array per datagram cost one allocation per packet for
        // every player at the snapshot rate.
        var outBuf = new byte[headerSize + KcpTuning.Mtu + Kcp.Overhead];
        _kcp = new Kcp(conv, (buf, size) =>
        {
            // Reserve room for the crypt header the caller fills in; below the KCP
            // layer the datagram is opaque, which is exactly kcp-go's split.
            var packet = headerSize + size <= outBuf.Length ? outBuf : new byte[headerSize + size];
            buf.AsSpan(0, size).CopyTo(packet.AsSpan(headerSize));
            _send(packet.AsMemory(0, headerSize + size));
        });

        KcpTuning.Apply(_kcp, headerSize);
    }

    /// <summary>
    /// Per-session inbound rate limit. Called only from the listener's receive loop, so the
    /// bucket needs no lock. A refused datagram is dropped (KCP retransmits it); the session
    /// is not closed, because a burst is not proof of malice.
    /// </summary>
    internal bool TryAdmitDatagram() => _inbound.TryTake();

    /// <summary>Feeds a decrypted datagram from the listener's receive loop into the ARQ.</summary>
    /// <returns>False when the KCP state machine refused the datagram.</returns>
    internal bool OnDatagram(ReadOnlySpan<byte> data)
    {
        _lastActivityTicks = Environment.TickCount64;
        lock (_lock)
        {
            if (_kcp.Input(data, ackNoDelay: true) < 0) return false;
            DrainLocked();
            ReleaseSendWaitersLocked();
        }
        return true;
    }

    /// <summary>Runs the periodic ARQ update. Called by the listener's update loop.</summary>
    internal void Tick()
    {
        bool dead;
        lock (_lock)
        {
            _kcp.Update();
            dead = _kcp.DeadLinkReached;
            if (!dead) ReleaseSendWaitersLocked();
        }

        if (dead)
        {
            _logger.LogInformation(
                "KCP session {Remote} conv={Conv} closed: dead link (a segment hit the retransmission limit unacknowledged)",
                Remote, Conv);
            Close(KcpCloseReason.DeadLink);
            return;
        }

        if (Environment.TickCount64 - _lastActivityTicks > _limits.IdleTimeoutMs)
        {
            _logger.LogInformation("KCP session {Remote} conv={Conv} closed: no datagram for {Ms}ms",
                Remote, Conv, _limits.IdleTimeoutMs);
            Close(KcpCloseReason.Idle);
        }
    }

    /// <summary>
    /// Moves complete messages out of the ARQ and into the read queue, stopping once the
    /// reader is <see cref="KcpListenerOptions.MaxPendingReceiveBytes"/> behind. What is
    /// left in the ARQ keeps KCP's receive queue full, so the advertised window shrinks to
    /// zero and the peer stops sending until the reader catches up.
    /// </summary>
    private void DrainLocked()
    {
        while (true)
        {
            if (Interlocked.Read(ref _pendingRecvBytes) >= _limits.MaxPendingReceiveBytes)
            {
                Volatile.Write(ref _drainPaused, true);
                return;
            }
            int n = _kcp.Recv(_recvScratch);
            if (n < 0) break;
            if (n == 0) continue;
            var chunk = _recvScratch.AsSpan(0, n).ToArray();
            if (!_received.Writer.TryWrite(chunk)) break; // completed: the reader is gone
            Interlocked.Add(ref _pendingRecvBytes, n);
        }
        Volatile.Write(ref _drainPaused, false);
    }

    private void ReleaseSendWaitersLocked()
    {
        if (_sendSpace != null && _kcp.WaitSnd < _limits.SendQueueSoftLimit)
        {
            _sendSpace.TrySetResult();
            _sendSpace = null;
        }
    }

    /// <summary>
    /// Waits until the send queue is below <see cref="KcpListenerOptions.SendQueueSoftLimit"/>.
    /// This is where a slow peer pushes back on the writer, the way a full TCP send buffer
    /// does; it completes synchronously in the common case.
    /// </summary>
    /// <exception cref="IOException">The session closed while waiting.</exception>
    public async ValueTask WaitForSendSpaceAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (IsClosed) throw new IOException("KCP session closed");
                if (_kcp.WaitSnd < _limits.SendQueueSoftLimit) return;
                _sendSpace ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _sendSpace.Task;
            }

            await wait.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Queues application bytes and flushes immediately. Immediate flush mirrors
    /// the Go side's <c>SetWriteDelay(false)</c>: waiting for the next update would
    /// add up to a full interval of latency to every frame.
    /// </summary>
    /// <exception cref="IOException">
    /// The session is closed, the write is larger than one maximum wire frame
    /// (<see cref="KcpTuning.MaxWriteBytes"/>), KCP refused it, or the send queue passed the
    /// hard limit. All but the first close the session and are logged, so the failure is
    /// visible at both ends rather than leaving a stream with a hole in it. (Before this,
    /// <see cref="Kcp.Send"/>'s return code was ignored and a refused frame vanished.)
    /// </exception>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (IsClosed) throw new IOException("KCP session closed");
        if (data.IsEmpty) return;

        // Checked BEFORE Send, so the stream stays all-or-nothing: nothing of a refused
        // frame is ever queued.
        if (data.Length > KcpTuning.MaxWriteBytes)
        {
            RejectWrite($"{data.Length}-byte write exceeds the maximum frame of {KcpTuning.MaxWriteBytes} bytes");
        }

        int rc = 0;
        int waitSnd;
        int length = data.Length;
        lock (_lock)
        {
            // Split like kcp-go's UDPSession.Write: a single Send may not span more than
            // 255 fragments, and the stream does not care where the pieces break.
            while (!data.IsEmpty && rc == 0)
            {
                int n = Math.Min(data.Length, KcpTuning.WriteChunkBytes);
                rc = _kcp.Send(data[..n]);
                data = data[n..];
            }
            waitSnd = _kcp.WaitSnd;
            if (rc == 0 && waitSnd <= _limits.SendQueueHardLimit) _kcp.Flush();
        }

        if (rc < 0)
        {
            RejectWrite($"KCP refused a {length}-byte write (code {rc}: " +
                        (rc == -2 ? "more than 255 fragments" : "empty write") + ")");
        }

        if (waitSnd > _limits.SendQueueHardLimit)
        {
            if (_stats != null) Interlocked.Increment(ref _stats._closedSlowConsumer);
            _logger.LogWarning(
                "KCP session {Remote} conv={Conv} closed as a slow or dead consumer: {WaitSnd} segments queued or in flight, " +
                "hard limit {Limit}", Remote, Conv, waitSnd, _limits.SendQueueHardLimit);
            Close(KcpCloseReason.SlowConsumer);
            throw new IOException(
                $"KCP send queue exceeded {_limits.SendQueueHardLimit} segments; session closed as a slow consumer");
        }
    }

    private void RejectWrite(string why)
    {
        if (_stats != null) Interlocked.Increment(ref _stats._writesRejected);
        _logger.LogWarning("KCP session {Remote} conv={Conv} closed: {Why}", Remote, Conv, why);
        Close(KcpCloseReason.WriteRejected);
        throw new IOException(why + "; session closed");
    }

    /// <summary>Waits for the next chunk of application bytes, or null when the session ends.</summary>
    public async ValueTask<byte[]?> ReadChunkAsync(CancellationToken ct)
    {
        byte[] chunk;
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token))
        {
            try
            {
                chunk = await _received.Reader.ReadAsync(linked.Token);
            }
            catch (ChannelClosedException) { return null; }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return null; }
        }

        long pending = Interlocked.Add(ref _pendingRecvBytes, -chunk.Length);
        if (pending < _limits.MaxPendingReceiveBytes && Volatile.Read(ref _drainPaused) && !IsClosed)
        {
            // The reader caught up: resume moving messages out of the ARQ. Recv flags a
            // window update once its queue drops below the window, and the next Update
            // sends it, which is what lets the throttled peer resume.
            lock (_lock)
            {
                if (Volatile.Read(ref _drainPaused)) DrainLocked();
            }
        }
        return chunk;
    }

    /// <summary>
    /// The peer finished sending: the reader drains what has already been reassembled and
    /// then sees end-of-stream, the same sequence a TCP FIN produces. KCP has no such signal
    /// on the wire; this exists for in-process peers (tests) that can deliver one.
    /// </summary>
    internal void PeerFinished() => _received.Writer.TryComplete();

    /// <summary>Closes the session. Idempotent.</summary>
    public void Close() => Close(KcpCloseReason.Local);

    /// <summary>Closes the session, recording why. Idempotent: the first reason wins.</summary>
    public void Close(KcpCloseReason reason)
    {
        if (Interlocked.CompareExchange(ref _closeState, StateClosing, StateOpen) != StateOpen) return;
        try
        {
            CloseReason = reason;
            if (_stats != null)
            {
                if (reason == KcpCloseReason.Idle) Interlocked.Increment(ref _stats._closedIdle);
                else if (reason == KcpCloseReason.DeadLink) Interlocked.Increment(ref _stats._closedDeadLink);
            }
            _received.Writer.TryComplete();
            _cts.Cancel();
            lock (_lock)
            {
                _sendSpace?.TrySetException(new IOException("KCP session closed"));
                _sendSpace = null;
            }
            Closed?.Invoke(this);
        }
        finally
        {
            // In a finally: Closed?.Invoke runs subscriber code, and a throw from
            // it must not strand the state at Closing and spin Dispose() forever.
            Volatile.Write(ref _closeState, StateClosed);
        }
    }

    public void Dispose()
    {
        Close();

        // Same reasoning as Connection.Dispose: Close() returning early means
        // "another thread owns the close", not "the close has finished", so the
        // token source must not be freed until that thread is actually done with
        // it.
        var spin = new SpinWait();
        while (Volatile.Read(ref _closeState) != StateClosed) spin.SpinOnce();

        _cts.Dispose();
    }
}
