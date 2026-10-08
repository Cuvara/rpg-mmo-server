using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// A seeded, deterministic UDP datagram impairment proxy that sits <b>between a real KCP
/// client socket and the real game server's UDP port</b> and degrades the link the way a
/// mobile network does: one-way latency, jitter on top of it, random loss, duplication,
/// reordering, a blackout that delivers nothing in either direction, and a permanent cut.
/// </summary>
/// <remarks>
/// <para><b>Why a relay and not a fake transport.</b> The cheapest way to write an adversity
/// test is to hand the code under test a stub that returns canned frames on a schedule — and
/// that test then measures the stub. Here nothing is stubbed: the server runs its own
/// <c>KcpListener</c>, the client runs a real <see cref="KcpTestClient"/>, both speak the
/// real KCP ARQ and the real <c>WireProtocol</c> framing, and the only thing this type does
/// is decide <b>whether and when</b> a datagram one of them produced is handed to the other.
/// Loss, duplication and reordering are therefore recovered — or not — by the shipping
/// KCP code, which is the point.</para>
///
/// <para><b>Datagrams, not bytes.</b> This used to be a TCP relay, where the only possible
/// impairments were delay and stall. Over UDP every impairment a real link applies can be
/// applied: a lost datagram is gone, a blackout loses everything in flight, and a cut is
/// silence — there is no RST to tell either peer.</para>
///
/// <para><b>Ordering.</b> With <see cref="Link.Reorder"/> false (the default) release times
/// are kept non-decreasing, so jitter spreads arrivals without reordering them; with it true
/// every datagram's delay is drawn independently and jitter reorders. Duplication and loss
/// are independent of both.</para>
///
/// <para><b>Determinism.</b> Loss, duplication and jitter come from a <see cref="Random"/>
/// per direction constructed from an explicit seed, so the impairment pattern for a given
/// sequence of datagrams is reproducible and a failure can name the seed. All timing is
/// <see cref="Stopwatch"/>-based: this host's wall clock runs 10–17% fast and has been
/// observed stepping backwards, so <c>DateTime.UtcNow</c> is not usable here.</para>
/// </remarks>
internal sealed class AdversityProxy : IAsyncDisposable
{
    /// <summary>How the link behaves in one direction.</summary>
    /// <param name="BaseDelayMs">One-way latency added to every datagram.</param>
    /// <param name="JitterMs">Peak deviation either side of <paramref name="BaseDelayMs"/>, clamped at zero.</param>
    /// <param name="LossRate">Probability in [0, 1) that a datagram is dropped.</param>
    /// <param name="DuplicateRate">Probability in [0, 1) that a datagram is delivered twice.</param>
    /// <param name="Reorder">Draw each delay independently, so jitter reorders datagrams.</param>
    internal readonly record struct Link(
        int BaseDelayMs, int JitterMs, double LossRate = 0, double DuplicateRate = 0, bool Reorder = false)
    {
        internal static readonly Link Clean = new(0, 0);

        public override string ToString() =>
            $"{BaseDelayMs}±{JitterMs}ms" +
            (LossRate > 0 ? $" loss={LossRate:P0}" : "") +
            (DuplicateRate > 0 ? $" dup={DuplicateRate:P0}" : "") +
            (Reorder ? " reorder" : "");
    }

    /// <summary>A whole profile: the link in each direction plus the seed.</summary>
    /// <param name="QueueBytes">
    /// Bytes a direction may hold in flight before further datagrams are tail-dropped, or 0
    /// for no limit — a narrow link's router queue rather than a merely slow one.
    /// </param>
    internal readonly record struct Profile(Link ToServer, Link ToClient, int Seed, int QueueBytes = 0)
    {
        internal static Profile Clean(int seed = 1, int queueBytes = 0) =>
            new(Link.Clean, Link.Clean, seed, queueBytes);

        internal static Profile Symmetric(int baseDelayMs, int jitterMs, int seed) =>
            new(new Link(baseDelayMs, jitterMs), new Link(baseDelayMs, jitterMs), seed);

        internal static Profile Symmetric(Link link, int seed) => new(link, link, seed);

        public override string ToString() =>
            $"toServer={ToServer} toClient={ToClient} seed={Seed}" +
            (QueueBytes > 0 ? $" queue={QueueBytes}B" : "");
    }

    /// <summary>Counters a test can assert on, so "the adversity happened" is measured.</summary>
    internal sealed class Counters
    {
        private long _toServerChunks, _toClientChunks, _toServerBytes, _toClientBytes;
        private long _lost, _blackedOut, _duplicated, _queueDropped, _reordered;
        private long _blackoutMicros;

        /// <summary>Datagrams delivered to the server.</summary>
        internal long ToServerChunks => Interlocked.Read(ref _toServerChunks);
        /// <summary>Datagrams delivered to the client.</summary>
        internal long ToClientChunks => Interlocked.Read(ref _toClientChunks);
        internal long ToServerBytes => Interlocked.Read(ref _toServerBytes);
        internal long ToClientBytes => Interlocked.Read(ref _toClientBytes);
        /// <summary>Datagrams dropped by the random loss model.</summary>
        internal long Lost => Interlocked.Read(ref _lost);
        /// <summary>Datagrams dropped because the link was dark or cut.</summary>
        internal long BlackedOut => Interlocked.Read(ref _blackedOut);
        /// <summary>Extra copies injected by the duplication model.</summary>
        internal long Duplicated => Interlocked.Read(ref _duplicated);
        /// <summary>Datagrams tail-dropped by <see cref="Profile.QueueBytes"/>.</summary>
        internal long QueueDropped => Interlocked.Read(ref _queueDropped);
        /// <summary>Datagrams scheduled to overtake one sent before them.</summary>
        internal long Reordered => Interlocked.Read(ref _reordered);
        internal double BlackoutMs => Interlocked.Read(ref _blackoutMicros) / 1000.0;

        internal void Delivered(bool toServer, int bytes)
        {
            if (toServer)
            {
                Interlocked.Increment(ref _toServerChunks);
                Interlocked.Add(ref _toServerBytes, bytes);
            }
            else
            {
                Interlocked.Increment(ref _toClientChunks);
                Interlocked.Add(ref _toClientBytes, bytes);
            }
        }

        internal void CountLost() => Interlocked.Increment(ref _lost);
        internal void CountBlackedOut() => Interlocked.Increment(ref _blackedOut);
        internal void CountDuplicated() => Interlocked.Increment(ref _duplicated);
        internal void CountQueueDropped() => Interlocked.Increment(ref _queueDropped);
        internal void CountReordered() => Interlocked.Increment(ref _reordered);
        internal void AddBlackout(TimeSpan d) => Interlocked.Add(ref _blackoutMicros, (long)(d.TotalMilliseconds * 1000));

        public override string ToString() =>
            $"toServer={ToServerChunks} dgrams/{ToServerBytes}B toClient={ToClientChunks} dgrams/{ToClientBytes}B " +
            $"lost={Lost} blackedOut={BlackedOut} duplicated={Duplicated} reordered={Reordered} queueDropped={QueueDropped} " +
            $"blackout={BlackoutMs:F0}ms";
    }

    private readonly record struct Item(byte[] Data, Socket Via, IPEndPoint To, bool ToServer);

    /// <summary>One direction of the link: its impairment model and its release queue.</summary>
    private sealed class Direction
    {
        public readonly Link Link;
        public readonly Random Rng;
        public readonly PriorityQueue<Item, (double Release, long Seq)> Queue = new();
        public readonly SemaphoreSlim Signal = new(0);
        public double LastRelease;
        public long Seq;
        public long QueuedBytes;

        public Direction(Link link, int seed)
        {
            Link = link;
            Rng = new Random(seed);
        }
    }

    private const int ReadBufferBytes = 64 * 1024;

    private readonly Socket _front;
    private readonly IPEndPoint _upstream;
    private readonly Profile _profile;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPEndPoint, Socket> _flows = new();
    private readonly List<Thread> _threads = new();
    private readonly Direction _toServer, _toClient;
    private volatile bool _blackoutToServer;
    private volatile bool _blackoutToClient;
    private volatile bool _cut;
    private long _blackoutStartTicks = -1;

    internal int Port { get; }
    internal Profile Config => _profile;
    internal Counters Stats { get; } = new();

    private AdversityProxy(int upstreamPort, Profile profile)
    {
        _upstream = new IPEndPoint(IPAddress.Loopback, upstreamPort);
        _profile = profile;
        _toServer = new Direction(profile.ToServer, profile.Seed);
        _toClient = new Direction(profile.ToClient, profile.Seed ^ 0x5f3759df);
        _front = NewSocket();
        Port = ((IPEndPoint)_front.LocalEndPoint!).Port;
    }

    private static Socket NewSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            try { s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch (SocketException) { }
        }
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        try { s.ReceiveBufferSize = 4 << 20; s.SendBufferSize = 4 << 20; } catch (SocketException) { }
        return s;
    }

    /// <summary>
    /// Bind an ephemeral UDP port and relay every client that sends to it to
    /// <paramref name="upstreamPort"/>, each through its own upstream socket so the server
    /// sees one endpoint per client.
    /// </summary>
    internal static AdversityProxy Start(int upstreamPort, Profile profile)
    {
        var proxy = new AdversityProxy(upstreamPort, profile);
        var ct = proxy._cts.Token;
        // Dedicated threads rather than thread-pool tasks: under a busy test run the pool
        // can lag by seconds, and a relay that stalls is indistinguishable from the network
        // fault it is supposed to be injecting on purpose.
        proxy.StartThread("proxy-front", () => proxy.FrontLoop(ct));
        proxy.StartThread("proxy-to-server", () => proxy.Pump(proxy._toServer, ct));
        proxy.StartThread("proxy-to-client", () => proxy.Pump(proxy._toClient, ct));
        return proxy;
    }

    private void StartThread(string name, Action body)
    {
        var t = new Thread(() => body()) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
        lock (_threads) _threads.Add(t);
        t.Start();
    }

    /// <summary>
    /// Deliver nothing in the given directions, without telling anyone. Datagrams arriving
    /// (or due for release) while a direction is dark are lost, as on a real link; KCP's
    /// retransmission is what recovers once it comes back.
    /// </summary>
    internal void Blackout(bool toServer, bool toClient)
    {
        bool wasDark = _blackoutToServer || _blackoutToClient;
        _blackoutToServer = toServer;
        _blackoutToClient = toClient;
        bool isDark = toServer || toClient;
        if (!wasDark && isDark) Interlocked.Exchange(ref _blackoutStartTicks, _clock.ElapsedTicks);
        else if (wasDark && !isDark)
        {
            long start = Interlocked.Exchange(ref _blackoutStartTicks, -1);
            if (start >= 0) Stats.AddBlackout(TimeSpan.FromSeconds((_clock.ElapsedTicks - start) / (double)Stopwatch.Frequency));
        }
    }

    /// <summary>Blackout in both directions — a link that has gone dark entirely.</summary>
    internal void Blackout(bool on) => Blackout(on, on);

    /// <summary>
    /// Cut the link for good: every datagram in either direction is dropped from now on.
    /// Over UDP there is no RST, so neither peer learns of it — the server finds out through
    /// its KCP idle timeout or dead-link detection, exactly as it would in the field.
    /// </summary>
    internal void Cut() => _cut = true;

    private void FrontLoop(CancellationToken ct)
    {
        var buf = new byte[ReadBufferBytes];
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            int n;
            EndPoint from = any;
            try { n = _front.ReceiveFrom(buf, SocketFlags.None, ref from); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }

            var client = (IPEndPoint)from;
            var up = _flows.GetOrAdd(client, c =>
            {
                var s = NewSocket();
                StartThread("proxy-upstream", () => UpstreamLoop(s, c, ct));
                return s;
            });
            Offer(_toServer, buf.AsSpan(0, n), up, _upstream, toServer: true);
        }
    }

    private void UpstreamLoop(Socket up, IPEndPoint client, CancellationToken ct)
    {
        var buf = new byte[ReadBufferBytes];
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            int n;
            EndPoint from = any;
            try { n = up.ReceiveFrom(buf, SocketFlags.None, ref from); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }
            Offer(_toClient, buf.AsSpan(0, n), _front, client, toServer: false);
        }
    }

    /// <summary>Apply loss, duplication, queue limit and delay, then schedule.</summary>
    private void Offer(Direction d, ReadOnlySpan<byte> datagram, Socket via, IPEndPoint to, bool toServer)
    {
        if (_cut || (toServer ? _blackoutToServer : _blackoutToClient))
        {
            Stats.CountBlackedOut();
            return;
        }

        lock (d)
        {
            if (d.Link.LossRate > 0 && d.Rng.NextDouble() < d.Link.LossRate)
            {
                Stats.CountLost();
                return;
            }
            int copies = d.Link.DuplicateRate > 0 && d.Rng.NextDouble() < d.Link.DuplicateRate ? 2 : 1;
            if (copies == 2) Stats.CountDuplicated();

            for (int i = 0; i < copies; i++)
            {
                if (_profile.QueueBytes > 0 && d.QueuedBytes + datagram.Length > _profile.QueueBytes)
                {
                    Stats.CountQueueDropped();
                    continue;
                }

                double now = _clock.Elapsed.TotalMilliseconds;
                int drawn = d.Link.BaseDelayMs;
                if (d.Link.JitterMs > 0) drawn += d.Rng.Next(-d.Link.JitterMs, d.Link.JitterMs + 1);
                if (drawn < 0) drawn = 0;
                double release = now + drawn + i; // a duplicate trails its original by 1ms
                if (!d.Link.Reorder)
                {
                    release = Math.Max(d.LastRelease, release);
                }
                else if (i == 0 && release < d.LastRelease)
                {
                    Stats.CountReordered();
                }
                d.LastRelease = Math.Max(d.LastRelease, release);

                d.QueuedBytes += datagram.Length;
                d.Queue.Enqueue(new Item(datagram.ToArray(), via, to, toServer), (release, d.Seq++));
            }
        }
        d.Signal.Release();
    }

    /// <summary>Release each datagram at its scheduled instant.</summary>
    private void Pump(Direction d, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Item item = default;
                bool due = false;
                double wait = 50;
                lock (d)
                {
                    if (d.Queue.TryPeek(out var head, out var pri))
                    {
                        wait = pri.Release - _clock.Elapsed.TotalMilliseconds;
                        if (wait <= 0)
                        {
                            d.Queue.Dequeue();
                            d.QueuedBytes -= head.Data.Length;
                            item = head;
                            due = true;
                        }
                    }
                }

                if (due)
                {
                    if (_cut || (item.ToServer ? _blackoutToServer : _blackoutToClient))
                    {
                        Stats.CountBlackedOut();
                        continue;
                    }
                    try
                    {
                        item.Via.SendTo(item.Data, SocketFlags.None, item.To);
                        Stats.Delivered(item.ToServer, item.Data.Length);
                    }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { return; }
                    continue;
                }

                d.Signal.Wait(TimeSpan.FromMilliseconds(Math.Clamp(wait, 1, 50)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _front.Close(); } catch { }
        foreach (var s in _flows.Values) { try { s.Close(); } catch { } }
        Thread[] threads;
        lock (_threads) threads = _threads.ToArray();
        foreach (var t in threads) { try { t.Join(TimeSpan.FromSeconds(2)); } catch { } }
        _front.Dispose();
        foreach (var s in _flows.Values) s.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
