using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// A seeded, deterministic TCP relay that sits <b>between a real client socket and the real
/// game server listener</b> and degrades the link the way a mobile network does: added
/// one-way latency, jitter on top of it, a blackout that stops moving bytes in either
/// direction, and an abrupt reset.
/// </summary>
/// <remarks>
/// <para><b>Why a relay and not a fake transport.</b> The cheapest way to write an adversity
/// test is to hand the code under test a stub that returns canned frames on a schedule — and
/// that test then measures the stub. Here nothing is stubbed: the server runs its own
/// <c>TcpListener</c>, the client runs a real <see cref="TcpClient"/>, both speak the real
/// <c>WireProtocol</c> framing, and the only thing this type does is decide <b>when</b> a
/// byte that was already produced by one of them is handed to the other. Shipping code sits
/// on both sides of the injection point.</para>
///
/// <para><b>Why it is pipelined.</b> Read and write run as separate tasks joined by a bounded
/// channel. A single read-delay-write loop would cap throughput at one chunk per delay
/// period, so a 80ms link would have silently throttled a 15Hz snapshot stream to 12.5/s —
/// the proxy would then be the thing measured, which is the failure mode this whole file
/// exists to avoid. The channel is bounded so the link still applies backpressure once the
/// in-flight queue fills, as a real one does.</para>
///
/// <para><b>Why a blackout stops reading.</b> During <see cref="Blackout"/> the reader stops
/// draining the socket as well as the writer stopping writing. That lets the kernel receive
/// buffer fill and the peer's writes block, which is what makes the game server's outbound
/// channel reach its drop-the-oldest path. A proxy that kept reading into unbounded memory
/// would relieve exactly the pressure the test is trying to create.</para>
///
/// <para><b>Determinism.</b> Jitter comes from a <see cref="Random"/> constructed from an
/// explicit seed, one generator per direction, so a run is reproducible and a failure can
/// name the seed. All timing is <see cref="Stopwatch"/>-based: this host's wall clock runs
/// 10–17% fast and has been observed stepping backwards, so <c>DateTime.UtcNow</c> is not
/// usable as a timing base here.</para>
/// </remarks>
internal sealed class AdversityProxy : IAsyncDisposable
{
    /// <summary>How the link behaves in one direction.</summary>
    /// <param name="BaseDelayMs">One-way latency added to every chunk.</param>
    /// <param name="JitterMs">
    /// Peak deviation either side of <paramref name="BaseDelayMs"/>. The drawn delay is
    /// clamped at zero and release times are kept non-decreasing, because this models a
    /// reliable ordered stream: jitter can spread arrivals out, it cannot reorder them.
    /// </param>
    internal readonly record struct Link(int BaseDelayMs, int JitterMs)
    {
        internal static readonly Link Clean = new(0, 0);
    }

    /// <summary>A whole profile: the link in each direction plus the jitter seed.</summary>
    /// <param name="SocketBufferBytes">
    /// Send and receive buffer size forced on both relay sockets, or 0 for the OS default.
    /// <para>This is the difference between a link that is slow and a link that is
    /// <b>narrow</b>, and only the narrow one can make a peer's write block. At the OS
    /// default a loopback socket absorbs several seconds of a one-player snapshot stream, so
    /// a blackout costs nothing and the server's drop-the-oldest path is never reached — the
    /// measured result being that a 1.2s stall at one client drops no frame at all. Shrinking
    /// the buffers models a constrained mobile link and is what puts that path under test.</para>
    /// </param>
    internal readonly record struct Profile(Link ToServer, Link ToClient, int Seed, int SocketBufferBytes = 0)
    {
        internal static Profile Clean(int seed = 1, int socketBufferBytes = 0) =>
            new(Link.Clean, Link.Clean, seed, socketBufferBytes);

        internal static Profile Symmetric(int baseDelayMs, int jitterMs, int seed) =>
            new(new Link(baseDelayMs, jitterMs), new Link(baseDelayMs, jitterMs), seed);

        public override string ToString() =>
            $"toServer={ToServer.BaseDelayMs}±{ToServer.JitterMs}ms " +
            $"toClient={ToClient.BaseDelayMs}±{ToClient.JitterMs}ms seed={Seed}" +
            (SocketBufferBytes > 0 ? $" sockBuf={SocketBufferBytes}B" : "");
    }

    /// <summary>Counters a test can assert on, so "the adversity happened" is measured.</summary>
    internal sealed class Counters
    {
        private long _toServerChunks, _toClientChunks, _toServerBytes, _toClientBytes;
        private long _blackoutMicros;

        internal long ToServerChunks => Interlocked.Read(ref _toServerChunks);
        internal long ToClientChunks => Interlocked.Read(ref _toClientChunks);
        internal long ToServerBytes => Interlocked.Read(ref _toServerBytes);
        internal long ToClientBytes => Interlocked.Read(ref _toClientBytes);
        internal double BlackoutMs => Interlocked.Read(ref _blackoutMicros) / 1000.0;

        internal void CountToServer(int bytes)
        {
            Interlocked.Increment(ref _toServerChunks);
            Interlocked.Add(ref _toServerBytes, bytes);
        }

        internal void CountToClient(int bytes)
        {
            Interlocked.Increment(ref _toClientChunks);
            Interlocked.Add(ref _toClientBytes, bytes);
        }

        internal void AddBlackout(TimeSpan d) => Interlocked.Add(ref _blackoutMicros, (long)(d.TotalMilliseconds * 1000));

        public override string ToString() =>
            $"toServer={ToServerChunks} chunks/{ToServerBytes}B " +
            $"toClient={ToClientChunks} chunks/{ToClientBytes}B blackout={BlackoutMs:F0}ms";
    }

    private const int ReadBufferBytes = 64 * 1024;

    /// <summary>
    /// In-flight chunks per direction. Bounded so the link applies backpressure rather than
    /// absorbing an unbounded burst; 4096 is far above anything a correct run queues, so
    /// hitting it is itself a signal rather than a routine throttle.
    /// </summary>
    private const int ChannelCapacity = 4096;

    private readonly TcpListener _listener;
    private readonly int _upstreamPort;
    private readonly Profile _profile;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _pumps = new();
    private volatile bool _blackoutToServer;
    private volatile bool _blackoutToClient;
    private TcpClient? _downstream, _upstream;
    private Task? _acceptTask;

    internal int Port { get; }
    internal Profile Config => _profile;
    internal Counters Stats { get; } = new();

    private AdversityProxy(TcpListener listener, int port, int upstreamPort, Profile profile)
    {
        _listener = listener;
        Port = port;
        _upstreamPort = upstreamPort;
        _profile = profile;
    }

    /// <summary>
    /// Bind an ephemeral port and relay the first connection that arrives to
    /// <paramref name="upstreamPort"/>. One connection at a time, which is all a client is.
    /// </summary>
    internal static AdversityProxy Start(int upstreamPort, Profile profile)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var proxy = new AdversityProxy(listener, port, upstreamPort, profile);
        proxy._acceptTask = Task.Run(() => proxy.AcceptAsync(proxy._cts.Token));
        return proxy;
    }

    /// <summary>
    /// Stop moving bytes in both directions without closing anything. The link is dark: the
    /// peers do not learn of it, which is precisely why it is the interesting case — a
    /// blackout is indistinguishable from a silent client until TCP itself gives up.
    /// </summary>
    internal void Blackout(bool toServer, bool toClient)
    {
        _blackoutToServer = toServer;
        _blackoutToClient = toClient;
    }

    /// <summary>Blackout in both directions — a link that has gone dark entirely.</summary>
    internal void Blackout(bool on) => Blackout(on, on);

    /// <summary>
    /// Reset both sockets — zero linger, so the peers see an RST, the shape a killed client
    /// or a dropped NAT mapping produces, rather than a polite FIN.
    /// </summary>
    internal void Cut()
    {
        foreach (var s in new[] { _downstream, _upstream })
        {
            if (s == null) continue;
            try { s.Client.LingerState = new LingerOption(true, 0); } catch { /* already gone */ }
            try { s.Close(); } catch { /* already gone */ }
        }
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            var down = await _listener.AcceptTcpClientAsync(ct);
            down.NoDelay = true;
            var up = new TcpClient { NoDelay = true };
            if (_profile.SocketBufferBytes > 0)
            {
                // Applied to the upstream socket BEFORE connecting: the receive window is
                // negotiated in the handshake, so setting it afterwards does not shrink what
                // the server may have in flight.
                up.ReceiveBufferSize = _profile.SocketBufferBytes;
                up.SendBufferSize = _profile.SocketBufferBytes;
                down.ReceiveBufferSize = _profile.SocketBufferBytes;
                down.SendBufferSize = _profile.SocketBufferBytes;
            }
            await up.ConnectAsync(IPAddress.Loopback, _upstreamPort, ct);
            _downstream = down;
            _upstream = up;

            var toServer = new Random(_profile.Seed);
            var toClient = new Random(_profile.Seed ^ 0x5f3759df);

            _pumps.Add(PumpAsync(down.GetStream(), up.GetStream(), _profile.ToServer, toServer, toServer: true, ct));
            _pumps.Add(PumpAsync(up.GetStream(), down.GetStream(), _profile.ToClient, toClient, toServer: false, ct));
        }
        catch (OperationCanceledException) { /* the test finished */ }
        catch (SocketException) { /* listener closed under us */ }
        catch (ObjectDisposedException) { /* listener closed under us */ }
    }

    /// <summary>
    /// One direction: a reader that stamps each chunk with the monotonic instant it becomes
    /// eligible, and a writer that releases it then.
    /// </summary>
    private async Task PumpAsync(
        NetworkStream from, NetworkStream to, Link link, Random rng, bool toServer, CancellationToken ct)
    {
        bool Dark() => toServer ? _blackoutToServer : _blackoutToClient;

        var chan = Channel.CreateBounded<(byte[] Data, double ReleaseMs)>(
            new BoundedChannelOptions(ChannelCapacity) { SingleReader = true, SingleWriter = true });

        double lastRelease = 0;

        var reader = Task.Run(async () =>
        {
            var buf = new byte[ReadBufferBytes];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // A blackout stops the DRAIN, not just the delivery: the kernel receive
                    // buffer then fills and the peer's writes block, which is what drives the
                    // server's outbound channel to its drop path.
                    while (Dark() && !ct.IsCancellationRequested) await Task.Delay(5, ct);

                    int n = await from.ReadAsync(buf, ct);
                    if (n <= 0) break;

                    double now = _clock.Elapsed.TotalMilliseconds;
                    int drawn = link.BaseDelayMs;
                    if (link.JitterMs > 0) drawn += rng.Next(-link.JitterMs, link.JitterMs + 1);
                    if (drawn < 0) drawn = 0;

                    // Non-decreasing release times. This models a reliable ordered stream:
                    // jitter spreads arrivals, it does not reorder them, and asserting on an
                    // order the transport never produces would be testing the fixture.
                    double release = Math.Max(lastRelease, now + drawn);
                    lastRelease = release;

                    var copy = new byte[n];
                    Buffer.BlockCopy(buf, 0, copy, 0, n);
                    await chan.Writer.WriteAsync((copy, release), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { chan.Writer.TryComplete(); }
        }, ct);

        try
        {
            await foreach (var (data, releaseMs) in chan.Reader.ReadAllAsync(ct))
            {
                double wait = releaseMs - _clock.Elapsed.TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);

                var held = Stopwatch.StartNew();
                while (Dark() && !ct.IsCancellationRequested) await Task.Delay(5, ct);
                if (held.Elapsed > TimeSpan.FromMilliseconds(10)) Stats.AddBlackout(held.Elapsed);

                await to.WriteAsync(data, ct);
                await to.FlushAsync(ct);
                if (toServer) Stats.CountToServer(data.Length); else Stats.CountToClient(data.Length);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { try { await reader; } catch { /* already reported */ } }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        Cut();
        if (_acceptTask != null) { try { await _acceptTask; } catch { } }
        foreach (var p in _pumps) { try { await p; } catch { } }
        _downstream?.Dispose();
        _upstream?.Dispose();
        _cts.Dispose();
    }
}
