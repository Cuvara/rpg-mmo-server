namespace GameServer.Tests.Infrastructure;

/// <summary>
/// A thread that allocates continuously, including large-object-heap arrays that blow the
/// LOH budget, so background gen2 GCs happen throughout a measurement. In a full parallel run
/// other tests provide this by accident; a guard that wants to be deterministic about it
/// takes it on purpose (SC-003, see <see cref="ThreadAllocation"/>).
/// </summary>
internal sealed class BackgroundGcPressure : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;

    private BackgroundGcPressure()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "background-gc-pressure" };
    }

    public static BackgroundGcPressure Start()
    {
        var p = new BackgroundGcPressure();
        p._thread.Start();
        return p;
    }

    private void Run()
    {
        var keep = new List<object>();
        var large = new byte[64][];
        int next = 0;
        while (!_stop)
        {
            for (int i = 0; i < 3000; i++) keep.Add(new byte[300 + (i & 7) * 100]);
            large[next++ % large.Length] = new byte[90_000]; // over the 85,000 B LOH threshold
            if (keep.Count > 20000) keep.Clear();            // survivors, so older generations fill
            Thread.Sleep(0);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}
