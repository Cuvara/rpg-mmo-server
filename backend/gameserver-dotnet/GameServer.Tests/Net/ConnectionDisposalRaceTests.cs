
using System.Net;
using GameServer.Net.Transport;
using GameServer.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Net;

/// <summary>
/// Close() and Dispose() racing on the same connection.
/// </summary>
/// <remarks>
/// <para>
/// This is the shutdown path: <c>GameServerHost.ShutdownAsync</c> →
/// <c>ConnectionManager.CloseAll</c> → <c>Connection.Close</c>, running
/// concurrently with the per-connection handler's <c>finally { conn?.Dispose() }</c>.
/// Under Agones a throw there means terminate fails instead of draining.
/// </para>
/// <para>
/// The interleaving is narrow — one thread must be preempted between winning the
/// CAS and calling Cancel() — so a single Close/Dispose pair almost never hits
/// it. These tests hammer it from many threads over many iterations, which is
/// what makes them fail on the broken code rather than merely pass on the fixed
/// code. A test that cannot fail first proves nothing here: the broken version
/// passed CI on develop.
/// </para>
/// </remarks>
public class ConnectionDisposalRaceTests
{
    // The race window is the handful of instructions between Close()'s CAS and
    // its _cts.Cancel(). Hitting it needs volume, so a connection has to be cheap
    // to build — a real TCP pair costs milliseconds and would let setup dominate.
    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = Stream.Null;
        public string RemoteEndPoint => "test";
        public void Close() { }
        public void Dispose() { }
    }

    private static Connection NewConnection() =>
        new("racer", new NullTransport(), NullLogger.Instance);

    /// <summary>
    /// Run a racing pair repeatedly for a fixed wall-clock budget, oversubscribing
    /// cores so the scheduler preempts often, and return whatever was thrown.
    /// </summary>
    private static List<Exception> Hammer(TimeSpan budget, int racersPerConnection)
    {
        var failures = new List<Exception>();
        var deadline = DateTime.UtcNow + budget;
        long rounds = 0;

        while (DateTime.UtcNow < deadline && failures.Count == 0)
        {
            var conn = NewConnection();
            using var gate = new Barrier(racersPerConnection);
            var threads = new Thread[racersPerConnection];

            for (int t = 0; t < racersPerConnection; t++)
            {
                bool dispose = t % 2 == 0;
                threads[t] = new Thread(() =>
                {
                    gate.SignalAndWait();
                    try
                    {
                        if (dispose) conn.Dispose();
                        else conn.Close();
                    }
                    catch (Exception ex)
                    {
                        lock (failures) failures.Add(ex);
                    }
                })
                { IsBackground = true };
                threads[t].Start();
            }

            foreach (var th in threads) th.Join(TimeSpan.FromSeconds(5));
            rounds++;
        }

        Console.WriteLine($"[race] {rounds} rounds, {failures.Count} failure(s)");
        return failures;
    }

    /// <summary>
    /// Many threads calling Close() and Dispose() at once must never throw.
    /// Oversubscribes cores so the scheduler preempts inside Close().
    /// </summary>
    [Fact]
    public void ConcurrentCloseAndDispose_NeverThrows()
    {
        var failures = Hammer(TimeSpan.FromSeconds(8), racersPerConnection: 32);

        Assert.True(failures.Count == 0,
            $"{failures.Count} thread(s) threw during concurrent Close/Dispose. First: {failures.FirstOrDefault()}");
    }

    /// <summary>
    /// The narrowest form: exactly one Close() against one Dispose(), released
    /// together. Dispose() must not free the CancellationTokenSource while
    /// Close() is still mid-flight — Close() being STARTED is not Close() being
    /// FINISHED.
    /// </summary>
    [Fact]
    public void DisposeWaitsForAnInFlightCloseToFinish()
    {
        var failures = Hammer(TimeSpan.FromSeconds(8), racersPerConnection: 2);

        Assert.True(failures.Count == 0,
            $"Dispose() freed the CancellationTokenSource while Close() was still using it. First: {failures.FirstOrDefault()}");
    }

    /// <summary>
    /// <c>KcpSession</c> had the identical Close/Dispose shape and would have been
    /// the next one to throw. Fixed at the same time rather than waiting for a KCP
    /// deployment to find it, so it is hammered the same way.
    /// </summary>
    [Fact]
    public void KcpSession_ConcurrentCloseAndDispose_NeverThrows()
    {
        var failures = new List<Exception>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);

        while (DateTime.UtcNow < deadline && failures.Count == 0)
        {
            var session = new KcpSession(1, new IPEndPoint(IPAddress.Loopback, 1), _ => { }, 0);
            using var gate = new Barrier(8);
            var threads = new Thread[8];

            for (int t = 0; t < threads.Length; t++)
            {
                bool dispose = t % 2 == 0;
                threads[t] = new Thread(() =>
                {
                    gate.SignalAndWait();
                    try
                    {
                        if (dispose) session.Dispose();
                        else session.Close();
                    }
                    catch (Exception ex) { lock (failures) failures.Add(ex); }
                })
                { IsBackground = true };
                threads[t].Start();
            }
            foreach (var th in threads) th.Join(TimeSpan.FromSeconds(5));
        }

        Assert.True(failures.Count == 0,
            $"KcpSession threw during concurrent Close/Dispose. First: {failures.FirstOrDefault()}");
    }

    /// <summary>
    /// Close() must stay idempotent and safe after Dispose() — teardown genuinely
    /// calls them in both orders.
    /// </summary>
    [Fact]
    public void CloseAfterDispose_IsSafe()
    {
        var conn = NewConnection();

        conn.Dispose();
        conn.Close();
        conn.Dispose();
    }
}
