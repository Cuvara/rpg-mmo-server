using System.Diagnostics;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// A <b>diagnostic</b> witness for timing tests (#510): dedicated threads that sleep a
/// millisecond at a time, plus a thread-pool latency probe, recording every wake-up that came
/// late. It shares the caller's <see cref="Stopwatch"/>, so the recorded intervals can be
/// printed next to the instants the test took its own readings at.
///
/// <para><b>It excuses nothing.</b> Its output goes into failure messages only, to help tell
/// "the machine was starved" from "the scheduler regressed". It is not proof of either: it
/// runs in the same process as the server (GC pauses stop it too), its probe measures the
/// shared thread pool rather than the host, its watcher threads run at normal priority while
/// the server's tick and KCP threads may run above it, and Windows <c>Sleep(1)</c> returns at
/// roughly 15.6ms granularity. A recorded interval is therefore a "late wake-up", never a
/// verified host stall, and no assertion may depend on it.</para>
/// </summary>
internal sealed class HostStallSentinel : IDisposable
{
    /// <summary>One late wake-up of a watcher thread or of the thread-pool probe.</summary>
    internal readonly record struct Stall(double StartMs, double EndMs)
    {
        internal double DurationMs => EndMs - StartMs;
    }

    private readonly Stopwatch _clock;
    private readonly double _thresholdMs;
    private readonly List<Stall> _stalls = new();
    private readonly Thread[] _threads;
    private readonly Thread _poolProbe;
    private volatile bool _stop;

    /// <param name="clock">The stopwatch the test timestamps its own readings with.</param>
    /// <param name="thresholdMs">Shortest gap between two wake-ups that counts as a stall.</param>
    /// <param name="threads">
    /// How many independent watcher threads. A scheduler starves threads one at a time, so a
    /// single watcher can be run while the threads under test wait; the union of several is a
    /// far better witness to "this process was not being run".
    /// </param>
    internal HostStallSentinel(Stopwatch clock, double thresholdMs, int threads = 4)
    {
        _clock = clock;
        _thresholdMs = thresholdMs;
        _threads = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            _threads[i] = new Thread(Run) { IsBackground = true, Name = $"stall-sentinel-{i}", Priority = ThreadPriority.Normal };
            _threads[i].Start();
        }

        _poolProbe = new Thread(RunPoolProbe) { IsBackground = true, Name = "stall-sentinel-pool", Priority = ThreadPriority.Normal };
        _poolProbe.Start();
    }

    /// <summary>
    /// The thread pool is where the server's and the clients' continuations run, and it can
    /// be starved while every dedicated thread in the process is scheduled normally (the pool
    /// grows slowly when cores are oversubscribed). A watcher thread cannot see that, so this
    /// one queues a work item every few milliseconds and records the ones that waited.
    /// </summary>
    private void RunPoolProbe()
    {
        while (!_stop)
        {
            double queued = _clock.Elapsed.TotalMilliseconds;
            using var ran = new ManualResetEventSlim(false);
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                double started = _clock.Elapsed.TotalMilliseconds;
                if (started - queued >= _thresholdMs)
                {
                    lock (_stalls) _stalls.Add(new Stall(queued, started));
                }
                try { ran.Set(); } catch (ObjectDisposedException) { }
            }, null);
            while (!_stop && !ran.Wait(5)) { }
            Thread.Sleep(2);
        }
    }

    private void Run()
    {
        double last = _clock.Elapsed.TotalMilliseconds;
        while (!_stop)
        {
            Thread.Sleep(1);
            double now = _clock.Elapsed.TotalMilliseconds;
            if (now - last >= _thresholdMs)
            {
                lock (_stalls) _stalls.Add(new Stall(last, now));
            }
            last = now;
        }
    }

    /// <summary>The stalls recorded so far, in time order.</summary>
    internal Stall[] Snapshot()
    {
        lock (_stalls) return _stalls.OrderBy(x => x.StartMs).ToArray();
    }

    public void Dispose()
    {
        _stop = true;
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(2));
        _poolProbe.Join(TimeSpan.FromSeconds(2));
    }
}
