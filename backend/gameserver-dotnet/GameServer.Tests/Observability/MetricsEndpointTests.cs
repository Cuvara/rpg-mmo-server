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

    /// <summary>
    /// <c>/debug/entities</c> exists only after <see cref="MetricsEndpoint.EnableDebugEntities"/>:
    /// a server that never opted in answers 404 there, and one that did serves the provider's
    /// bytes verbatim.
    /// </summary>
    [Fact]
    public async Task DebugEntities_IsAbsentUntilEnabled_ThenServesTheProvidersDocument()
    {
        using var metrics = new GameMetrics("map_01", $"rpg.gameserver.test.debug.{Guid.NewGuid():N}");
        int port = 0;
        await using var endpoint = TestPorts.BindWithRetry(leased =>
        {
            port = leased;
            return MetricsEndpoint.TryStart($"localhost:{leased}", metrics, "gs-test", NullLogger.Instance);
        });
        Assert.NotNull(endpoint);
        string host = new Uri(endpoint!.UriPrefix).Host;

        var (before, _) = await GetAsync($"http://{host}:{port}/debug/entities");
        Assert.Equal(404, before);

        Assert.True(endpoint.EnableDebugEntities(() => System.Text.Encoding.UTF8.GetBytes("{\"count\":0}")));
        var (after, body) = await GetAsync($"http://{host}:{port}/debug/entities");
        Assert.Equal(200, after);
        Assert.Equal("{\"count\":0}", body);
    }

    private static async Task<(int status, string body)> SendAsync(HttpMethod method, string url, string? json = null,
        bool chunked = false)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(method, url);
        if (json != null && chunked)
        {
            // A stream that cannot report its length makes HttpClient send the body chunked, with
            // no Content-Length: the server must find the size by reading, not from a header.
            req.Content = new StreamContent(new UnknownLengthStream(System.Text.Encoding.UTF8.GetBytes(json)));
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        else if (json != null)
        {
            req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }
        var resp = await http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static MetricsEndpoint StartEndpoint(GameMetrics metrics, out string baseUrl)
    {
        int port = 0;
        var endpoint = TestPorts.BindWithRetry(leased =>
        {
            port = leased;
            return MetricsEndpoint.TryStart($"localhost:{leased}", metrics, "gs-test", NullLogger.Instance);
        });
        Assert.NotNull(endpoint);
        baseUrl = $"http://{new Uri(endpoint!.UriPrefix).Host}:{port}";
        return endpoint;
    }

    /// <summary>
    /// The debug switch off binds neither debug endpoint, even with a kill submitter in hand;
    /// on binds both. The control arm is the same call with the switch flipped.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DebugSurface_BindsNothingWhenOff_AndBothEndpointsWhenOn(bool enabled)
    {
        using var metrics = new GameMetrics("map_01", $"rpg.gameserver.test.debugsurface.{Guid.NewGuid():N}");
        await using var endpoint = StartEndpoint(metrics, out string baseUrl);
        int submitted = 0;

        DebugSurface.Enable(endpoint, enabled,
            () => System.Text.Encoding.UTF8.GetBytes("{\"count\":0}"),
            (id, _) =>
            {
                Interlocked.Increment(ref submitted);
                return Task.FromResult(new DebugKillResult(DebugKillOutcome.Killed, 7, 42));
            });

        var (entities, _) = await SendAsync(HttpMethod.Get, baseUrl + "/debug/entities");
        var (kill, _) = await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", "{\"id\":\"p1\"}");

        if (enabled)
        {
            Assert.Equal(200, entities);
            Assert.Equal(200, kill);
            Assert.Equal(1, submitted);
        }
        else
        {
            Assert.Equal(404, entities);
            Assert.Equal(404, kill);
            Assert.Equal(0, submitted);
        }
    }

    /// <summary>
    /// <c>POST /debug/kill</c>: the id reaches the submitter (body or query), the outcome maps to
    /// a status and a JSON body, GET is refused, and a request naming nobody never reaches the
    /// submitter.
    /// </summary>
    [Fact]
    public async Task DebugKill_ParsesTheId_AndMapsEachOutcome()
    {
        using var metrics = new GameMetrics("map_01", $"rpg.gameserver.test.debugkill.{Guid.NewGuid():N}");
        await using var endpoint = StartEndpoint(metrics, out string baseUrl);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();

        Assert.True(endpoint.EnableDebugKill((id, _) =>
        {
            seen.Enqueue(id);
            DebugKillOutcome outcome = id switch
            {
                "p1" => DebugKillOutcome.Killed,
                "dead" => DebugKillOutcome.AlreadyDead,
                "m1" => DebugKillOutcome.NotAPlayer,
                _ => DebugKillOutcome.NotFound,
            };
            return Task.FromResult(new DebugKillResult(outcome, 99, outcome == DebugKillOutcome.Killed ? 80 : 0));
        }));

        var (ok, okBody) = await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", "{\"id\":\"p1\"}");
        Assert.Equal(200, ok);
        using (var doc = System.Text.Json.JsonDocument.Parse(okBody))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("p1", doc.RootElement.GetProperty("id").GetString());
            Assert.Equal("killed", doc.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(99UL, doc.RootElement.GetProperty("tick").GetUInt64());
            Assert.Equal(80, doc.RootElement.GetProperty("hp_before").GetInt32());
        }

        Assert.Equal(409, (await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", "{\"user_id\":\"dead\"}")).status);
        Assert.Equal(400, (await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", "{\"entity_id\":\"m1\"}")).status);
        Assert.Equal(404, (await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill?id=ghost")).status);

        int before = seen.Count;
        Assert.Equal(405, (await SendAsync(HttpMethod.Get, baseUrl + "/debug/kill?id=p1")).status);
        Assert.Equal(400, (await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", "{}")).status);
        Assert.Equal(400, (await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", "not json")).status);
        Assert.Equal(before, seen.Count);

        Assert.Equal(new[] { "p1", "dead", "m1", "ghost" }, seen.ToArray());
    }

    /// <summary>
    /// Review R1-2: a body over the limit, or one that is not exactly one complete JSON object,
    /// is refused and queues nothing, even when it starts with a valid id. Before the fix each of
    /// these reached the submitter with id "p1": the handler parsed the first 4096 bytes of an
    /// oversized body, and the parser returned at the first id it saw.
    /// </summary>
    [Fact]
    public async Task DebugKill_RefusesOversizedAndTrailingGarbageBodies_WithoutQueueing()
    {
        using var metrics = new GameMetrics("map_01", $"rpg.gameserver.test.debugkill.{Guid.NewGuid():N}");
        await using var endpoint = StartEndpoint(metrics, out string baseUrl);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Assert.True(endpoint.EnableDebugKill((id, _) =>
        {
            seen.Enqueue(id);
            return Task.FromResult(new DebugKillResult(DebugKillOutcome.Killed, 1, 100));
        }));

        string valid = "{\"id\":\"p1\"}";
        // Exactly at the limit (padded with whitespace) is still accepted.
        string atLimit = valid + new string(' ', DebugKill.MaxBodyBytes - valid.Length);
        Assert.Equal(200, (await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", atLimit)).status);
        Assert.Single(seen);

        var (tooLarge, tooLargeBody) = await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", atLimit + " ");
        Assert.Equal(413, tooLarge);
        Assert.Contains(DebugKill.BodyTooLarge, tooLargeBody);
        Assert.Equal(413, (await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", valid + new string('x', 10_000))).status);
        // Chunked, so no Content-Length to refuse on: the extra byte read past the limit decides.
        Assert.Equal(413, (await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", atLimit + " ", chunked: true)).status);
        Assert.Equal(200, (await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill", atLimit, chunked: true)).status);
        Assert.Equal(2, seen.Count);

        foreach (string bad in new[]
                 {
                     valid + "garbage",
                     valid + "{\"id\":\"p2\"}",
                     valid + " ,",
                     "{\"id\":\"p1\"",
                     "{\"id\":\"p1\",\"x\":",
                     "[\"p1\"]",
                 })
        {
            var (status, body) = await SendAsync(HttpMethod.Post, baseUrl + "/debug/kill?id=p1", bad);
            Assert.True(status == 400, $"{bad} -> {status}");
            Assert.Contains(DebugKill.InvalidBody, body);
        }

        Assert.Equal(2, seen.Count);
    }

    /// <summary>A read-only stream that cannot seek, so it has no length to declare.</summary>
    private sealed class UnknownLengthStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("{\"id\":\"p1\"}", null, "p1", null)]
    [InlineData("  {\"user_id\":\"u\",\"other\":[1,{\"id\":\"x\"}]}\n", null, "u", null)]
    [InlineData("{\"id\":\"p1\",\"id\":\"p2\"}", null, "p1", null)]
    [InlineData("{}", "q", "q", null)]
    [InlineData("", "q", "q", null)]
    [InlineData("{}", null, null, DebugKill.MissingId)]
    [InlineData("{\"id\":\"p1\"}x", "q", null, DebugKill.InvalidBody)]
    [InlineData("{\"id\":\"p1\"}{}", null, null, DebugKill.InvalidBody)]
    [InlineData("{\"id\":\"p1\"", null, null, DebugKill.InvalidBody)]
    [InlineData("not json", "q", null, DebugKill.InvalidBody)]
    [InlineData("\"p1\"", null, null, DebugKill.InvalidBody)]
    public void DebugKill_ParseId_RequiresOneCompleteObject(string body, string? query, string? expectedId, string? expectedError)
    {
        string? id = DebugKill.ParseId(System.Text.Encoding.UTF8.GetBytes(body), query, out string? error);
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedError, error);
    }
}
