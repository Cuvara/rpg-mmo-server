using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameServer.Server;

/// <summary>
/// The coarse-sleep half of the tick loop's sleep-then-spin pacing, with a
/// high-resolution timer on Windows.
///
/// <para><b>Why it exists.</b> On Windows a process that has not asked for a finer timer
/// wakes from <c>WaitOne(ms)</c> on the 15.625 ms system quantum. The loop sleeps to
/// 1 ms before its deadline and then spins, but a wake-up that lands up to one quantum late
/// overshoots the deadline, so the spin never runs and ticks are issued on quantum
/// boundaries: 98.4% of 4-tick snapshot intervals were 62.5 or 78.1 ms (stdev 6.9 ms, against
/// 0.4 ms on Linux). Production runs Linux; this matters for developers and for the
/// native-Windows soak host.</para>
///
/// <para><b>How.</b> A waitable timer created with
/// <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c> (Windows 10 1803+) wakes at roughly 0.5-1 ms
/// granularity. It is private to this object and does not change the system-wide timer
/// resolution the way <c>timeBeginPeriod</c> does. The wait is a
/// <see cref="WaitHandle.WaitAny(WaitHandle[], int)"/> on the timer and the cancellation
/// handle, so shutdown still wakes the sleep immediately. P/Invokes are source-generated
/// (<c>LibraryImport</c>), so NativeAOT needs no marshalling stubs or reflection, and the
/// handle array is built once, so a sleep allocates nothing.</para>
///
/// <para>On Linux, or if the timer cannot be created (older Windows), this falls back to
/// <c>ct.WaitHandle.WaitOne</c>, which is exactly the previous behaviour.</para>
/// </summary>
internal sealed partial class TickSleeper : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly WaitHandle _cancel;
    private readonly TimerWaitHandle? _timer;
    private readonly WaitHandle[]? _handles;

    /// <summary>True when sleeps use the high-resolution timer.</summary>
    public bool IsHighResolution => _timer != null;

    public TickSleeper(CancellationToken ct)
    {
        _cancel = ct.WaitHandle;
        if (!OperatingSystem.IsWindows()) return;

        nint raw = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (raw == 0) return; // pre-1803 Windows: keep the coarse wait

        _timer = new TimerWaitHandle(raw);
        _handles = new WaitHandle[] { _timer, _cancel };
    }

    /// <summary>
    /// Sleeps about <paramref name="milliseconds"/>. Returns true when cancellation woke it
    /// (or was already requested) and the caller should stop.
    /// </summary>
    public bool Sleep(int milliseconds)
    {
        if (_timer == null)
            return _cancel.WaitOne(milliseconds);

        // Relative due time: negative, in 100 ns units.
        long dueTime = -(long)milliseconds * 10_000;
        if (!SetWaitableTimer(_timer.SafeWaitHandle.DangerousGetHandle(), in dueTime, 0, 0, 0, 0))
            return _cancel.WaitOne(milliseconds);

        return WaitHandle.WaitAny(_handles!) == 1;
    }

    public void Dispose() => _timer?.Dispose();

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(nint raw) => SafeWaitHandle = new SafeWaitHandle(raw, ownsHandle: true);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", EntryPoint = "SetWaitableTimer")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        nint timer, in long dueTime, int period, nint completionRoutine, nint argToCompletionRoutine, int resume);
}
