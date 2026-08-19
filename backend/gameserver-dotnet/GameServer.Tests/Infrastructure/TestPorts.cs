using System.Net;
using System.Net.Sockets;
using GameServer.Net.Transport;
using GameServer.Server;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// The one place tests get a bound TCP port from.
/// <para>
/// Every test class used to carry its own copy of a <c>FreeTcpPort()</c> helper that bound
/// port 0, read the number, <b>closed the listener</b> and handed the number to whatever
/// would bind it later. Between the close and that later bind the port belongs to nobody,
/// so a sibling test — or a second call to the same helper, since the kernel happily hands
/// out a port it just took back — could take it. That is what produced the intermittent
/// <c>SocketException : Address already in use</c>. Seven copies of the same race is how it
/// survived: fixing one taught the suite nothing about the others.
/// </para>
/// <para>
/// There is no single right answer for every binder, so this offers two, and the choice is
/// about who owns the socket:
/// <list type="bullet">
/// <item><see cref="StartServerAsync"/> — the binder can report what it bound. Nothing is
/// predicted, so there is no window at all. Use this whenever possible.</item>
/// <item><see cref="Lease"/> — the binder cannot report, and needs a number up front. The
/// socket is held until the instant before the handoff, which makes concurrent leases
/// mutually exclusive; it narrows the window rather than removing it.</item>
/// </list>
/// </para>
/// </summary>
internal static class TestPorts
{
    /// <summary>A running server and the port it actually bound.</summary>
    internal readonly record struct RunningServer(Task RunTask, int Port);

    /// <summary>
    /// Start <paramref name="server"/> on an ephemeral port and return the port the kernel
    /// gave it, read back from the listener itself via
    /// <see cref="GameServerHost.ListeningAddressAsync"/>. The returned task is the server's
    /// <c>RunAsync</c>, still running; the caller owns cancelling and awaiting it.
    /// <para>
    /// Nothing is guessed and nothing is released: the socket named by the returned port is
    /// the socket the server is listening on. Callers should leave
    /// <c>ServerOptions.ServerAddr</c> at ":0" too, so anything the server advertises about
    /// itself (registry entries) matches what it bound.
    /// </para>
    /// </summary>
    public static async Task<RunningServer> StartServerAsync(
        GameServerHost server, CancellationToken ct, TimeSpan? timeout = null)
    {
        var runTask = server.RunAsync(":0", ct);
        string addr = await server.ListeningAddressAsync.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), ct);
        return new RunningServer(runTask, TransportFactory.ParseAddr(addr).Port);
    }

    /// <summary>
    /// A port held open until you dispose it.
    /// <para>
    /// For binders that cannot be asked what they bound and must be told a number instead —
    /// <see cref="System.Net.HttpListener"/> is the case in this suite: its prefixes require
    /// a literal port, it has no ephemeral-bind mode, and it reports nothing back. Holding
    /// the socket means two leases alive at the same time can never name the same port,
    /// which is the collision that actually fires inside a parallel test class. It does
    /// <b>not</b> make the handoff atomic: between <see cref="Dispose"/> and the real bind
    /// the port is free, and an unrelated process can still win. Prefer
    /// <see cref="StartServerAsync"/> wherever the binder can report.
    /// </para>
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private readonly TcpListener _listener;
        private bool _released;

        public Lease()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        /// <summary>The held port. Valid until <see cref="Dispose"/>.</summary>
        public int Port { get; }

        /// <summary>Release the port. Bind immediately afterwards, not later.</summary>
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            try { _listener.Stop(); } catch { /* teardown */ }
        }
    }

    /// <summary>
    /// Take a <see cref="Lease"/>, release it, bind — and when that loses the race, do the
    /// whole thing again with a <b>new</b> lease.
    /// <para>
    /// <see cref="Lease"/> narrows the lease-to-bind window but cannot close it: the port is
    /// nobody's between <see cref="Lease.Dispose"/> and the real bind, and the kernel will
    /// re-issue a port it has just taken back. A suite doing ~25 of these handoffs per run
    /// across collections xUnit runs in parallel loses that race regularly. Retrying is the
    /// only fix available to a binder that must be told a literal number
    /// (<see cref="System.Net.HttpListener"/>), because there is no port it can be given that
    /// is guaranteed still free a microsecond later — but a *different* port almost certainly
    /// is.
    /// </para>
    /// <para>
    /// <paramref name="bind"/> is called with a just-released port and must either return the
    /// bound object, or signal failure in one of the two ways a binder in this suite can:
    /// throwing (<see cref="System.Net.HttpListenerException"/> /
    /// <see cref="SocketException"/>, as a raw <c>HttpListener</c> does) or returning
    /// <c>null</c> (as <c>MetricsEndpoint.TryStart</c> does — it swallows the bind error by
    /// design, since a metrics endpoint must not kill the game server).
    /// </para>
    /// <para>
    /// This weakens nothing. A genuine bind regression fails on every attempt and the caller
    /// still sees the original exception, or still sees <c>null</c> and still fails its
    /// assertion. Only a transient collision is absorbed.
    /// </para>
    /// </summary>
    /// <param name="bind">Binds to the supplied port; returns null or throws on failure.</param>
    /// <param name="attempts">How many fresh ports to try before giving up.</param>
    /// <returns>The bound object, or null when every attempt returned null.</returns>
    public static T? BindWithRetry<T>(Func<int, T?> bind, int attempts = 5) where T : class
    {
        Exception? lastThrow = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            int port;
            using (var lease = new Lease()) { port = lease.Port; }

            try
            {
                var bound = bind(port);
                if (bound is not null) return bound;
                lastThrow = null; // it failed by returning null, not by throwing
            }
            catch (Exception ex) when (ex is HttpListenerException or SocketException)
            {
                lastThrow = ex;
            }

            // Back off a little so a genuinely busy moment is not retried five times
            // inside the same microsecond.
            if (attempt < attempts) Thread.Sleep(20 * attempt);
        }

        // Every attempt failed. Reproduce the failure the caller would have seen without
        // the retry, so a real regression looks exactly like it always did.
        if (lastThrow is not null) throw lastThrow;
        return null;
    }
}
