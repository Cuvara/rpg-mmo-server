using System.Diagnostics;
using GameServer.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameServer.Tests.Events;

/// <summary>
/// Unit tests for everything in <see cref="RedisEventStream"/> EXCEPT the Redis call:
/// queue bounding, drop-oldest counting, never-throw on a dead sink, dispose-flush.
/// They run against the internal sink seam, so they need no docker and no Redis —
/// the wire contract itself is covered by <see cref="RedisEventStreamRedisTests"/>.
/// </summary>
public sealed class RedisEventStreamQueueTests
{
    private static GameEvent Evt(string type = "entity_killed") =>
        new(type, [1, 2, 3]);

    /// <summary>
    /// Poll until <paramref name="done"/> or the budget expires. Stopwatch, never
    /// DateTime.UtcNow: this host's wall clock runs fast and steps (#153), so a
    /// wall-clock budget shrinks under exactly the load it exists to tolerate.
    /// </summary>
    private static async Task<bool> EventuallyAsync(Func<bool> done, int seconds = 5)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (done()) return true;
            await Task.Delay(20);
        }
        return done();
    }

    [Fact]
    public async Task PublishNeverThrows_WhenEverySinkCallFails()
    {
        // A permanently dead sink (the "Redis is down and the multiplexer cannot help"
        // case). Zero retry delays so the failure path is exercised without waiting out
        // the backoff schedule.
        await using var stream = new RedisEventStream(
            (_, _) => throw new InvalidOperationException("redis is dead"),
            NullLogger.Instance, retryDelays: []);

        for (int i = 0; i < 10; i++)
        {
            // The contract under test: the caller-facing path neither throws nor blocks.
            await stream.PublishAsync(EventStreams.Game, Evt(), CancellationToken.None);
        }

        Assert.True(await EventuallyAsync(() => stream.PublishFailures == 10),
            $"expected 10 publish failures, saw {stream.PublishFailures}");
        Assert.Equal(0, stream.Published);
        Assert.Equal(0, stream.Dropped);
    }

    [Fact]
    public async Task QueueFull_DropsOldest_AndCountsEveryDrop()
    {
        var firstEventEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<string>();

        var stream = new RedisEventStream(
            async (_, evt) =>
            {
                lock (delivered) delivered.Add(evt.Type);
                firstEventEntered.TrySetResult();
                await release.Task;
            },
            NullLogger.Instance, queueCapacity: 2, retryDelays: []);

        // e1 is pulled by the drain and parks inside the sink; the queue is then empty.
        await stream.PublishAsync(EventStreams.Game, Evt("e1"), CancellationToken.None);
        await firstEventEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Five more into a queue bounded at 2: e2, e3, e4 are displaced oldest-first.
        for (int i = 2; i <= 6; i++)
        {
            await stream.PublishAsync(EventStreams.Game, Evt($"e{i}"), CancellationToken.None);
        }

        Assert.True(await EventuallyAsync(() => stream.Dropped == 3),
            $"expected 3 drops, saw {stream.Dropped}");

        release.SetResult();
        await stream.DisposeAsync();

        // Survivors are the newest — the head that was in flight plus the tail two.
        Assert.Equal(["e1", "e5", "e6"], delivered);
        Assert.Equal(3, stream.Published);
        Assert.Equal(3, stream.Dropped);
        Assert.Equal(0, stream.PublishFailures);
    }

    [Fact]
    public async Task Dispose_FlushesQueuedEvents_ThenDropsLateOffers()
    {
        var delivered = new List<string>();
        var stream = new RedisEventStream(
            (_, evt) =>
            {
                lock (delivered) delivered.Add(evt.Type);
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        for (int i = 1; i <= 3; i++)
        {
            await stream.PublishAsync(EventStreams.Game, Evt($"e{i}"), CancellationToken.None);
        }

        await stream.DisposeAsync();
        Assert.Equal(["e1", "e2", "e3"], delivered);
        Assert.Equal(3, stream.Published);

        // After shutdown a publish is still throw-free — the event is dropped, counted.
        await stream.PublishAsync(EventStreams.Game, Evt("late"), CancellationToken.None);
        Assert.Equal(1, stream.Dropped);
        Assert.Equal(3, stream.Published);
    }

    [Fact]
    public async Task TransientFailure_IsRetried_NotDropped()
    {
        int calls = 0;
        var stream = new RedisEventStream(
            (_, _) =>
            {
                // Fail the first attempt, succeed the retry — a reconnect blip.
                return Interlocked.Increment(ref calls) == 1
                    ? Task.FromException(new InvalidOperationException("blip"))
                    : Task.CompletedTask;
            },
            NullLogger.Instance, retryDelays: [TimeSpan.FromMilliseconds(1)]);

        await stream.PublishAsync(EventStreams.Game, Evt(), CancellationToken.None);

        Assert.True(await EventuallyAsync(() => stream.Published == 1),
            "the retried publish never succeeded");
        Assert.Equal(0, stream.PublishFailures);
        Assert.Equal(2, calls);
        await stream.DisposeAsync();
    }
}
