namespace GameServer.Tests.Infrastructure;

/// <summary>
/// Pins the one test-host setting every zero-allocation guard depends on.
///
/// <para>With background (concurrent) GC on, a background GC running because OTHER tests
/// allocate inflates <see cref="GC.GetAllocatedBytesForCurrentThread"/> for a thread that
/// allocates nothing, in steps of 136 + n*1344 bytes, at arbitrary instructions. In a full
/// parallel run that failed <c>SnapshotTraceTests</c>, <c>SnapshotV3EncoderTests</c> and
/// <c>HitboxHistoryTests</c> about half the time. With
/// <c>DOTNET_gcConcurrent=0</c> the same reproduction passed 12 of 12 (7 of 12 failed with
/// it on). The guards stay exact; the test host is made quiet instead.</para>
/// </summary>
public class AllocationCounterIsolationTests
{
    [Fact]
    public void TestHost_RunsWithBackgroundGcOff()
    {
        Assert.True(AppContext.TryGetSwitch("System.GC.Concurrent", out bool concurrent),
            "System.GC.Concurrent is not set; the csproj ConcurrentGarbageCollection=false did not reach the test host.");
        Assert.False(concurrent);
    }
}
