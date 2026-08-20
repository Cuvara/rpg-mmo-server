using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Observability;
using GameServer.Tests.Infrastructure;

namespace GameServer.Tests.Observability;

/// <summary>
/// Tests that the observability endpoint actually binds and serves.
/// <para>
/// The wildcard case is the one that matters: `METRICS_ADDR=:9101` becomes the
/// HttpListener wildcard prefix `http://+:9101/`, and OpenTelemetry's
/// PrometheusHttpListener used to reject it (`UriBuilder` cannot parse "+"), so the
/// endpoint silently never started on Linux — the deployed target. Nothing caught it
/// because no test ever started the endpoint.
/// </para>
/// </summary>
public class MetricsEndpointTests
{
    private static async Task<(int status, string body)> GetAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await http.GetAsync(url);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <param name="addrTemplate">METRICS_ADDR value, {0} = port.</param>
    /// <param name="requestHost">
    /// Authority the scrape is sent to. A wildcard prefix answers any Host header, so
    /// a bare IP works; a prefix registered for a named host only answers that name —
    /// which is exactly why the docker-compose workaround has to scrape the endpoint
    /// under its service name.
    /// </param>
    [Theory]
    [InlineData(":{0}", "127.0.0.1")]        // wildcard: the production / compose form
    [InlineData("0.0.0.0:{0}", "127.0.0.1")] // also normalized to the wildcard
    [InlineData("*:{0}", "127.0.0.1")]
    [InlineData("localhost:{0}", "localhost")]
    public async Task TryStart_BindsAndServesMetricsAndHealth(string addrTemplate, string requestHost)
    {
        // MetricsEndpoint binds an HttpListener, not a socket it can hand back: HttpListener
        // prefixes need a literal port, there is no ephemeral-bind mode, and it reports
        // nothing about what it took — so the ":0"-and-ask pattern used everywhere else in
        // this suite does not apply here. A lease is the next best thing: the port stays
        // held while the four [Theory] cases pick theirs in parallel, so they cannot be
        // handed the same number (the old FreePort() released immediately, and the kernel
        // will happily re-issue a port it just took back — three of the four cases failing
        // together in one run is that signature, not four independent collisions). The
        // handoff itself is still not atomic — so bind through TestPorts.BindWithRetry,
        // which comes back with a NEW lease when the previous port was taken in that gap.
        // TryStart swallows the bind error and returns null by design (a metrics endpoint
        // must not kill the game server), so here a lost race surfaces as null rather than
        // as an exception, and the retry has to treat null as retryable too. This weakens
        // nothing: a genuine bind regression fails all five attempts, BindWithRetry returns
        // null, and the Assert.NotNull below still fires.
        using var metrics = new GameMetrics("map_01", $"rpg.gameserver.test.endpoint.{Guid.NewGuid():N}");

        string boundAddr = "";
        int boundPort = 0;
        await using var endpoint = TestPorts.BindWithRetry(leased =>
        {
            boundAddr = string.Format(addrTemplate, leased);
            boundPort = leased;
            return MetricsEndpoint.TryStart(boundAddr, metrics, "gs-test", NullLogger.Instance);
        });

        Assert.NotNull(endpoint); // null = it failed to bind, which is the regression
        string addr = boundAddr;
        int port = boundPort;
        metrics.PlayerJoined();

        bool wildcardBound = endpoint!.UriPrefix.StartsWith("http://+:", StringComparison.Ordinal);

        // On Linux — CI and the production target — a wildcard address must really bind
        // the "+" prefix. Pin that down so the fallback below can never quietly become
        // the normal path there.
        if (!OperatingSystem.IsWindows() && requestHost == "127.0.0.1")
        {
            Assert.True(wildcardBound,
                $"expected a wildcard bind for '{addr}', got prefix '{endpoint.UriPrefix}'");
        }

        // Windows needs an admin URL ACL for "+", so TryStart deliberately falls back to
        // localhost for unprivileged dev runs. HttpListener answers 400 to any Host header
        // no registered prefix matches, so scrape the authority that was actually bound
        // rather than the one we asked for. The status/body assertions stay identical.
        string scrapeHost = wildcardBound ? requestHost : new Uri(endpoint.UriPrefix).Host;

        var (healthStatus, healthBody) = await GetAsync($"http://{scrapeHost}:{port}/healthz");
        Assert.Equal(200, healthStatus);
        Assert.Equal("ok", healthBody);

        var (metricsStatus, metricsBody) = await GetAsync($"http://{scrapeHost}:{port}/metrics");
        Assert.Equal(200, metricsStatus);
        // Prometheus exposition of a known instrument, proving the exporter is live
        // and the documented name translation (dots -> underscores) is applied.
        Assert.Contains("gameserver_players_online", metricsBody);
    }

    [Fact]
    public async Task TryStart_EmptyAddr_DisablesEndpoint()
    {
        using var metrics = new GameMetrics("map_01", $"rpg.gameserver.test.endpoint.{Guid.NewGuid():N}");
        await using var endpoint = MetricsEndpoint.TryStart("", metrics, "gs-test", NullLogger.Instance);
        Assert.Null(endpoint);
    }

    /// <summary>
    /// "off" is the Go gateway's documented off-switch for METRICS_ADDR. It used to
    /// reach int.Parse here and kill the server at startup with a bare
    /// FormatException — for a value that reads like "turn it off".
    /// </summary>
    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("none")]
    [InlineData("disabled")]
    [InlineData("  off  ")]
    public async Task TryStart_OffSwitches_DisableEndpointWithoutThrowing(string addr)
    {
        using var metrics = new GameMetrics("map_01", $"rpg.gameserver.test.endpoint.{Guid.NewGuid():N}");
        await using var endpoint = MetricsEndpoint.TryStart(addr, metrics, "gs-test", NullLogger.Instance);
        Assert.Null(endpoint);
    }

    /// <summary>
    /// A mistyped address costs metrics, not the game server — the same call already
    /// treats a failed bind as non-fatal.
    /// </summary>
    [Theory]
    [InlineData("nonsense")]
    [InlineData("localhost:not-a-port")]
    [InlineData(":99999999999")]
    public async Task TryStart_UnparseableAddr_DisablesEndpointInsteadOfCrashing(string addr)
    {
        using var metrics = new GameMetrics("map_01", $"rpg.gameserver.test.endpoint.{Guid.NewGuid():N}");
        await using var endpoint = MetricsEndpoint.TryStart(addr, metrics, "gs-test", NullLogger.Instance);
        Assert.Null(endpoint);
    }

    [Theory]
    [InlineData(":9101", "+", 9101)]
    [InlineData("0.0.0.0:9101", "+", 9101)]
    [InlineData("*:9101", "+", 9101)]
    [InlineData("+:9101", "+", 9101)]
    [InlineData("localhost:9101", "localhost", 9101)]
    [InlineData("gameserver-dotnet:9101", "gameserver-dotnet", 9101)]
    public void ParseAddr_NormalizesWildcardHosts(string addr, string wantHost, int wantPort)
    {
        var (host, port) = MetricsEndpoint.ParseAddr(addr);
        Assert.Equal(wantHost, host);
        Assert.Equal(wantPort, port);
    }
}
