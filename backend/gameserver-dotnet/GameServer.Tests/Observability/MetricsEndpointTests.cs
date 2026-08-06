using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Observability;

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
    /// <summary>Grab a free TCP port by binding port 0 and releasing it.</summary>
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

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
        int port = FreePort();
        string addr = string.Format(addrTemplate, port);

        using var metrics = new GameMetrics("map_01", $"rpg.gameserver.test.endpoint.{Guid.NewGuid():N}");
        await using var endpoint = MetricsEndpoint.TryStart(addr, metrics, "gs-test", NullLogger.Instance);

        Assert.NotNull(endpoint); // null = it failed to bind, which is the regression
        metrics.PlayerJoined();

        var (healthStatus, healthBody) = await GetAsync($"http://{requestHost}:{port}/healthz");
        Assert.Equal(200, healthStatus);
        Assert.Equal("ok", healthBody);

        var (metricsStatus, metricsBody) = await GetAsync($"http://{requestHost}:{port}/metrics");
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
