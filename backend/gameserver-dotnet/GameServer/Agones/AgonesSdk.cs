using Microsoft.Extensions.Logging;

namespace GameServer.Agones;

/// <summary>Agones game server SDK abstraction.</summary>
public interface IAgonesSdk
{
    /// <summary>Mark the server as ready to receive connections.</summary>
    Task ReadyAsync();

    /// <summary>Mark the server for shutdown.</summary>
    Task ShutdownAsync();

    /// <summary>Mark the server as allocated.</summary>
    Task AllocateAsync();

    /// <summary>Send a health ping.</summary>
    Task HealthAsync();
}

/// <summary>No-op Agones SDK for local development (no Agones sidecar).</summary>
public sealed class NoopAgonesSdk : IAgonesSdk
{
    public Task ReadyAsync() => Task.CompletedTask;
    public Task ShutdownAsync() => Task.CompletedTask;
    public Task AllocateAsync() => Task.CompletedTask;
    public Task HealthAsync() => Task.CompletedTask;
}

/// <summary>Periodic health check loop for Agones.</summary>
public static class AgonesHealthLoop
{
    /// <summary>Send health pings at the specified interval until cancelled.</summary>
    public static async Task RunAsync(IAgonesSdk sdk, TimeSpan interval, CancellationToken ct, ILogger logger)
    {
        logger.LogInformation("Agones health loop started (interval: {Interval})", interval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await sdk.HealthAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agones health ping failed");
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("Agones health loop stopped");
    }
}
