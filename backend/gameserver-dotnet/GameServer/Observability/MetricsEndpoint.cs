using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace GameServer.Observability;

/// <summary>
/// Hosts the observability HTTP surface of the game server:
/// <c>/metrics</c> (OpenTelemetry Prometheus exposition) and <c>/healthz</c>.
///
/// Both paths live on the same TCP port but are served by two independent
/// <see cref="HttpListener"/> registrations (the OpenTelemetry exporter owns
/// its own listener). Everything runs on background threads — the tick loop is
/// never blocked, and gauge callbacks execute on the scrape thread.
/// </summary>
public sealed class MetricsEndpoint : IAsyncDisposable
{
    /// <summary>Histogram bucket boundaries in seconds, sized for 10-15Hz ticks.</summary>
    private static readonly double[] TickDurationBuckets =
    [
        0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 1.0
    ];

    private readonly MeterProvider _meterProvider;
    private readonly HttpListener _healthListener;
    private readonly HttpListener? _statusListener;
    private readonly HttpListener? _contentListener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private Task? _healthTask;
    private Task? _statusTask;
    private Task? _contentTask;
    private Func<(byte[] Bytes, string Hash)>? _contentProvider;
    private Func<ServerStatus>? _statusProvider;

    /// <summary>Prefix the Prometheus exporter is bound to (e.g. <c>http://+:9101/</c>).</summary>
    public string UriPrefix { get; }

    private MetricsEndpoint(MeterProvider meterProvider, HttpListener healthListener, HttpListener? statusListener, HttpListener? contentListener, string uriPrefix, ILogger logger)
    {
        _meterProvider = meterProvider;
        _healthListener = healthListener;
        _statusListener = statusListener;
        _contentListener = contentListener;
        _logger = logger;
        UriPrefix = uriPrefix;
    }

    /// <summary>
    /// Register the callback that produces <c>/status</c> JSON responses.
    /// Must be called after the server is fully wired up (tick loop, spawner, stores).
    /// </summary>
    public void SetStatusProvider(Func<ServerStatus> provider) => _statusProvider = provider;

    /// <summary>
    /// Register the callback that supplies the content document served at <c>/content</c>.
    /// </summary>
    /// <remarks>
    /// Returns the canonical bytes read from disk and their hash — deliberately not a
    /// serialisable object. Re-serialising here would mean clients parse a document the
    /// server never read, so a defect in this writer would surface as a defect in their
    /// reader. Serving the original bytes keeps the hash a statement about a file.
    /// </remarks>
    public void SetContentProvider(Func<(byte[] Bytes, string Hash)> provider) => _contentProvider = provider;

    /// <summary>
    /// Start the metrics endpoint, or return <c>null</c> when disabled.
    /// </summary>
    /// <param name="addr">
    /// Listen address in Go style (<c>":9101"</c>, <c>"0.0.0.0:9101"</c>,
    /// <c>"localhost:9101"</c>). Null or empty disables metrics entirely.
    /// </param>
    /// <param name="metrics">Instrument set whose meter is registered on the provider.</param>
    /// <param name="serviceInstanceId">Server ID reported as the OTel service instance.</param>
    /// <param name="logger">Logger for startup / scrape errors.</param>
    /// <returns>The running endpoint, or <c>null</c> if metrics are disabled.</returns>
    public static MetricsEndpoint? TryStart(
        string? addr,
        GameMetrics metrics,
        string serviceInstanceId,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(addr))
        {
            logger.LogInformation("Metrics endpoint disabled (METRICS_ADDR is empty)");
            return null;
        }

        // Same off-switch vocabulary as the Go gateway's resolveMetricsAddr, so one
        // METRICS_ADDR value means the same thing to both binaries. Without this,
        // METRICS_ADDR=off reached int.Parse below and killed the server on startup
        // with a bare FormatException — for a value that reads like "turn it off".
        if (addr.Trim() is var trimmed &&
            (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase)
             || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
             || trimmed.Equals("disabled", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogInformation("Metrics endpoint disabled (METRICS_ADDR={Addr})", trimmed);
            return null;
        }

        (string host, int port) parsed;
        try
        {
            parsed = ParseAddr(addr);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // Non-fatal, matching how a failed bind is treated a few lines down: a
            // mistyped metrics address costs metrics, not the game server. Logged as
            // an error rather than swallowed, so the mistake is visible.
            logger.LogError(
                "Metrics endpoint disabled: METRICS_ADDR={Addr} is not a host:port, a port, " +
                "or one of off/none/disabled", addr);
            return null;
        }

        var (host, port) = parsed;

        // On Windows, binding the "+" wildcard prefix requires an URL ACL
        // (admin-only). Fall back to localhost so unprivileged dev runs still
        // get a working endpoint; Linux (the production target) binds "+" fine
        // (see the UriBuilder note in TryStartOn for what "fine" required).
        var hostCandidates = host == "+" && OperatingSystem.IsWindows()
            ? new[] { "+", "localhost" }
            : new[] { host };

        foreach (var candidate in hostCandidates)
        {
            var endpoint = TryStartOn(candidate, port, metrics, serviceInstanceId, logger,
                suppressError: candidate != hostCandidates[^1]);
            if (endpoint != null)
            {
                return endpoint;
            }
        }
        return null;
    }

    private static MetricsEndpoint? TryStartOn(
        string host,
        int port,
        GameMetrics metrics,
        string serviceInstanceId,
        ILogger logger,
        bool suppressError)
    {
        string prefix = $"http://{host}:{port}/";
        bool wildcard = host == "+";

        MeterProvider? provider = null;
        HttpListener? health = null;
        try
        {
            provider = Sdk.CreateMeterProviderBuilder()
                .ConfigureResource(r => r.AddService(
                    serviceName: "gameserver",
                    serviceInstanceId: serviceInstanceId))
                .AddMeter(metrics.MeterName)
                .AddView(
                    GameMetrics.TickDurationInstrument,
                    new ExplicitBucketHistogramConfiguration { Boundaries = TickDurationBuckets })
                // The group histogram needs its own view for the same reason the tick one
                // does: the SDK's default boundaries start at 0 and step in units sized for
                // milliseconds, while these values are recorded in seconds, so without this
                // every observation lands in the lowest bucket and histogram_quantile over
                // it returns nothing useful. Same boundaries as the tick, because a group's
                // duration is bounded by the tick's and the two are read together.
                .AddView(
                    GameMetrics.GroupDurationInstrument,
                    new ExplicitBucketHistogramConfiguration { Boundaries = TickDurationBuckets })
                .AddPrometheusHttpListener(options =>
                {
                    // OpenTelemetry builds its listener prefix as
                    // `new UriBuilder("http", Host, Port).Uri`, and UriBuilder rejects
                    // the HttpListener wildcards "+" and "*" with
                    // `UriFormatException: Invalid URI: The hostname could not be parsed`
                    // — thrown in the PrometheusHttpListener constructor, before any
                    // option we set could take effect. So for a wildcard bind we hand
                    // OTel a UriBuilder-safe placeholder and rewrite the prefix on the
                    // listener itself in ConfigureHttpListener, which runs before Start.
                    // HttpListener accepts "+" natively; only UriBuilder does not.
                    options.Host = wildcard ? "localhost" : host;
                    options.Port = port;
                    options.ScrapeEndpointPath = "/metrics";
                    // Keep the exposition clean: no otel_scope_* labels, and the
                    // standard name translation (dots -> underscores, unit and
                    // _total suffixes) that docs/METRICS.md documents.
                    options.ScopeInfoEnabled = false;
                    options.TranslationStrategy =
                        PrometheusTranslationStrategy.UnderscoreEscapingWithSuffixes;

                    if (wildcard)
                    {
                        options.ConfigureHttpListener = (_, listener) =>
                        {
                            listener.Prefixes.Clear();
                            listener.Prefixes.Add(prefix);
                        };
                    }
                })
                .Build();

            health = new HttpListener();
            health.Prefixes.Add(prefix + "healthz/");
            health.Start();

            HttpListener? status = null;
            try
            {
                status = new HttpListener();
                status.Prefixes.Add(prefix + "status/");
                status.Start();
            }
            catch (Exception ex2)
            {
                logger.LogWarning("Could not bind /status endpoint ({Reason}); /status disabled", ex2.Message);
                try { status?.Close(); } catch { /* ignore */ }
                status = null;
            }

            HttpListener? content = null;
            try
            {
                content = new HttpListener();
                content.Prefixes.Add(prefix + "content/");
                content.Start();
            }
            catch (Exception ex3)
            {
                logger.LogWarning("Could not bind /content endpoint ({Reason}); /content disabled", ex3.Message);
                try { content?.Close(); } catch { /* ignore */ }
                content = null;
            }

            var endpoint = new MetricsEndpoint(provider!, health, status, content, prefix, logger);
            endpoint._healthTask = Task.Run(() => endpoint.HealthLoopAsync(endpoint._cts.Token));
            if (status != null)
            {
                endpoint._statusTask = Task.Run(() => endpoint.StatusLoopAsync(endpoint._cts.Token));
            }
            if (content != null)
            {
                endpoint._contentTask = Task.Run(() => endpoint.ContentLoopAsync(endpoint._cts.Token));
            }

            logger.LogInformation(
                "Metrics endpoint listening on {Prefix} (/metrics, /healthz, /status, /content)", prefix);
            return endpoint;
        }
        catch (Exception ex)
        {
            if (suppressError)
            {
                logger.LogWarning("Could not bind metrics endpoint on {Prefix} ({Reason}); trying fallback", prefix, ex.Message);
            }
            else
            {
                logger.LogError(ex, "Failed to start metrics endpoint on {Prefix}", prefix);
            }
            try { health?.Close(); } catch { /* ignore */ }
            provider?.Dispose();
            return null;
        }
    }

    private async Task StatusLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _statusListener!.GetContextAsync();
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (OperationCanceledException) { break; }

            try
            {
                var provider = _statusProvider;
                var status = provider?.Invoke() ?? new ServerStatus();
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(status, ServerStatusContext.Default.ServerStatus);

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = body.Length;
                // Allow cross-origin polling from Unity WebGL builds or browser dev tools.
                ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                await ctx.Response.OutputStream.WriteAsync(body, ct);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Status response failed");
            }
        }
    }

    /// <summary>
    /// Serves the content document at <c>/content</c>, honouring a client-supplied hash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A client that already holds the current set sends <c>?hash=</c> and gets
    /// <c>304 Not Modified</c> with no body. That is what keeps the content fetch off the
    /// join path after the first time: content changes only when a server restarts, so the
    /// steady-state answer is always 304 and costs one round trip of headers.
    /// </para>
    /// <para>
    /// The hash is echoed in <c>ETag</c> and in <c>X-Content-Hash</c>. The second is not
    /// redundant — Unity's <c>UnityWebRequest</c> and several proxies rewrite or strip
    /// <c>ETag</c>, and a client that cannot read back the hash it just received has no way
    /// to ask for 304 next time, silently turning every join into a full download.
    /// </para>
    /// </remarks>
    private async Task ContentLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _contentListener!.GetContextAsync();
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (OperationCanceledException) { break; }

            try
            {
                var provider = _contentProvider;
                if (provider == null)
                {
                    // Reached only if a request lands between the listener starting and
                    // boot registering the provider. 503 rather than an empty 200: an
                    // empty content set is a legitimate answer, so it must never be the
                    // one a client gets from a server that simply is not ready yet.
                    ctx.Response.StatusCode = 503;
                    ctx.Response.ContentLength64 = 0;
                    ctx.Response.Close();
                    continue;
                }

                var (bytes, hash) = provider();

                ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                ctx.Response.AddHeader("X-Content-Hash", hash);
                ctx.Response.AddHeader("ETag", "\"" + hash + "\"");

                string? requested = ctx.Request.QueryString["hash"];
                if (!string.IsNullOrEmpty(requested) && string.Equals(requested, hash, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 304;
                    ctx.Response.ContentLength64 = 0;
                    ctx.Response.Close();
                    continue;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Content response failed");
            }
        }
    }

    private async Task HealthLoopAsync(CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes("ok");

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _healthListener.GetContextAsync();
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (OperationCanceledException) { break; }

            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body, ct);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health response failed");
            }
        }
    }

    /// <summary>
    /// Split a Go-style listen address into host and port.
    /// A wildcard or missing host becomes <c>+</c> so the listener binds all interfaces.
    /// </summary>
    internal static (string Host, int Port) ParseAddr(string addr)
    {
        string host;
        string port;

        int colon = addr.LastIndexOf(':');
        if (colon < 0)
        {
            host = "";
            port = addr;
        }
        else
        {
            host = addr[..colon];
            port = addr[(colon + 1)..];
        }

        if (string.IsNullOrEmpty(host) || host is "*" or "0.0.0.0" or "+")
        {
            host = "+";
        }

        return (host, int.Parse(port));
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Idempotence guard. This is single-owner today (one `await using` in
        // Program.cs), so it is hardening rather than a fix — but an unguarded
        // Cancel-then-Dispose on a CancellationTokenSource is exactly the shape
        // that threw out of ShutdownAsync twice, and the guard costs nothing.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        try { _healthListener.Close(); } catch { /* ignore */ }
        try { _statusListener?.Close(); } catch { /* ignore */ }
        try { _contentListener?.Close(); } catch { /* ignore */ }

        if (_healthTask != null)
        {
            try { await _healthTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }
        if (_statusTask != null)
        {
            try { await _statusTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }
        if (_contentTask != null)
        {
            try { await _contentTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
        }

        _meterProvider.Dispose();
        _cts.Dispose();
    }
}
