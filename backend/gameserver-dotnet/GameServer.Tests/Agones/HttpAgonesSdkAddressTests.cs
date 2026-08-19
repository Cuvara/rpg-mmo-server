using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Tests.Infrastructure;

namespace GameServer.Tests.Agones;

/// <summary>
/// <see cref="HttpAgonesSdk.GetAddressAsync"/> against a fake sidecar.
///
/// <para>The success body below is <b>not invented</b>. It is the response a real Agones
/// <b>1.59.0</b> sidecar returned for <c>GET /gameserver</c>, captured through
/// <c>kubectl port-forward</c> against <c>map-servers-dev-kl485-gsmrh</c> in
/// <c>rpg-realtime</c> — snake_case field names, node address in <c>status.address</c>,
/// host port under the <c>status.ports</c> entry named <c>game</c>. Pinning the captured
/// shape is the whole value of these tests: everything else about the read is a fallback to
/// null, and a fallback that fires for the wrong reason looks exactly like one that fires
/// for the right one.</para>
///
/// <para>None of these tests is dependency-gated — the sidecar is an in-process
/// <see cref="HttpListener"/> — so none of them skips. A skip here would mean the fake
/// failed to bind, which is a failure, not an absent dependency.</para>
/// </summary>
public class HttpAgonesSdkAddressTests
{
    /// <summary>Short on purpose: the absent-sidecar test waits out a real timeout.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>Verbatim capture from Agones 1.59.0, trimmed of nothing that is read.</summary>
    private const string LiveBody = """
        {"object_meta":{"name":"map-servers-dev-kl485-gsmrh","namespace":"rpg-realtime",
         "uid":"453744af-a302-4c1b-b756-7ba9e111bb77","resource_version":"1746447",
         "generation":"7","creation_timestamp":"1786938633","deletion_timestamp":"0",
         "annotations":{"agones.dev/sdk-version":"1.59.0"},
         "labels":{"agones.dev/fleet":"map-servers-dev","rpg-mmo/role":"map"}},
         "spec":{"health":{"disabled":false,"period_seconds":5,"failure_threshold":3,
         "initial_delay_seconds":10}},
         "status":{"state":"Ready","address":"192.168.65.3",
         "addresses":[{"type":"InternalIP","address":"192.168.65.3"},
                      {"type":"Hostname","address":"docker-desktop"},
                      {"type":"PodIP","address":"10.1.0.94"}],
         "ports":[{"name":"game","port":7691}],
         "players":null,"counters":{},"lists":{}}}
        """;

    /// <summary>The live shape parses to the node address plus the <c>game</c> host port.</summary>
    [Fact]
    public async Task LiveSidecarShape_YieldsAddressAndGamePort()
    {
        using var sidecar = new FakeStatusSidecar(LiveBody);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        var addr = await sdk.GetAddressAsync();

        Assert.NotNull(addr);
        Assert.Equal("192.168.65.3", addr!.Address);
        Assert.Equal(7691, addr.Port);
        // This string is what ends up in Redis and, through the gateway, in
        // MsgEnterWorldResp.ServerAddr — the value the client actually dials.
        Assert.Equal("192.168.65.3:7691", addr.ToString());
    }

    /// <summary>The read is a GET on <c>/gameserver</c> and carries no body.</summary>
    [Fact]
    public async Task Read_IsAGetOnGameserver()
    {
        using var sidecar = new FakeStatusSidecar(LiveBody);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        await sdk.GetAddressAsync();

        var req = Assert.Single(sidecar.Requests);
        Assert.Equal("GET", req.Method);
        Assert.Equal("/gameserver", req.Path);
    }

    /// <summary>
    /// The port is chosen by NAME. A fleet that grows a second container port ahead of the
    /// game port in the array must not shift what gets advertised — <c>ports[0]</c> would.
    /// </summary>
    [Fact]
    public async Task GamePortIsSelectedByName_NotByIndex()
    {
        const string body = """
            {"status":{"address":"10.0.0.7",
             "ports":[{"name":"metrics","port":31111},
                      {"name":"game","port":7691},
                      {"name":"debug","port":32222}]}}
            """;
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        var addr = await sdk.GetAddressAsync();

        Assert.NotNull(addr);
        Assert.Equal(7691, addr!.Port);
    }

    /// <summary>No port named <c>game</c> is a null, not a guess at another port.</summary>
    [Fact]
    public async Task MissingGamePort_ReturnsNull()
    {
        const string body = """
            {"status":{"address":"10.0.0.7","ports":[{"name":"metrics","port":31111}]}}
            """;
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        Assert.Null(await sdk.GetAddressAsync());
    }

    /// <summary>An empty port array, and no ports key at all, are both null.</summary>
    [Theory]
    [InlineData("""{"status":{"address":"10.0.0.7","ports":[]}}""")]
    [InlineData("""{"status":{"address":"10.0.0.7"}}""")]
    public async Task NoPorts_ReturnsNull(string body)
    {
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        Assert.Null(await sdk.GetAddressAsync());
    }

    /// <summary>
    /// A status with a port but no address is the unscheduled pod, and it must not compose
    /// a hostless address — that is exactly the value this whole feature exists to stop
    /// reaching Redis.
    /// </summary>
    [Theory]
    [InlineData("""{"status":{"ports":[{"name":"game","port":7691}]}}""")]
    [InlineData("""{"status":{"address":"","ports":[{"name":"game","port":7691}]}}""")]
    [InlineData("""{"status":{"address":"   ","ports":[{"name":"game","port":7691}]}}""")]
    [InlineData("""{"status":{}}""")]
    [InlineData("{}")]
    public async Task MissingAddress_ReturnsNull(string body)
    {
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        Assert.Null(await sdk.GetAddressAsync());
    }

    /// <summary>A port outside the valid range is refused rather than advertised.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task UnusablePort_ReturnsNull(int port)
    {
        string body =
            "{\"status\":{\"address\":\"10.0.0.7\",\"ports\":[{\"name\":\"game\",\"port\":" + port + "}]}}";
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        Assert.Null(await sdk.GetAddressAsync());
    }

    /// <summary>A sidecar answering 500 is null and, above all, does not throw.</summary>
    [Fact]
    public async Task SidecarError_ReturnsNullWithoutThrowing()
    {
        using var sidecar = new FakeStatusSidecar(LiveBody, statusCode: 500);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        AgonesGameServerAddress? addr = null;
        Assert.Null(await Record.ExceptionAsync(async () => addr = await sdk.GetAddressAsync()));
        Assert.Null(addr);
    }

    /// <summary>An unparsable body is null, not a crash on the server's start-up path.</summary>
    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"status\": {")]
    [InlineData("[1,2,3]")]
    public async Task MalformedJson_ReturnsNullWithoutThrowing(string body)
    {
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        AgonesGameServerAddress? addr = null;
        Assert.Null(await Record.ExceptionAsync(async () => addr = await sdk.GetAddressAsync()));
        Assert.Null(addr);
    }

    /// <summary>No sidecar listening at all: null, no exception, one timeout.</summary>
    [Fact]
    public async Task AbsentSidecar_ReturnsNullWithoutThrowing()
    {
        int deadPort;
        using (var lease = new TestPorts.Lease()) { deadPort = lease.Port; }

        using var sdk = new HttpAgonesSdk(
            NullLogger.Instance, $"http://localhost:{deadPort}/", TestTimeout);

        AgonesGameServerAddress? addr = null;
        Assert.Null(await Record.ExceptionAsync(async () => addr = await sdk.GetAddressAsync()));
        Assert.Null(addr);
    }

    /// <summary>The no-op SDK has no address to report and says so.</summary>
    [Fact]
    public async Task NoopSdk_ReportsNoAddress()
    {
        Assert.Null(await new NoopAgonesSdk().GetAddressAsync());
    }

    // ── status.state, the allocation gate's input (issue #151) ──

    /// <summary>The live capture reports Ready, and the state read returns it verbatim.</summary>
    [Fact]
    public async Task LiveSidecarShape_YieldsTheState()
    {
        using var sidecar = new FakeStatusSidecar(LiveBody);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        Assert.Equal("Ready", await sdk.GetStateAsync());
    }

    /// <summary>An allocated GameServer reads as exactly the constant the gate compares to.</summary>
    [Fact]
    public async Task AllocatedState_MatchesTheConstantTheGateCompares()
    {
        const string body = """
            {"status":{"state":"Allocated","address":"10.0.0.7",
             "ports":[{"name":"game","port":7691}]}}
            """;
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        Assert.Equal(AgonesGameServerState.Allocated, await sdk.GetStateAsync());
    }

    /// <summary>
    /// No state, no status, a non-2xx and an unparsable body are all null — "unreadable",
    /// which the gate treats as "keep waiting" rather than as a state.
    /// </summary>
    [Theory]
    [InlineData("""{"status":{"address":"10.0.0.7"}}""", 200)]
    [InlineData("""{"status":{"state":"   ","address":"10.0.0.7"}}""", 200)]
    [InlineData("""{}""", 200)]
    [InlineData("""{"status":{"state":"Allocated"}}""", 500)]
    [InlineData("not json at all", 200)]
    public async Task UnreadableState_IsNull(string body, int status)
    {
        using var sidecar = new FakeStatusSidecar(body, status);
        using var sdk = new HttpAgonesSdk(NullLogger.Instance, sidecar.BaseAddress, TestTimeout);

        Assert.Null(await sdk.GetStateAsync());
    }

    /// <summary>No sidecar at all is a null and never an exception.</summary>
    [Fact]
    public async Task StateRead_WithNoSidecar_IsNullAndDoesNotThrow()
    {
        var lease = new TestPorts.Lease();
        var dead = $"http://localhost:{lease.Port}/";
        lease.Dispose();

        using var sdk = new HttpAgonesSdk(NullLogger.Instance, dead, TestTimeout);

        string? state = "unset";
        Assert.Null(await Record.ExceptionAsync(async () => state = await sdk.GetStateAsync()));
        Assert.Null(state);
    }

    /// <summary>The no-op SDK has no GameServer object, so it has no state.</summary>
    [Fact]
    public async Task NoopSdk_HasNoState()
    {
        Assert.Null(await new NoopAgonesSdk().GetStateAsync());
    }

    /// <summary>The port name must stay the one the fleet manifests and the gateway use.</summary>
    [Fact]
    public void GamePortName_MatchesTheFleetManifestsAndTheGateway()
    {
        // deploy/agones/fleet-*.yaml ports[].name, and gamePortName in
        // gateway/registry/agones_allocator.go.
        Assert.Equal("game", AgonesGameServerAddress.GamePortName);
    }

    /// <summary>
    /// A fake sidecar that answers <c>GET /gameserver</c> with a fixed body and status, and
    /// records what it was asked.
    /// </summary>
    private sealed class FakeStatusSidecar : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _body;
        private readonly int _statusCode;

        public readonly ConcurrentQueue<RecordedRequest> Requests = new();

        public string BaseAddress { get; }

        public FakeStatusSidecar(string body, int statusCode = 200)
        {
            _body = body;
            _statusCode = statusCode;

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
                    Requests.Enqueue(new RecordedRequest(
                        ctx.Request.Url?.AbsolutePath ?? "", ctx.Request.HttpMethod));

                    ctx.Response.StatusCode = _statusCode;
                    ctx.Response.ContentType = "application/json";
                    var payload = System.Text.Encoding.UTF8.GetBytes(_body);
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

        internal readonly record struct RecordedRequest(string Path, string Method);
    }
}
