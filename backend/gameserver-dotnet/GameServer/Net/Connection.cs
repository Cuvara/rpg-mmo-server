using System.Net.Sockets;
using System.Threading.Channels;
using GameServer.Net.Transport;
using Microsoft.Extensions.Logging;
using RpgMmo.Wire.V1;

namespace GameServer.Net;

/// <summary>
/// Represents a single player connection. Manages read/write loops over the
/// transport's byte stream using a bounded channel for the send queue
/// (capacity 64, drops oldest on full).
/// </summary>
/// <remarks>
/// The transport is deliberately abstracted behind <see cref="ITransportConnection"/>:
/// the length-prefixed JSON codec only needs a reliable ordered stream, which TCP
/// and KCP both provide, so nothing in this class knows which one it is on.
/// </remarks>
/// <summary>
/// One item in a connection's send queue: either an envelope that is already built, or a
/// marker meaning "a snapshot is staged for you to encode".
///
/// <para>The marker exists so that snapshot encoding happens on the write task, at the
/// moment the frame is about to go out, rather than on the tick thread. Both kinds share
/// one channel with a single reader, which is what makes ordering a property of the
/// structure rather than of discipline: tick N+1's frame physically cannot overtake tick
/// N's, because one task writes them in the order it dequeues them.</para>
/// </summary>
internal readonly struct SendItem
{
    /// <summary>Non-null for a pre-built envelope; null for a snapshot marker.</summary>
    public readonly Envelope? Envelope;

    private SendItem(Envelope? envelope) => Envelope = envelope;

    public static SendItem Snapshot => new(null);

    public static SendItem For(Envelope envelope) => new(envelope);
}

public sealed class Connection : IDisposable
{
    /// <summary>User ID associated with this connection (set after JWT validation).</summary>
    public string UserId { get; }

    /// <summary>
    /// Per-connection snapshot delta encoder state. Lives and dies with the connection,
    /// so a reconnecting client always starts from a fresh keyframe.
    /// </summary>
    public GameServer.Snapshot.SnapshotDeltaState DeltaState { get; }

    /// <summary>
    /// Wire encoding this connection speaks, latched from the first frame decoded
    /// on it and used for every reply.
    /// </summary>
    /// <remarks>
    /// The server never chooses an encoding; it answers in whatever the client
    /// used. That is what lets one server binary serve Protobuf and legacy JSON
    /// clients at once, and lets the gateway, the game server and the Unity
    /// client be upgraded in any order. Defaults to
    /// <see cref="WireEncoding.Json"/> so a reply that somehow precedes any read
    /// stays on the legacy encoding.
    /// </remarks>
    /// <remarks>
    /// <b>volatile:</b> this is written by the read loop and read by the tick
    /// loop, which are different tasks on different threads. A byte-sized write
    /// cannot tear, but without a memory barrier the tick loop could keep
    /// reading a stale value indefinitely and go on serializing snapshots in
    /// the wrong encoding for a client that has already switched.
    /// </remarks>
    private volatile WireEncoding _encoding;

    /// <inheritdoc cref="_encoding"/>
    public WireEncoding Encoding
    {
        get => _encoding;
        private set => _encoding = value;
    }

    /// <summary>
    /// Reusable AOI scratch, <b>double buffered</b>. The tick's gather phase fills one
    /// while the write task may still be encoding the other.
    ///
    /// <para>Two is provably enough: a buffer is only handed to the encoder by
    /// <see cref="TakePendingSnapshot"/>, and the gather phase reuses the pending slot's
    /// buffer while the job is still unclaimed, so at most one buffer is ever being
    /// encoded and at most one is being written.</para>
    ///
    /// <para><b>Thread affinity:</b> index <see cref="_gatherBuffer"/> is written only by
    /// the tick thread; the index handed out to the encoder is read only by the write
    /// task. The handover itself is the only shared state and is under
    /// <see cref="_snapshotLock"/>.</para>
    /// </summary>
    private readonly Shared.GameLogic.Components.EntityState[][] _aoiBuffers =
    {
        Array.Empty<Shared.GameLogic.Components.EntityState>(),
        Array.Empty<Shared.GameLogic.Components.EntityState>(),
    };

    private readonly object _snapshotLock = new();

    /// <summary>
    /// Buffer the tick thread gathers into. Flipped by the <b>tick thread</b> and only
    /// when the previous job was already claimed — so the buffer an encoder is holding
    /// is never the one being written, and an unclaimed job is overwritten in its own
    /// buffer. Flipping it from the write task instead would race with the gather's read
    /// of it.
    /// </summary>
    private int _gatherBuffer;

    /// <summary>Buffer index the staged job refers to.</summary>
    private int _pendingBuffer;

    /// <summary>Set while a staged snapshot is waiting to be encoded.</summary>
    private bool _snapshotPending;

    private int _pendingCount;
    private ulong _pendingTick;
    private ulong _pendingAckTick;
    private int _pendingKeyframeInterval;

    /// <summary>
    /// Read everything this connection needs for its snapshot out of the world, into
    /// buffers this connection owns, and stage it for the write task to encode.
    ///
    /// <para>Called inside a single <see cref="GameServer.World.EcsWorld.ReadAll"/> scope
    /// for the whole broadcast. Nothing here serializes: the tick thread's entire share
    /// of a snapshot is now this gather plus a flag.</para>
    ///
    /// <para><b>Coalescing is the back-pressure policy, and it is lossless.</b> If a
    /// staged snapshot has not been claimed yet, this overwrites it — newest state wins.
    /// That loses nothing, because encoding is lazy: a snapshot that was never encoded
    /// never advanced the delta encoder's <c>_lastSent</c>, so the next one it does
    /// encode carries every change since the last snapshot actually <i>sent</i>. The
    /// previous design was the opposite way round and lost data: it encoded on the tick,
    /// advanced <c>_lastSent</c>, and only then handed the envelope to a bounded channel
    /// that drops the oldest under load — so a dropped frame's updates were gone until
    /// the next keyframe.</para>
    /// </summary>
    internal void GatherSnapshotView(
        GameServer.World.WorldReader reader, float radius, ulong tick, int keyframeInterval)
    {
        reader.TryGetSnapshotAnchor(UserId, out var anchor, out ulong ackTick);

        int index;
        lock (_snapshotLock)
        {
            // Claimed jobs release their buffer, so move to the other one. An unclaimed
            // job is overwritten where it already is — nobody is reading it.
            if (!_snapshotPending)
            {
                _gatherBuffer ^= 1;
            }
            else
            {
                // The write task has not claimed the previous gather yet, so this one
                // replaces it and that tick's frame is never sent. Positionally it loses
                // nothing -- the delta is computed against the last frame actually SENT --
                // but the client receives one fewer snapshot than the server staged, and
                // that is the whole of the gap between `snapshots_sent_total` (which counts
                // stagings) and what a client measures arriving. Counted so the two can be
                // told apart: a client seeing fewer frames than the server sent is either
                // this, or a client that is not reading its socket, and those have opposite
                // fixes.
                SnapshotsCoalesced++;
            }

            index = _gatherBuffer;
        }

        int count = reader.GetEntitiesInRange(anchor, radius, _aoiBuffers[index]);
        if (count > _aoiBuffers[index].Length)
        {
            _aoiBuffers[index] = new Shared.GameLogic.Components.EntityState[count];
            count = reader.GetEntitiesInRange(anchor, radius, _aoiBuffers[index]);
        }

        lock (_snapshotLock)
        {
            _pendingBuffer = index;
            _pendingCount = count;
            _pendingTick = tick;
            _pendingAckTick = ackTick;
            _pendingKeyframeInterval = keyframeInterval;
            _snapshotPending = true;
        }

        // A marker every gather, never conditionally.
        //
        // Gating this on "no job already pending" looks like an obvious optimisation and
        // is a starvation bug: the send queue is bounded and drops the OLDEST item when
        // full, so a dropped marker would leave the job pending forever, no further
        // marker would ever be enqueued, and that connection would stop sending snapshots
        // permanently. Surplus markers are free — the claim returns false and the write
        // task moves on.
        _sendChannel.Writer.TryWrite(SendItem.Snapshot);
    }

    /// <summary>
    /// Claim the staged snapshot, if there is one. Called only by the write task.
    /// Returns false for a surplus marker, which is normal and harmless.
    /// </summary>
    private bool TakePendingSnapshot(
        out Shared.GameLogic.Components.EntityState[] buffer, out int count,
        out ulong tick, out ulong ackTick, out int keyframeInterval)
    {
        lock (_snapshotLock)
        {
            if (!_snapshotPending)
            {
                buffer = Array.Empty<Shared.GameLogic.Components.EntityState>();
                count = 0; tick = 0; ackTick = 0; keyframeInterval = 0;
                return false;
            }

            buffer = _aoiBuffers[_pendingBuffer];
            count = _pendingCount;
            tick = _pendingTick;
            ackTick = _pendingAckTick;
            keyframeInterval = _pendingKeyframeInterval;

            _snapshotPending = false;
            return true;
        }
    }

    /// <summary>
    /// Serialization buffers for this connection's snapshots. Per-connection, like
    /// <see cref="DeltaState"/>, and touched only by this connection's write task — so no
    /// pool is ever shared across threads.
    /// </summary>
    private readonly GameServer.Snapshot.SnapshotFrameWriter _frameWriter = new();

    private readonly ITransportConnection _transport;
    private readonly Stream _stream;
    /// <summary>
    /// Gathers that replaced a staged snapshot the write task had not claimed yet, so that
    /// tick's frame was never sent. See <see cref="GatherSnapshotView"/>.
    /// </summary>
    internal long SnapshotsCoalesced { get; private set; }

    /// <summary>
    /// Snapshot frames this connection has actually written to its socket.
    /// </summary>
    /// <remarks>
    /// The only figure comparable with what a client counts arriving. `snapshots.sent`
    /// counts stagings and `snapshots.coalesced` counts the ones a later gather replaced;
    /// neither says how many frames left the process, and a client measuring fewer frames
    /// than the server staged could be explained by either. Measured live at 14.2/s against
    /// 15/s staged, with coalescing at zero, this is what closes the question.
    /// </remarks>
    internal long SnapshotFramesWritten { get; private set; }

    private long _reportedCoalesced;
    private long _reportedFramesWritten;

    /// <summary>
    /// What this connection has coalesced and written since the last call.
    /// </summary>
    /// <remarks>
    /// Deltas are taken HERE, per connection, rather than by summing the running totals of
    /// whichever connections happen to be in the tick's scratch array. That sum is not a
    /// delta: a connection joining or leaving moves it by that connection's whole history,
    /// and the reading is then wrong by an unbounded amount for as long as the session
    /// lasts. Measured, it reported 17 frames/s written while two clients were each counting
    /// 14.4/s arriving -- a figure that cannot be true, and the kind of counter that sends
    /// an investigation in the wrong direction.
    /// </remarks>
    internal void TakeSnapshotCounters(out long coalesced, out long framesWritten)
    {
        long c = SnapshotsCoalesced;
        long w = SnapshotFramesWritten;

        coalesced = c - _reportedCoalesced;
        framesWritten = w - _reportedFramesWritten;

        _reportedCoalesced = c;
        _reportedFramesWritten = w;
    }

    private readonly Channel<SendItem> _sendChannel;
    private readonly CancellationTokenSource _cts;
    private readonly ILogger _logger;
    // Close lifecycle, three states rather than a bool.
    //
    // A single "already closing" flag is being asked to mean two different things
    // — close BEGUN and close COMPLETE — and Dispose() needs the second. When
    // Close() returned early it told Dispose() "someone else is closing", never
    // "closing has finished", so Dispose() could free the CancellationTokenSource
    // while the other thread was still between its CAS and its Cancel(). That
    // threw ObjectDisposedException out of GameServerHost.ShutdownAsync, which
    // under Agones means terminate fails instead of draining.
    private const int StateOpen = 0;
    private const int StateClosing = 1;
    private const int StateClosed = 2;
    private int _closeState;

    /// <summary>
    /// Managed thread id of the Close() currently in flight, 0 when none is.
    ///
    /// Close() cancels the token source, and cancellation callbacks — including the
    /// resumption of every loop parked on that token — run INLINE on the cancelling
    /// thread. So a cancelled heartbeat or read loop can complete its connection
    /// handler, and that handler can call Dispose(), all on the same stack, below
    /// the Close() that started it. Dispose() has to recognise that stack: spinning
    /// there waits on a frame it is itself blocking.
    /// </summary>
    private int _closingThreadId;

    /// <summary>
    /// Set when a Dispose() re-entered on the closing thread and had to leave the
    /// token source alone. Close() frees it on that Dispose()'s behalf once the
    /// teardown it was in the middle of is actually finished.
    /// </summary>
    private int _disposeDeferred;

    /// <summary>Heartbeat interval — both sides send MsgPing at this cadence.</summary>
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);

    /// <summary>If no MsgPong arrives within this window the connection is declared dead.</summary>
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Monotonic timestamp (ms since epoch) of the last received MsgPong, or
    /// connection creation time. Written by the read loop, read by the heartbeat
    /// task — both through <see cref="Volatile"/> so no lock is needed.
    /// </summary>
    private long _lastPongMs;

    /// <summary>The peer address, for logging.</summary>
    public string RemoteEndPoint => _transport.RemoteEndPoint;

    /// <summary>Creates a connection over an already-accepted transport connection.</summary>
    /// <param name="encoding">
    /// Encoding this connection starts on. The join handshake runs on a
    /// throwaway connection over the same socket and the real one is built
    /// afterwards, so the encoding the client already demonstrated has to be
    /// handed over explicitly — otherwise the very first reply would silently
    /// fall back to legacy JSON.
    /// </param>
    public Connection(string userId, ITransportConnection transport, ILogger logger,
        WireEncoding encoding = WireEncoding.Json)
    {
        UserId = userId;
        // Keyframe phase is derived from the user id, so it is stable across runs and
        // across reconnects of the same player.
        DeltaState = new GameServer.Snapshot.SnapshotDeltaState(
            GameServer.Snapshot.SnapshotDeltaState.PhaseFor(userId));
        Encoding = encoding;
        _transport = transport;
        _stream = transport.Stream;
        _logger = logger;
        _cts = new CancellationTokenSource();

        _lastPongMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _sendChannel = Channel.CreateBounded<SendItem>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Convenience overload for TCP callers (and tests) that already hold a
    /// <see cref="TcpClient"/>. Ownership of the client transfers to this connection.
    /// </summary>
    public Connection(string userId, TcpClient tcp, ILogger logger,
        WireEncoding encoding = WireEncoding.Json)
        : this(userId, new TcpTransportConnection(tcp), logger, encoding)
    {
    }

    /// <summary>Enqueue an envelope for sending. Non-blocking; drops oldest if full.</summary>
    public void Send(Envelope env)
    {
        if (_cts.IsCancellationRequested) return;
        _sendChannel.Writer.TryWrite(SendItem.For(env));
    }

    /// <summary>
    /// Read loop: continuously reads envelopes from the wire and dispatches them via the handler.
    /// Returns when the connection is closed or an error occurs.
    /// </summary>
    public async Task ReadLoopAsync(Func<Connection, Envelope, Task> handler)
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var env = await WireProtocol.DecodeAsync(_stream, _cts.Token);
                if (env == null) break; // clean EOF

                Encoding = env.Encoding;
                await handler(this, env);
            }
        }
        catch (OperationCanceledException) { /* expected on close */ }
        catch (IOException) { /* peer disconnect */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read loop error for user {UserId}", UserId);
        }
    }

    /// <summary>
    /// Write loop: dequeues envelopes from the send channel and writes them to the wire.
    /// Returns when the channel is completed or the connection is closed.
    /// </summary>
    public async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var item in _sendChannel.Reader.ReadAllAsync(_cts.Token))
            {
                Envelope? env = item.Envelope;
                var isSnapshot = false;

                if (env == null)
                {
                    // Snapshot marker: encode now, on this task, immediately before the
                    // write. If the job was already superseded by a newer gather this
                    // claims the newer one; if it was claimed by an earlier marker there
                    // is nothing to do and the marker is simply dropped.
                    if (!TakePendingSnapshot(out var buffer, out int count, out ulong tick,
                                             out ulong ackTick, out int keyframeInterval))
                    {
                        continue;
                    }

                    SnapshotMessage snapshot = DeltaState.Encode(
                        tick, ackTick, buffer.AsSpan(0, count), keyframeInterval,
                        intern: Encoding == WireEncoding.Proto);

                    if (Encoding == WireEncoding.Proto)
                    {
                        // Serialize straight into this connection's reused buffers. The
                        // frame is valid until the next WriteFrame call on this writer,
                        // and the await below completes before we can loop back to it.
                        // JSON keeps the allocating path: it is not the production
                        // encoding, and JsonWriter would need its own reuse story.
                        ReadOnlyMemory<byte> pooledFrame =
                            _frameWriter.WriteFrame((byte)MsgType.Snapshot, snapshot);
                        await _stream.WriteAsync(pooledFrame, _cts.Token);
                        await _stream.FlushAsync(_cts.Token);
                        SnapshotFramesWritten++;
                        continue;
                    }

                    env = WireProtocol.NewEnvelope(MsgType.Snapshot, snapshot, Encoding);
                    isSnapshot = true;
                }

                byte[] frame = WireProtocol.Encode(env);
                await _stream.WriteAsync(frame, _cts.Token);
                await _stream.FlushAsync(_cts.Token);
                if (isSnapshot) SnapshotFramesWritten++;
            }
        }
        catch (OperationCanceledException) { /* expected on close */ }
        catch (IOException) { /* peer disconnect */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Write loop error for user {UserId}", UserId);
        }
    }

    /// <summary>Read a single envelope from the wire (used during handshake).</summary>
    public async Task<Envelope?> ReadOneAsync()
    {
        var env = await WireProtocol.DecodeAsync(_stream, _cts.Token);
        if (env != null) Encoding = env.Encoding;
        return env;
    }

    /// <summary>Write a single envelope to the wire (used during handshake).</summary>
    public async Task WriteOneAsync(Envelope env)
    {
        byte[] frame = WireProtocol.Encode(env);
        await _stream.WriteAsync(frame, _cts.Token);
        await _stream.FlushAsync(_cts.Token);
    }

    /// <summary>Record that a MsgPong was received, resetting the heartbeat timer.</summary>
    public void RecordPong() =>
        Volatile.Write(ref _lastPongMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// Reply to an incoming MsgPing with a MsgPong carrying the original
    /// timestamp and the server's current wall clock.
    /// </summary>
    public void HandlePing(PingMessage ping)
    {
        var pong = new PongMessage
        {
            Timestamp = ping.Timestamp,
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        var env = WireProtocol.NewEnvelope(MsgType.Pong, pong, Encoding);
        Send(env);
    }

    /// <summary>
    /// Heartbeat loop: sends MsgPing every 10 s and closes the connection if
    /// no MsgPong has been received within 30 s. Intended to run as a
    /// fire-and-forget task alongside the read/write loops.
    /// </summary>
    public async Task HeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(PingInterval);
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                var last = Volatile.Read(ref _lastPongMs);
                var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last;
                if (elapsed > (long)PongTimeout.TotalMilliseconds)
                {
                    _logger.LogInformation("Heartbeat timeout for {UserId}", UserId);
                    Close();
                    return;
                }

                var ping = new PingMessage
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var env = WireProtocol.NewEnvelope(MsgType.Ping, ping, Encoding);
                Send(env);
            }
        }
        catch (OperationCanceledException) { /* connection closed */ }
    }

    /// <summary>Close the connection. Idempotent.</summary>
    public void Close()
    {
        if (Interlocked.CompareExchange(ref _closeState, StateClosing, StateOpen) != StateOpen) return;
        Volatile.Write(ref _closingThreadId, Environment.CurrentManagedThreadId);
        try
        {
            _cts.Cancel();
            _sendChannel.Writer.TryComplete();
            try { _transport.Close(); } catch { /* ignore */ }
        }
        finally
        {
            // In a finally so a throw from a cancellation callback cannot strand
            // the state at Closing and spin Dispose() forever.
            Volatile.Write(ref _closingThreadId, 0);
            Volatile.Write(ref _closeState, StateClosed);

            // A Dispose() that re-entered on this very stack could not free the
            // token source while we were still using it, so it left the job here.
            // Safe now: every use of _cts above has returned.
            if (Interlocked.Exchange(ref _disposeDeferred, 0) != 0) _cts.Dispose();
        }
    }

    public void Dispose()
    {
        Close();

        // Re-entered on the thread that is still inside Close()? Then the Close()
        // we are waiting on is a frame further down this same stack — cancellation
        // resumed a loop inline, that loop finished its handler, and the handler
        // called us. Spinning would block the very frame that has to finish first,
        // which is a hang, not a wait. Hand the disposal back to that Close() and
        // return: it frees the token source once it is genuinely done with it.
        if (Volatile.Read(ref _closingThreadId) == Environment.CurrentManagedThreadId)
        {
            Volatile.Write(ref _disposeDeferred, 1);
            return;
        }

        // Wait for an in-flight Close() on another thread to FINISH before
        // freeing the token source it is still using. Close() returning is not
        // enough: it returns immediately when someone else owns the close.
        //
        // A spin rather than a lock because CancellationTokenSource.Cancel runs
        // registered callbacks inline, and holding a lock across arbitrary user
        // code invites a deadlock. The wait is bounded by the length of Close(),
        // which is a cancel, a channel completion and a socket close.
        var spin = new SpinWait();
        while (Volatile.Read(ref _closeState) != StateClosed) spin.SpinOnce();

        _cts.Dispose();
    }
}
