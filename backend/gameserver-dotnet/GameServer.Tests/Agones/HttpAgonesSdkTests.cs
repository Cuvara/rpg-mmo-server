using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Tests.Infrastructure;

namespace GameServer.Tests.Agones;

/// <summary>
/// <see cref="HttpAgonesSdk"/> against a real local <see cref="HttpListener"/> standing in
/// for the Agones sidecar.
///
/// <para>A fake sidecar is the strongest test available in-process, and it is worth being
/// explicit about what it does NOT prove: no C# server has ever reported Ready to a real
/// Agones sidecar in this project. These tests pin the HTTP shape (four paths, POST, an
/// empty JSON body) and — more importantly — the failure behaviour, because the whole point
/// of the class is that a missing or broken sidecar must not be able to take the game server
/// down with it.</para>
/// </summary>
public class HttpAgonesSdkTests
{
    /// <summary>Short on purpose: the absent-sidecar test waits out a real timeout.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>Each of the four calls must POST an empty JSON object to its own path.</summary>
    [Fact]
    public async Task EachCall_PostsEmptyJsonToItsOwnPath()
    {
        using var sidecar = new FakeSidecar();
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        await sdk.ReadyAsync();
        await sdk.HealthAsync();
        await sdk.AllocateAsync();
        await sdk.ShutdownAsync();

        var requests = sidecar.Requests.ToList();
        Assert.Equal(
            new[] { "/ready", "/health", "/allocate", "/shutdown" },
            requests.Select(r => r.Path).ToArray());
        Assert.All(requests, r =>
        {
            Assert.Equal("POST", r.Method);
            Assert.Equal("{}", r.Body);
            Assert.Contains("application/json", r.ContentType ?? "");
        });
    }

    /// <summary>
    /// A sidecar answering 500 must not throw out of any call. Every call site is either
    /// start-up or a background loop, and an exception in either turns a sidecar fault into
    /// a dead game server — the opposite of what Agones is for.
    /// </summary>
    [Fact]
    public async Task SidecarReturningError_DoesNotThrow()
    {
        using var sidecar = new FakeSidecar(statusCode: 500);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        Assert.Null(await Record.ExceptionAsync(() => sdk.ReadyAsync()));
        Assert.Null(await Record.ExceptionAsync(() => sdk.HealthAsync()));
        Assert.Null(await Record.ExceptionAsync(() => sdk.AllocateAsync()));
        Assert.Null(await Record.ExceptionAsync(() => sdk.ShutdownAsync()));

        // The failure is counted rather than silently dropped: the pings are
        // fire-and-forget, so this counter is the only in-process evidence that the
        // sidecar has stopped answering — and Agones will restart the pod if it stays
        // that way.
        Assert.Equal(1, sdk.ConsecutiveHealthFailures);
    }

    /// <summary>No sidecar at all — nothing listening on the port — must also not throw.</summary>
    [Fact]
    public async Task AbsentSidecar_DoesNotThrow()
    {
        // Lease a port and release it immediately: the number is almost certainly still
        // free, which is exactly the "no sidecar" case we want.
        int deadPort;
        using (var lease = new TestPorts.Lease()) { deadPort = lease.Port; }

        using var sdk = new HttpAgonesSdk(
            NullLogger.Instance, $"http://localhost:{deadPort}/", TestTimeout);

        Assert.Null(await Record.ExceptionAsync(() => sdk.ReadyAsync()));
        Assert.Null(await Record.ExceptionAsync(() => sdk.HealthAsync()));
        Assert.Null(await Record.ExceptionAsync(() => sdk.AllocateAsync()));
        Assert.Null(await Record.ExceptionAsync(() => sdk.ShutdownAsync()));
        Assert.Equal(1, sdk.ConsecutiveHealthFailures);
    }

    /// <summary>A recovered sidecar clears the failure count, so the counter tracks a run of failures rather than a lifetime total.</summary>
    [Fact]
    public async Task HealthAsync_AfterRecovery_ResetsTheFailureCount()
    {
        using var sidecar = new FakeSidecar();
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        sidecar.StatusCode = 500;
        await sdk.HealthAsync();
        await sdk.HealthAsync();
        Assert.Equal(2, sdk.ConsecutiveHealthFailures);

        sidecar.StatusCode = 200;
        await sdk.HealthAsync();
        Assert.Equal(0, sdk.ConsecutiveHealthFailures);
    }

    /// <summary>The default target is the sidecar's documented port.</summary>
    [Fact]
    public void DefaultPort_IsTheAgonesSidecarPort()
    {
        Assert.Equal(9358, HttpAgonesSdk.DefaultPort);
    }

    /// <summary>The no-op implementation must report itself as disabled — the health loop keys off it.</summary>
    [Fact]
    public void NoopSdk_IsNotEnabled_AndHttpSdkIs()
    {
        Assert.False(new NoopAgonesSdk().IsEnabled);
        using var http = new HttpAgonesSdk(NullLogger.Instance, "http://localhost:1/", TestTimeout);
        Assert.True(http.IsEnabled);
    }

    /// <summary>
    /// A fake Agones sidecar: one <see cref="HttpListener"/> that accepts anything, records
    /// it, and answers with a configurable status.
    /// </summary>
    private sealed class FakeSidecar : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public readonly ConcurrentQueue<RecordedRequest> Requests = new();

        /// <summary>Status every response carries. Mutable so a test can break and heal the sidecar.</summary>
        public int StatusCode;

        public string BaseAddress { get; }

        public FakeSidecar(int statusCode = 200)
        {
            StatusCode = statusCode;

            // HttpListener prefixes need a literal port and it cannot report an ephemeral
            // bind, so a lease is the right tool here (see TestPorts). The lease-to-bind
            // handoff is still not atomic, so bind through BindWithRetry: on "Address
            // already in use" it comes back with a different port instead of failing a
            // test that has nothing to do with ports.
            string baseAddress = "";
            _listener = TestPorts.BindWithRetry(port =>
            {
                string prefix = $"http://localhost:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start(); // throws HttpListenerException if the port was taken
                baseAddress = prefix;
                return listener;
            })!;

            BaseAddress = baseAddress;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; } // listener closed
                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    Requests.Enqueue(new RecordedRequest(
                        ctx.Request.Url?.AbsolutePath ?? "",
                        ctx.Request.HttpMethod,
                        ctx.Request.ContentType,
                        body));

                    ctx.Response.StatusCode = StatusCode;
                    ctx.Response.ContentType = "application/json";
                    var payload = System.Text.Encoding.UTF8.GetBytes("{}");
                    ctx.Response.ContentLength64 = payload.Length;
                    await ctx.Response.OutputStream.WriteAsync(payload);
                    ctx.Response.Close();
                }
                catch { /* a torn-down test client is not a failure */ }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* teardown */ }
            try { _listener.Close(); } catch { /* teardown */ }
            _cts.Dispose();
        }

        internal readonly record struct RecordedRequest(
            string Path, string Method, string? ContentType, string Body);
    }
}

/// <summary>
/// Port resolution, split out because it mutates a process-global environment variable and
/// must not run beside the tests that construct clients from it.
/// </summary>
[Collection(AgonesEnvCollection.Name)]
public class HttpAgonesSdkPortTests
{
    /// <summary>An unset variable resolves to the documented default.</summary>
    [Fact]
    public void ResolvePort_Unset_UsesDefault()
    {
        using var _ = new EnvVar(HttpAgonesSdk.PortEnvVar, null);
        Assert.Equal(HttpAgonesSdk.DefaultPort, HttpAgonesSdk.ResolvePort());
    }

    /// <summary>A valid override wins, which is what lets a test or a sidecar on a nonstandard port work.</summary>
    [Fact]
    public void ResolvePort_ValidOverride_IsUsed()
    {
        using var _ = new EnvVar(HttpAgonesSdk.PortEnvVar, "19358");
        Assert.Equal(19358, HttpAgonesSdk.ResolvePort());
    }

    /// <summary>
    /// A typo falls back rather than throwing. A server that refuses to boot on a bad env
    /// var is a restart loop; a server on the wrong port is one warning line.
    /// </summary>
    [Theory]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void ResolvePort_InvalidOverride_FallsBackToDefault(string raw)
    {
        using var _ = new EnvVar(HttpAgonesSdk.PortEnvVar, raw);
        Assert.Equal(HttpAgonesSdk.DefaultPort, HttpAgonesSdk.ResolvePort());
    }

    /// <summary>
    /// The env var actually reaches the client's base address — resolving the number
    /// correctly would be worth nothing if the constructor ignored it.
    /// </summary>
    [Fact]
    public void Constructor_WithoutExplicitAddress_TargetsTheResolvedPort()
    {
        using var _ = new EnvVar(HttpAgonesSdk.PortEnvVar, "19359");
        using var sdk = new HttpAgonesSdk(NullLogger.Instance);
        Assert.Equal("http://localhost:19359/", sdk.BaseAddress);
    }

    /// <summary>Sets an environment variable for the duration of a test and restores it after.</summary>
    private sealed class EnvVar : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVar(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}

/// <summary>Serialises the classes that mutate Agones environment variables.</summary>
[CollectionDefinition(Name)]
public class AgonesEnvCollection
{
    public const string Name = "AgonesEnv";
}
