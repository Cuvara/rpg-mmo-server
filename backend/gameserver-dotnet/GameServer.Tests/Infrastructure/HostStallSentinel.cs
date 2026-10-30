using System.Diagnostics;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// An independent witness to host stalls (#510): a dedicated thread that sleeps a
/// millisecond at a time and records every time it was not scheduled again for a long while.
///
/// <para><b>Why it exists.</b> A timing test that fails because the machine stopped running
/// the test process for 200ms is reporting on the machine, and "re-run until green" teaches
/// people to stop reading failures. The sentinel turns that suspicion into a measurement: it
/// shares the caller's <see cref="Stopwatch"/>, so a stall is a pair of instants that can be
/// compared with the instants the test took its own readings at. A stall that does not
/// overlap the readings in question explains nothing, and a quiet sentinel proves the host
/// was fine — so the failure stands.</para>
///
/// <para><b>At normal priority, deliberately.</b> The threads whose starvation produces the
/// gaps under test (the server's tick and KCP threads, the observer's client loop) run at
/// normal priority. A sentinel boosted above them is scheduled ahead of a contended core
/// while they wait, and reports a quiet host during exactly the stall it exists to witness
/// (measured: 312ms gaps with zero stalls recorded by an above-normal sentinel).</para>
///
/// <para>It runs on its own thread, not the thread pool, for the same reason the
/// <see cref="AdversityProxy"/> pumps do: pool starvation is one of the stalls it has to be
/// able to see, so it cannot depend on the pool.</para>
/// </summary>
internal sealed class HostStallSentinel : IDisposable
{
    /// <summary>One interval during which the sentinel thread was not scheduled.</summary>
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
