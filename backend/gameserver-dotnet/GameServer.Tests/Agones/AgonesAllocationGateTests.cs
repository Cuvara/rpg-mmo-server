using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Agones;

/// <summary>
/// <see cref="AgonesAllocationGate"/> in isolation, against a scripted state source.
///
/// <para>What is being pinned is a safety asymmetry, not a happy path: the gate must open on
/// <c>Allocated</c> and on nothing else. Every other answer — another lifecycle state, a
/// failed read, a throwing SDK — has to keep it shut, because a gate that opens on an
/// unreadable state is exactly the bug it was written to prevent (a Ready-but-unallocated pod
/// taking live players, measured on k3d and recorded in ADR-18).</para>
///
/// <para>No cluster and no sidecar: the SDK is a fake, so none of these skip.</para>
/// </summary>
public class AgonesAllocationGateTests
{
    /// <summary>Fast poll so the tests measure logic rather than the 1s production interval.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    /// <summary>Allocated on the first read opens the gate without a delay.</summary>
    [Fact]
    public async Task AllocatedOnTheFirstRead_OpensImmediately()
    {
        var sdk = new ScriptedStateSdk(AgonesGameServerState.Allocated);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.True(await AgonesAllocationGate.WaitForAllocatedAsync(
            sdk, cts.Token, NullLogger.Instance, Poll));
        Assert.Equal(1, sdk.Calls);
    }

    /// <summary>
    /// Ready does not open it. This is the entire point of the change: the fleet's spare
    /// replicas sit in exactly this state.
    /// </summary>
    [Theory]
    [InlineData("Ready")]
    [InlineData("Scheduled")]
    [InlineData("RequestReady")]
    [InlineData("Shutdown")]
    [InlineData("Unhealthy")]
    [InlineData("allocated")]  // case matters: the comparison is ordinal, not a fuzzy match
    public async Task NonAllocatedStates_KeepItShut(string state)
    {
        var sdk = new ScriptedStateSdk(state);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Assert.False(await AgonesAllocationGate.WaitForAllocatedAsync(
            sdk, cts.Token, NullLogger.Instance, Poll));
        Assert.True(sdk.Calls > 1, "the gate stopped polling instead of waiting");
    }

    /// <summary>A state that flips to Allocated part-way through opens the gate then.</summary>
    [Fact]
    public async Task AStateThatBecomesAllocated_OpensIt()
    {
        var sdk = new ScriptedStateSdk(AgonesGameServerState.Ready);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = AgonesAllocationGate.WaitForAllocatedAsync(
            sdk, cts.Token, NullLogger.Instance, Poll);

        await sdk.ObservedAtLeast(3);
        sdk.State = AgonesGameServerState.Allocated;

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// An unreadable state is "keep waiting", never "assume allocated". Null is what a
    /// missing sidecar, a timeout and a non-2xx all reduce to, and treating any of them as
    /// an allocation would register a pod nobody handed out.
    /// </summary>
    [Fact]
    public async Task AnUnreadableState_IsNotTreatedAsAllocated()
    {
        var sdk = new ScriptedStateSdk(null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Assert.False(await AgonesAllocationGate.WaitForAllocatedAsync(
            sdk, cts.Token, NullLogger.Instance, Poll));
        Assert.True(sdk.Calls > 1, "a null read ended the wait");
    }

    /// <summary>
    /// A throwing SDK does not end the wait either. HttpAgonesSdk does not throw, but a
    /// custom implementation that does must not turn one bad read into a permanent
    /// non-registration — or, worse, an unobserved task exception.
    /// </summary>
    [Fact]
    public async Task AThrowingSdk_DoesNotEndTheWait_AndRecoveryStillOpensIt()
    {
        var sdk = new ScriptedStateSdk(AgonesGameServerState.Ready) { Throw = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = AgonesAllocationGate.WaitForAllocatedAsync(
            sdk, cts.Token, NullLogger.Instance, Poll);

        await sdk.ObservedAtLeast(3);
        sdk.Throw = false;
        sdk.State = AgonesGameServerState.Allocated;

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Cancellation returns false rather than throwing out of the caller's task.</summary>
    [Fact]
    public async Task Cancellation_ReturnsFalseWithoutThrowing()
    {
        var sdk = new ScriptedStateSdk(AgonesGameServerState.Ready);
        using var cts = new CancellationTokenSource();

        var wait = AgonesAllocationGate.WaitForAllocatedAsync(
            sdk, cts.Token, NullLogger.Instance, Poll);
        await sdk.ObservedAtLeast(1);
        cts.Cancel();

        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>An SDK whose reported state the test drives, counting reads.</summary>
    private sealed class ScriptedStateSdk : IAgonesSdk
    {
        private string? _state;
        private int _calls;

        public ScriptedStateSdk(string? state) => _state = state;

        public bool IsEnabled => true;

        /// <summary>Reported state; settable mid-wait.</summary>
        public string? State
        {
            get => Volatile.Read(ref _state);
            set => Volatile.Write(ref _state, value);
        }

        /// <summary>When true, <see cref="GetStateAsync"/> throws instead of answering.</summary>
        public bool Throw
        {
            get => Volatile.Read(ref _throw) == 1;
            set => Volatile.Write(ref _throw, value ? 1 : 0);
        }
        private int _throw;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Completes once the gate has read the state at least <paramref name="n"/> times.</summary>
        public async Task ObservedAtLeast(int n)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Calls < n)
            {
                Assert.True(DateTime.UtcNow < deadline, $"the gate polled {Calls} times, expected {n}");
                await Task.Delay(10);
            }
        }

        public Task ReadyAsync() => Task.CompletedTask;
        public Task ShutdownAsync() => Task.CompletedTask;
        public Task AllocateAsync() => Task.CompletedTask;
        public Task HealthAsync() => Task.CompletedTask;
        public Task<AgonesGameServerAddress?> GetAddressAsync() =>
            Task.FromResult<AgonesGameServerAddress?>(null);

        public Task<string?> GetStateAsync()
        {
            Interlocked.Increment(ref _calls);
            if (Throw) throw new InvalidOperationException("scripted sidecar failure");
            return Task.FromResult(State);
        }
    }
}
