using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using GameServer.Net.Transport;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// A real KCP client for the in-process game server: a UDP socket, the server's own
/// <see cref="Kcp"/> state machine configured with the production profile
/// (<see cref="KcpTuning"/>), optional <see cref="KcpCrypto"/>, a random conversation id
/// and its own update loop. It replaced <c>TcpClient</c> in every gameplay test when the
/// server went KCP-only, and deliberately keeps the same surface (<c>ConnectAsync</c>,
/// <c>GetStream</c>, <c>Dispose</c>) so that swap was mechanical.
/// </summary>
/// <remarks>
/// <para><b>What KCP cannot say, and how this client fills the gap.</b> KCP has no
/// handshake, no FIN and no RST. Two socket behaviours the suite relies on therefore have
/// no wire equivalent, and this client models them explicitly rather than pretending:</para>
/// <list type="bullet">
/// <item><b>Connect.</b> <see cref="ConnectAsync(IPAddress, int)"/> sends a zero-length
/// PUSH at <c>sn = 0</c> (legal KCP: <c>len = 0</c>, delivered as an empty message the
/// server's stream skips) and waits for its ACK, so the server has a session — and a
/// pending handshake — when the call returns, as it did after a TCP connect.</item>
/// <item><b>EOF.</b> A read returns 0 once the server's session for this conversation is
/// gone from every in-process listener (<see cref="KcpListener.FindLiveSession"/>) and the
/// last datagram has had time to land. That is the observation a socket peer made by
/// reading EOF; it is in-process only, which every user of this type is.</item>
/// <item><b>Graceful close.</b> <see cref="Dispose"/> waits for its outstanding data to be
/// acknowledged and then tells the server session the peer finished
/// (<see cref="KcpSession.PeerFinished"/>): the server reads what was sent, then EOF —
/// the FIN sequence. <see cref="Abort"/> instead just goes silent, which is all a vanished
/// KCP client ever does; the server finds out by idle timeout or dead link.</item>
/// </list>
/// <para><b>Receive backpressure.</b> Like the server session, the client stops draining
/// the ARQ once <see cref="ReceiveBufferBytes"/> are waiting for the reader, so a test
/// that stops reading closes the KCP window and pushes back on the server, as a full
/// socket receive buffer did.</para>
/// </remarks>
public sealed class KcpTestClient : IDisposable
{
    /// <summary>
    /// Turns on the listener's conv index before any test can create a listener, so every
    /// session in this process is findable by <see cref="KcpListener.FindLiveSession"/>.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void EnableSessionIndex() => KcpListener.TrackSessionsByConv = true;

    private static readonly ConcurrentDictionary<KcpTestClient, byte> s_live = new();
    private static readonly object s_loopGate = new();
    private static Thread? s_loop;

    private readonly object _lock = new();
    private readonly Socket _socket;
    private readonly KcpCrypto? _crypto;
    private readonly int _headerSize;
    private readonly Kcp _kcp;
    private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly byte[] _recvScratch = new byte[KcpTuning.MaxMessageSize];
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IPEndPoint? _remote;
    private Thread? _recvThread;
    private long _establishedMs = -1;
    private volatile bool _sawServerSession;
    private KcpTestStream? _stream;
    private long _pendingRecvBytes;
    private bool _drainPaused;
    private long _lastRecvMs = -1;
    private long _datagramsSent, _datagramsReceived;
    private volatile bool _established;
    private volatile bool _remoteGone;
    private int _disposed;

    /// <summary>Creates a client. A non-empty <paramref name="transportKey"/> turns on kcp-go AES.</summary>
    /// <param name="transportKey">Pre-shared key, or null/empty for plaintext.</param>
    /// <param name="localPort">Local UDP port to bind (0 = ephemeral). Binding the port a
    /// previous client used models a restarted client behind the same address and port.</param>
    public KcpTestClient(string? transportKey = null, int localPort = 0)
    {
        _crypto = KcpCrypto.TryCreate(transportKey);
        _headerSize = _crypto != null ? KcpCrypto.HeaderSize : 0;
        Conv = NewConv();

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // Same reason as the listener: an ICMP port-unreachable (server not bound yet,
            // or restarting) must not surface as a receive error that ends the loop.
            const int SIO_UDP_CONNRESET = -1744830452;
            try { _socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch (SocketException) { }
        }
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, localPort));
        try { _socket.ReceiveBufferSize = 1 << 20; } catch (SocketException) { }

        _kcp = new Kcp(Conv, (buf, size) =>
        {
            var packet = new byte[_headerSize + size];
            buf.AsSpan(0, size).CopyTo(packet.AsSpan(_headerSize));
            SendDatagram(packet);
        });
        KcpTuning.Apply(_kcp, _headerSize);
    }

    /// <summary>
    /// What else is alive in this process — clients still ticking, KCP sessions on any
    /// listener, thread-pool backlog — for wall-clock tests to print next to a timing they
    /// are about to take.
    /// </summary>
    public static string Ambient() =>
        $"liveClients={s_live.Count} liveSessions={KcpListener.LiveSessionCount} " +
        $"poolThreads={ThreadPool.ThreadCount} poolPending={ThreadPool.PendingWorkItemCount}";

    /// <summary>The conversation id, random per client.</summary>
    public uint Conv { get; }

    /// <summary>Accepted for source compatibility with <c>TcpClient</c>; KCP writes always flush.</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>Reassembled bytes the client buffers for its reader before closing its window.</summary>
    public int ReceiveBufferBytes { get; set; } = 256 * 1024;

    /// <summary>How long ConnectAsync waits for the opening segment's ACK.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The client's own UDP endpoint (what the server sees, absent a proxy).</summary>
    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>True between a successful connect and EOF or disposal.</summary>
    public bool Connected => _established && !_remoteGone && Volatile.Read(ref _disposed) == 0;

    /// <summary>Datagrams this client has sent.</summary>
    public long DatagramsSent => Interlocked.Read(ref _datagramsSent);

    /// <summary>Datagrams this client has received from its server.</summary>
    public long DatagramsReceived => Interlocked.Read(ref _datagramsReceived);

    /// <summary>Segments queued or in flight.</summary>
    public int WaitSnd { get { lock (_lock) return _kcp.WaitSnd; } }

    private static uint NewConv()
    {
        Span<byte> b = stackalloc byte[4];
        uint v;
        do { RandomNumberGenerator.Fill(b); v = BitConverter.ToUInt32(b); } while (v == 0);
        return v;
    }

    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>; see <see cref="ConnectAsync(IPAddress, int)"/>.</summary>
    public Task ConnectAsync(string host, int port) => ConnectAsync(ResolveHost(host), port, CancellationToken.None);

    /// <summary>Connects to <paramref name="host"/>:<paramref name="port"/>.</summary>
    public Task ConnectAsync(string host, int port, CancellationToken ct) => ConnectAsync(ResolveHost(host), port, ct);

    /// <summary>Opens the conversation and waits for the server to acknowledge it.</summary>
    public Task ConnectAsync(IPAddress address, int port) => ConnectAsync(address, port, CancellationToken.None);

    /// <summary>
    /// Opens the conversation (a zero-length PUSH at sn 0) and waits until the server has
    /// acknowledged it, so a session exists server-side when this returns.
    /// </summary>
    /// <exception cref="SocketException">No acknowledgement within <see cref="ConnectTimeout"/>
    /// (<see cref="SocketError.TimedOut"/>) — the analogue of a refused TCP connect, so
    /// existing retry loops keep working.</exception>
    public async Task ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var target = new IPEndPoint(address.Equals(IPAddress.Any) ? IPAddress.Loopback : address, port);
        if (_remote != null)
        {
            // A retry loop calling again after a timed-out connect: the opening segment is
            // still being retransmitted by KCP, so just keep waiting for its ACK.
            if (_established || !_remote.Equals(target)) throw new InvalidOperationException("already connected");
        }
        else
        {
            _remote = target;
            // A dedicated thread, like the listener's: a test suite full of blocking waits
            // can starve the thread pool for seconds, and an async receive loop would then
            // stall the ARQ and show up as a network fault that is not there.
            _recvThread = new Thread(() => ReceiveLoop(_cts.Token)) { IsBackground = true, Name = $"kcp-client-{Conv:x8}", Priority = ThreadPriority.AboveNormal };
            _recvThread.Start();
            Register(this);

            lock (_lock)
            {
                _kcp.SendEmptySegment();
                _kcp.Flush();
            }
        }

        var sw = Stopwatch.StartNew();
        while (!_established)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_kcp.WaitSnd == 0)
                {
                    Interlocked.Exchange(ref _establishedMs, _clock.ElapsedMilliseconds);
                    _established = true;
                }
            }
            if (_established) break;
            if (sw.Elapsed > ConnectTimeout)
            {
                throw new SocketException((int)SocketError.TimedOut);
            }
            await Task.Delay(2, ct);
        }
    }

    /// <summary>Synchronous connect, for call sites that used <c>TcpClient.Connect</c>.</summary>
    public void Connect(IPAddress address, int port) => ConnectAsync(address, port).GetAwaiter().GetResult();

    /// <summary>Synchronous connect, for call sites that used <c>TcpClient.Connect</c>.</summary>
    public void Connect(string host, int port) => ConnectAsync(host, port).GetAwaiter().GetResult();

    private static IPAddress ResolveHost(string host) =>
        host is "localhost" or "" ? IPAddress.Loopback : IPAddress.Parse(host);

    /// <summary>The reliable byte stream. Disposing it closes the client, as a NetworkStream did.</summary>
    public Stream GetStream()
    {
        if (_remote == null) throw new InvalidOperationException("not connected");
        return _stream ??= new KcpTestStream(this);
    }

    /// <summary>
    /// Sends <paramref name="datagram"/> verbatim from this client's socket to its server,
    /// bypassing KCP — for malformed-input tests. Encryption is not applied.
    /// </summary>
    public void SendRaw(ReadOnlySpan<byte> datagram)
    {
        if (_remote == null) throw new InvalidOperationException("not connected");
        _socket.SendTo(datagram, SocketFlags.None, _remote);
    }

    /// <summary>
    /// The server's session for this conversation, if one is live in this process. Tests
    /// use it to assert server-side state (pending bytes, window, close reason).
    /// </summary>
    public KcpSession? ServerSession => KcpListener.FindLiveSession(Conv);

    /// <summary>Graceful close: see the type remarks. Idempotent.</summary>
    public void Close() => Dispose();

    /// <summary>
    /// Graceful close. Waits (bounded) for outstanding data to be acknowledged, then signals
    /// end-of-stream to the in-process server session and stops.
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_established && !_remoteGone)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                lock (_lock) { if (_kcp.WaitSnd == 0) break; }
                Thread.Sleep(2);
            }
            KcpListener.FindLiveSession(Conv)?.PeerFinished();
        }
        Stop();
    }

    /// <summary>
    /// Vanish: stop sending and receiving without telling anyone — a killed client, a
    /// dropped NAT mapping. The server learns only through its idle timeout or dead link.
    /// </summary>
    public void Abort() => Stop();

    private void Stop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        s_live.TryRemove(this, out _);
        _cts.Cancel();
        _received.Writer.TryComplete();
        try { _socket.Close(); } catch { }
        _socket.Dispose();
        try { _recvThread?.Join(TimeSpan.FromSeconds(1)); } catch { }
        _crypto?.Dispose();
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static void Register(KcpTestClient c)
    {
        s_live.TryAdd(c, 0);
        lock (s_loopGate)
        {
            if (s_loop == null)
            {
                s_loop = new Thread(UpdateLoop) { IsBackground = true, Name = "kcp-client-update", Priority = ThreadPriority.AboveNormal };
                s_loop.Start();
            }
        }
    }

    /// <summary>One loop drives every client's ARQ timer, like the listener's.</summary>
    private static void UpdateLoop()
    {
        while (true)
        {
            foreach (var c in s_live.Keys) c.Tick();
            Thread.Sleep(KcpTuning.Interval);
        }
    }

    private void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_lock)
        {
            try { _kcp.Update(); } catch (ObjectDisposedException) { }
        }
        if (!_established || _remoteGone) return;
        if (KcpListener.FindLiveSession(Conv) != null)
        {
            _sawServerSession = true;
        }
        else
        {
            // The server's session ended. Let the last datagrams it flushed land before
            // the reader is allowed to see EOF. A session this client never observed
            // gets a longer grace, so a lookup racing the opener can never fake an EOF.
            long now = _clock.ElapsedMilliseconds;
            long last = Interlocked.Read(ref _lastRecvMs);
            bool quiet = now - Math.Max(last, 0) > 100;
            bool settled = _sawServerSession || now - Interlocked.Read(ref _establishedMs) > 1000;
            if (quiet && settled)
            {
                _remoteGone = true;
                _received.Writer.TryComplete();
                // Nothing left to drive: a client a test forgot to dispose must not keep
                // costing the shared loop for the rest of the run.
                s_live.TryRemove(this, out _);
            }
        }
    }

    private void SendDatagram(byte[] packet)
    {
        if (Volatile.Read(ref _disposed) != 0 || _remote == null) return;
        try
        {
            _crypto?.Seal(packet);
            _socket.SendTo(packet, SocketFlags.None, _remote);
            Interlocked.Increment(ref _datagramsSent);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private void ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            int received;
            EndPoint fromEp = any;
            try { received = _socket.ReceiveFrom(buffer, SocketFlags.None, ref fromEp); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }

            var from = (IPEndPoint)fromEp;
            if (from.Port != _remote!.Port) continue;

            Span<byte> data = buffer.AsSpan(0, received);
            if (_crypto != null)
            {
                data = _crypto.Open(data);
                if (data.IsEmpty) continue;
            }

            Interlocked.Increment(ref _datagramsReceived);
            Interlocked.Exchange(ref _lastRecvMs, _clock.ElapsedMilliseconds);
            lock (_lock)
            {
                if (_kcp.Input(data, ackNoDelay: true) < 0) continue;
                DrainLocked();
            }
        }
    }

    private void DrainLocked()
    {
        while (true)
        {
            if (Interlocked.Read(ref _pendingRecvBytes) >= ReceiveBufferBytes)
            {
                _drainPaused = true;
                return;
            }
            int n = _kcp.Recv(_recvScratch);
            if (n < 0) break;
            if (n == 0) continue;
            if (!_received.Writer.TryWrite(_recvScratch.AsSpan(0, n).ToArray())) break;
            Interlocked.Add(ref _pendingRecvBytes, n);
        }
        _drainPaused = false;
    }

    private void Consumed(int bytes)
    {
        long pending = Interlocked.Add(ref _pendingRecvBytes, -bytes);
        if (pending < ReceiveBufferBytes)
        {
            lock (_lock)
            {
                if (_drainPaused) DrainLocked();
            }
        }
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(KcpTestClient));
        if (data.IsEmpty) return;
        lock (_lock)
        {
            // Large test payloads (oversized-frame tests) go in MaxMessageSize pieces: the
            // client side is a byte stream and must not refuse what a socket would accept.
            while (!data.IsEmpty)
            {
                int n = Math.Min(data.Length, KcpTuning.MaxMessageSize / 2);
                int rc = _kcp.Send(data[..n]);
                if (rc < 0) throw new IOException($"KCP refused a {n}-byte write (code {rc})");
                data = data[n..];
            }
            _kcp.Flush();
        }
    }

    /// <summary>The client's byte stream.</summary>
    private sealed class KcpTestStream(KcpTestClient owner) : Stream
    {
        private byte[]? _pending;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            if (_pending == null)
            {
                if (Volatile.Read(ref owner._disposed) != 0) throw new ObjectDisposedException(nameof(KcpTestClient));
                try
                {
                    if (!await owner._received.Reader.WaitToReadAsync(ct)) return 0;
                }
                catch (ChannelClosedException) { return 0; }
                if (!owner._received.Reader.TryRead(out var chunk)) return 0;
                owner.Consumed(chunk.Length);
                _pending = chunk;
                _offset = 0;
            }
            int n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            if (_offset >= _pending.Length) _pending = null;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            owner.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => owner.Write(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) owner.Dispose();
            base.Dispose(disposing);
        }
    }
}
