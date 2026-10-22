using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using GameServer.Observability;

namespace GameServer.Scaffolding;

/// <summary>
/// Ingress for <c>POST /debug/kill</c>: the HTTP thread leaves a request here and awaits it;
/// <see cref="DebugKillPhase"/> takes it on the tick thread and completes it. Bounded at
/// <see cref="MaxPending"/>.
/// </summary>
/// <remarks>
/// Kept apart from the phase because it is ingress, not simulation state: like the world's
/// input queue, it is filled by another thread and drained by the tick, and it carries nothing
/// a snapshot or a save would need (ADR-12; <c>SimulationStateArchitectureTests</c>).
/// </remarks>
public sealed class DebugKillQueue
{
    /// <summary>Requests that may wait for the tick at once; beyond it a request is answered busy.</summary>
    public const int MaxPending = 64;

    private const int StatePending = 0;
    private const int StateTaken = 1;
    private const int StateWithdrawn = 2;

    /// <summary>One queued kill.</summary>
    public sealed class Request
    {
        internal Request(string id) => Id = id;

        /// <summary>The player to kill (user id = entity id).</summary>
        public string Id { get; }

        internal readonly TaskCompletionSource<DebugKillResult> Done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Pending, then exactly one of taken (by the tick) or withdrawn (by the caller).</summary>
        internal int State;

        /// <summary>Report what the tick did with this request. Tick thread.</summary>
        public void Complete(in DebugKillResult result) => Done.TrySetResult(result);
    }

    private readonly ConcurrentQueue<Request> _pending = new();
    private int _pendingCount;

    /// <summary>True when nothing is waiting. One volatile read; no lock, no allocation.</summary>
    public bool IsEmpty => _pending.IsEmpty;

    /// <summary>
    /// Queue a kill of the player <paramref name="id"/> for the next tick. Any thread. Completes
    /// when the tick has resolved it; cancelling <paramref name="ct"/> before then withdraws it
    /// unapplied and completes it <see cref="DebugKillOutcome.Timeout"/>.
    /// </summary>
    public Task<DebugKillResult> Submit(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        if (Interlocked.Increment(ref _pendingCount) > MaxPending)
        {
            Interlocked.Decrement(ref _pendingCount);
            return Task.FromResult(new DebugKillResult(DebugKillOutcome.Busy, 0, 0));
        }

        var request = new Request(id);
        if (ct.CanBeCanceled)
        {
            ct.Register(static state =>
            {
                var r = (Request)state!;
                if (Interlocked.CompareExchange(ref r.State, StateWithdrawn, StatePending) == StatePending)
                {
                    r.Done.TrySetResult(new DebugKillResult(DebugKillOutcome.Timeout, 0, 0));
                }
            }, request);
        }

        _pending.Enqueue(request);
        return request.Done.Task;
    }

    /// <summary>
    /// Take the next request that has not been withdrawn. Tick thread. The caller must
    /// <see cref="Request.Complete"/> it.
    /// </summary>
    public bool TryTake([NotNullWhen(true)] out Request? request)
    {
        while (_pending.TryDequeue(out request))
        {
            Interlocked.Decrement(ref _pendingCount);
            if (Interlocked.CompareExchange(ref request.State, StateTaken, StatePending) == StatePending)
            {
                return true;
            }
        }

        request = null;
        return false;
    }
}
