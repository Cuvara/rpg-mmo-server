using Microsoft.Extensions.Logging;

namespace GameServer.Agones;

/// <summary>Agones game server SDK abstraction.</summary>
public interface IAgonesSdk
{
    /// <summary>
    /// True when this implementation actually talks to a sidecar.
    ///
    /// <para>The health loop keys off it. A loop running against <see cref="NoopAgonesSdk"/>
    /// logs reassuring lines about pings that were never sent, which is worse than no loop —
    /// ADR-14 decision 4.</para>
    /// </summary>
    bool IsEnabled { get; }

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
    /// <inheritdoc/>
    public bool IsEnabled => false;

    /// <inheritdoc/>
    public Task ReadyAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ShutdownAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public Task AllocateAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public Task HealthAsync() => Task.CompletedTask;
}

/// <summary>
/// Real Agones SDK client, speaking the sidecar's <b>HTTP</b> interface on
/// <c>localhost:9358</c> — four POSTs with an empty JSON object as the body:
/// <c>/ready</c>, <c>/health</c>, <c>/allocate</c>, <c>/shutdown</c>.
///
/// <para>HTTP and not gRPC on purpose (ADR-14 decision 1): the official Agones C# SDK is
/// gRPC and would drag <c>Grpc.Net.Client</c> and its transitive tree into a module whose
/// rules are "NativeAOT compatible — no reflection-based serialization" and "no other
/// external dependencies". <c>System.Net.Http</c> is in-box and the body is a string
/// literal, so nothing here needs a serializer at all.</para>
///
/// <para><b>No method on this class ever throws.</b> Every call site is either the server's
/// start-up path or a background loop; an exception escaping into either one turns a sidecar
/// hiccup into a dead game server, which is the opposite of what Agones is for. Failures are
/// logged and swallowed. See <see cref="HealthAsync"/> for the one place where that choice
/// has a consequence worth stating out loud.</para>
/// </summary>
public sealed class HttpAgonesSdk : IAgonesSdk, IDisposable
{
    /// <summary>The Agones sidecar's default HTTP port.</summary>
    public const int DefaultPort = 9358;

    /// <summary>Environment variable overriding <see cref="DefaultPort"/>.</summary>
    public const string PortEnvVar = "AGONES_SDK_HTTP_PORT";

    /// <summary>
    /// Per-request timeout. Deliberately short and well under the fleet's health
    /// <c>periodSeconds</c>: a ping that blocks longer than the health window is
    /// indistinguishable from one that never happened, so failing fast and retrying on the
    /// next loop iteration keeps the pod inside its window instead of hanging past it.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many consecutive health failures pass before the log escalates from warning to
    /// error. At a 2s ping interval this is ~10s of silence — long enough not to shout about
    /// a single dropped request, short enough to appear before Agones' own
    /// <c>failureThreshold</c> kills the pod.
    /// </summary>
    private const int HealthFailureEscalation = 5;

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ILogger _logger;

    private int _consecutiveHealthFailures;

    /// <summary>
    /// Consecutive failed health pings, 0 once one succeeds. Diagnostics and tests: the
    /// pings themselves are fire-and-forget, so this is the only in-process evidence that
    /// the sidecar has stopped answering.
    /// </summary>
    public int ConsecutiveHealthFailures => Volatile.Read(ref _consecutiveHealthFailures);

    /// <summary>The base address requests are sent to. Diagnostics and tests.</summary>
    public string BaseAddress => _http.BaseAddress?.ToString() ?? "";

    /// <inheritdoc/>
    public bool IsEnabled => true;

    /// <summary>
    /// Create a client for the sidecar.
    /// </summary>
    /// <param name="logger">Where failures are reported. Nothing else observes them.</param>
    /// <param name="baseAddress">
    /// Sidecar base address. Null resolves it from <see cref="PortEnvVar"/>, falling back to
    /// <see cref="DefaultPort"/>. Tests point this at a local listener.
    /// </param>
    /// <param name="timeout">Per-request timeout; null uses <see cref="DefaultTimeout"/>.</param>
    public HttpAgonesSdk(ILogger logger, string? baseAddress = null, TimeSpan? timeout = null)
    {
        _logger = logger;
        var resolved = baseAddress ?? $"http://localhost:{ResolvePort(logger)}/";
        _http = new HttpClient
        {
            BaseAddress = new Uri(resolved),
            Timeout = timeout ?? DefaultTimeout,
        };
        _ownsHttpClient = true;
        _logger.LogInformation("Agones HTTP SDK targeting sidecar at {BaseAddress}", resolved);
    }

    /// <summary>Create a client over a caller-owned <see cref="HttpClient"/> (tests).</summary>
    /// <param name="http">Client with <see cref="HttpClient.BaseAddress"/> already set. Not disposed by this instance.</param>
    /// <param name="logger">Where failures are reported.</param>
    public HttpAgonesSdk(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
        _ownsHttpClient = false;
    }

    /// <summary>
    /// Read <see cref="PortEnvVar"/>. An unset, unparsable or out-of-range value falls back to
    /// <see cref="DefaultPort"/> — a typo in a manifest must not stop the server from starting,
    /// because a server that refuses to boot is a restart loop and a server on the wrong port
    /// is one log line.
    /// </summary>
    public static int ResolvePort(ILogger? logger = null)
    {
        var raw = Environment.GetEnvironmentVariable(PortEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultPort;

        if (int.TryParse(raw, out var port) && port > 0 && port <= 65535)
            return port;

        logger?.LogWarning(
            "{EnvVar}='{Raw}' is not a valid port; falling back to {DefaultPort}",
            PortEnvVar, raw, DefaultPort);
        return DefaultPort;
    }

    /// <inheritdoc/>
    public Task ReadyAsync() => PostAsync("ready", "Ready");

    /// <inheritdoc/>
    public Task ShutdownAsync() => PostAsync("shutdown", "Shutdown");

    /// <inheritdoc/>
    public Task AllocateAsync() => PostAsync("allocate", "Allocate");

    /// <summary>
    /// Send one health ping.
    ///
    /// <para><b>On failure this logs and returns normally, and that has a cost worth naming:
    /// Agones restarts the pod when pings stop arriving.</b> So a sidecar that has stopped
    /// answering is a real fault heading for a real restart, and swallowing the exception
    /// hides the cause of it. The mitigation is not to throw — throwing out of the health
    /// loop kills the ping task and guarantees the restart it was trying to report — but to
    /// count: the first failure logs a warning, every
    /// <see cref="HealthFailureEscalation"/>th consecutive failure logs an error naming the
    /// count, and a recovery logs how long the gap was. If a pod is killed, the reason is in
    /// its own log rather than only in the Kubernetes event.</para>
    /// </summary>
    public async Task HealthAsync()
    {
        var ok = await PostCoreAsync("health").ConfigureAwait(false);
        if (ok)
        {
            var failures = Interlocked.Exchange(ref _consecutiveHealthFailures, 0);
            if (failures > 0)
            {
                _logger.LogInformation(
                    "Agones health ping recovered after {Failures} consecutive failures", failures);
            }
            return;
        }

        var count = Interlocked.Increment(ref _consecutiveHealthFailures);
        if (count % HealthFailureEscalation == 0)
        {
            // Loud on purpose: at this point Agones has almost certainly already decided
            // this pod is unhealthy, and the restart that follows should not be a mystery.
            _logger.LogError(
                "Agones health ping has failed {Count} times in a row; the sidecar is not " +
                "answering and Agones will restart this pod if the pings do not resume",
                count);
        }
        else if (count == 1)
        {
            _logger.LogWarning("Agones health ping failed");
        }
    }

    private async Task PostAsync(string path, string label)
    {
        if (await PostCoreAsync(path).ConfigureAwait(false))
        {
            _logger.LogInformation("Agones {Label} reported to sidecar", label);
        }
        else
        {
            // Non-fatal by design. Ready never arriving means Agones never marks this
            // GameServer ready and the allocator will not hand it out — bad, but a running
            // server with no players beats a crashed one, and the next deploy is the fix.
            _logger.LogWarning("Agones {Label} could not be reported to the sidecar", label);
        }
    }

    /// <summary>
    /// POST an empty JSON object to <paramref name="path"/>. Returns false on any failure —
    /// transport error, timeout, or a non-2xx status — and never throws.
    /// </summary>
    private async Task<bool> PostCoreAsync(string path)
    {
        try
        {
            // A fresh StringContent per request: content objects are consumed by the send
            // and cannot be reused, and this is at most one small allocation every 2s off
            // the tick loop's thread.
            using var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(path, body).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return true;

            _logger.LogWarning("Agones sidecar POST /{Path} returned {Status}",
                path, (int)resp.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            // Everything: HttpRequestException (no sidecar), TaskCanceledException (timeout),
            // ObjectDisposedException (racing teardown). Nothing about a sidecar is worth
            // failing the server over.
            _logger.LogWarning(ex, "Agones sidecar POST /{Path} failed", path);
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
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
