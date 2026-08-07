using System.Net.Sockets;
using System.Threading.Channels;
using GameServer.Net.Transport;
using Microsoft.Extensions.Logging;

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

    private readonly ITransportConnection _transport;
    private readonly Stream _stream;
    private readonly Channel<Envelope> _sendChannel;
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

        _sendChannel = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(64)
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
        _sendChannel.Writer.TryWrite(env);
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
            await foreach (var env in _sendChannel.Reader.ReadAllAsync(_cts.Token))
            {
                byte[] frame = WireProtocol.Encode(env);
                await _stream.WriteAsync(frame, _cts.Token);
                await _stream.FlushAsync(_cts.Token);
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

    /// <summary>Close the connection. Idempotent.</summary>
    public void Close()
    {
        if (Interlocked.CompareExchange(ref _closeState, StateClosing, StateOpen) != StateOpen) return;
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
            Volatile.Write(ref _closeState, StateClosed);
        }
    }

    public void Dispose()
    {
        Close();

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
