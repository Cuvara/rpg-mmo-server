using System.Collections.Concurrent;
using System.Net;
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
    /// <summary>
    /// <b>No wall-clock budget at all for a sidecar that is in this process.</b>
    ///
    /// <para>Every test below except the two absent-sidecar ones talks to a
    /// <see cref="FakeStatusSidecar"/> running in the same process. There is no network, no
    /// scheduler outside this box and nothing to be slow about, so a per-request deadline
    /// there is not a claim the test is making — it is incidental machinery that can only
    /// ever turn a scheduling hiccup into a failure. It did:
    /// <c>LiveSidecarShape_YieldsAddressAndGamePort</c> returned null once in eight
    /// full-suite runs against the previous shared 700ms budget, because
    /// <see cref="HttpAgonesSdk.GetAddressAsync"/> maps a timeout onto the same null it
    /// returns for a body it could not read (#216).</para>
    ///
    /// <para><b>The fix is not a bigger number.</b> Widening a deadline that is not being
    /// asserted on only moves the flake somewhere further out and leaves the next reader
    /// believing 700ms — or 5s, or 30s — meant something. Removing it says what is true:
    /// these tests assert on a parsed address, never on how quickly it arrived. That is the
    /// same correction #200 made to <c>AchievedRateMeterTests</c>, which stopped asserting a
    /// rate the host scheduler decides.</para>
    ///
    /// <para><b>A hang is still bounded</b>, by <see cref="HangBackstop"/> below, which is
    /// the backstop's whole and only job.</para>
    /// </summary>
    private static readonly TimeSpan NoDeadline = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// The only budget left, and only the two tests pointing at a dead port use it. There a
    /// timeout is the <i>subject</i>: those tests assert that a sidecar which never answers
    /// produces a null rather than an exception, so the deadline is what they are exercising
    /// and keeping it short is what keeps them quick. On a platform where a closed local port
    /// refuses immediately it never fires at all.
    /// </summary>
    private static readonly TimeSpan AbsentSidecarTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// How long an in-process HTTP exchange may take before the test calls it a hang.
    ///
    /// <para><b>This is not a responsiveness assertion and must never be read as one.</b> It
    /// exists so a genuinely stuck exchange fails with a message instead of hanging the suite
    /// until the CI job is killed. Nothing about a value between one millisecond and thirty
    /// seconds is a defect these tests have an opinion on; only "never" is. Thirty seconds is
    /// roughly forty times the budget that flaked, so if it ever fires, something is stuck
    /// rather than slow — and <see cref="ReadAsync{T}"/> prints what the SDK logged so the
    /// next reader knows which.</para>
    /// </summary>
    private static readonly TimeSpan HangBackstop = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Await a read with <see cref="HangBackstop"/> as the only bound, failing with the
    /// subject's own log rather than with a bare timeout if it never finishes.
    /// </summary>
    /// <remarks>
    /// Raced with <see cref="Task.WhenAny(Task, Task)"/> rather than passed a shorter
    /// per-request timeout, because the two do different things: a per-request timeout makes
    /// the SDK <i>return null</i>, which is indistinguishable from the nulls these tests are
    /// about, while this leaves the null meaning what the SDK meant by it and reports a stall
    /// as a stall. <see cref="Task.Delay(TimeSpan)"/> runs on the runtime timer queue, which
    /// is monotonic — this host's <c>CLOCK_REALTIME</c> runs 10-17% fast (#153) and must not
    /// be anywhere near a deadline.
    /// </remarks>
    private static async Task<T> ReadAsync<T>(Task<T> read, CapturingLogger log, string what)
    {
        var finished = await Task.WhenAny(read, Task.Delay(HangBackstop));
        if (finished != read)
        {
            Assert.Fail(log.Explain(
                $"{what} had still not completed after {HangBackstop.TotalSeconds:F0}s against " +
                "a sidecar in this same process. That is a stall, not a slow host: there is no " +
                "network on this path."));
        }

        return await read;
    }

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
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(LiveBody);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        var addr = await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync");

        // Not Assert.NotNull: it prints "Value is null" and nothing else, and the five
        // reasons GetAddressAsync returns null are indistinguishable from that. This is what
        // cost #216 eight runs to attribute.
        Assert.True(addr != null, log.Explain(
            "GetAddressAsync returned null for the captured live sidecar body, which parses."));
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
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(LiveBody);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync");

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
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        var addr = await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync");

        Assert.True(addr != null, log.Explain(
            "GetAddressAsync returned null for a body that carries a port named 'game'."));
        Assert.Equal(7691, addr!.Port);
    }

    /// <summary>No port named <c>game</c> is a null, not a guess at another port.</summary>
    [Fact]
    public async Task MissingGamePort_ReturnsNull()
    {
        const string body = """
            {"status":{"address":"10.0.0.7","ports":[{"name":"metrics","port":31111}]}}
            """;
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        Assert.Null(await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync"));

        // Null for the RIGHT reason. This class's own opening remark warns that "a fallback
        // that fires for the wrong reason looks exactly like one that fires for the right
        // one", and until the SDK's log was captured nothing here checked which.
        Assert.True(log.Logged("has no port named"), log.Explain(
            "the read was null, but not because the port was missing."));
    }

    /// <summary>An empty port array, and no ports key at all, are both null.</summary>
    [Theory]
    [InlineData("""{"status":{"address":"10.0.0.7","ports":[]}}""")]
    [InlineData("""{"status":{"address":"10.0.0.7"}}""")]
    public async Task NoPorts_ReturnsNull(string body)
    {
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        Assert.Null(await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync"));
        Assert.True(log.Logged("has no port named"), log.Explain(
            "the read was null, but not because there was no game port."));
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
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        Assert.Null(await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync"));
        Assert.True(log.Logged("carries no address"), log.Explain(
            "the read was null, but not because the status carried no address."));
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
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        Assert.Null(await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync"));
        Assert.True(log.Logged("which is not a usable port"), log.Explain(
            $"the read was null, but not because port {port} was refused as unusable."));
    }

    /// <summary>A sidecar answering 500 is null and, above all, does not throw.</summary>
    [Fact]
    public async Task SidecarError_ReturnsNullWithoutThrowing()
    {
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(LiveBody, statusCode: 500);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        AgonesGameServerAddress? addr = null;
        Assert.Null(await Record.ExceptionAsync(async () =>
            addr = await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync")));
        Assert.Null(addr);
        Assert.True(log.Logged("returned 500"), log.Explain(
            "the read was null, but not because the sidecar answered 500."));
    }

    /// <summary>An unparsable body is null, not a crash on the server's start-up path.</summary>
    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"status\": {")]
    [InlineData("[1,2,3]")]
    public async Task MalformedJson_ReturnsNullWithoutThrowing(string body)
    {
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        AgonesGameServerAddress? addr = null;
        Assert.Null(await Record.ExceptionAsync(async () =>
            addr = await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync")));
        Assert.Null(addr);

        // The exception type is the only thing separating "could not read the body" from
        // "could not reach the sidecar": both reach the same catch and log the same text.
        Assert.True(log.Threw("JsonException"), log.Explain(
            "the read was null, but not because the body could not be parsed."));
    }

    /// <summary>No sidecar listening at all: null, no exception, one timeout.</summary>
    [Fact]
    public async Task AbsentSidecar_ReturnsNullWithoutThrowing()
    {
        int deadPort;
        using (var lease = new TestPorts.Lease()) { deadPort = lease.Port; }

        var log = new CapturingLogger();
        using var sdk = new HttpAgonesSdk(
            log, $"http://localhost:{deadPort}/", AbsentSidecarTimeout);

        AgonesGameServerAddress? addr = null;
        Assert.Null(await Record.ExceptionAsync(async () =>
            addr = await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync")));
        Assert.Null(addr);

        // Refused (HttpRequestException) where a closed local port refuses, timed out
        // (TaskCanceledException) where it does not. Either is "no sidecar"; neither is a
        // parse failure, and the point of asserting on both is that this test must not start
        // passing because the SDK stopped talking to the port at all.
        Assert.True(log.Threw("HttpRequestException") || log.Threw("TaskCanceledException"),
            log.Explain("the read was null, but not because the sidecar was unreachable."));
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
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(LiveBody);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        var state = await ReadAsync(sdk.GetStateAsync(), log, "GetStateAsync");

        Assert.True(state != null, log.Explain(
            "GetStateAsync returned null for the captured live sidecar body, which parses."));
        Assert.Equal("Ready", state);
    }

    /// <summary>An allocated GameServer reads as exactly the constant the gate compares to.</summary>
    [Fact]
    public async Task AllocatedState_MatchesTheConstantTheGateCompares()
    {
        const string body = """
            {"status":{"state":"Allocated","address":"10.0.0.7",
             "ports":[{"name":"game","port":7691}]}}
            """;
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(body);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        var state = await ReadAsync(sdk.GetStateAsync(), log, "GetStateAsync");

        Assert.True(state != null, log.Explain(
            "GetStateAsync returned null for a body carrying state 'Allocated'."));
        Assert.Equal(AgonesGameServerState.Allocated, state);
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
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(body, status);
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        Assert.Null(await ReadAsync(sdk.GetStateAsync(), log, "GetStateAsync"));
    }

    /// <summary>No sidecar at all is a null and never an exception.</summary>
    [Fact]
    public async Task StateRead_WithNoSidecar_IsNullAndDoesNotThrow()
    {
        var lease = new TestPorts.Lease();
        var dead = $"http://localhost:{lease.Port}/";
        lease.Dispose();

        var log = new CapturingLogger();
        using var sdk = new HttpAgonesSdk(log, dead, AbsentSidecarTimeout);

        string? state = "unset";
        Assert.Null(await Record.ExceptionAsync(async () =>
            state = await ReadAsync(sdk.GetStateAsync(), log, "GetStateAsync")));
        Assert.Null(state);
        Assert.True(log.Threw("HttpRequestException") || log.Threw("TaskCanceledException"),
            log.Explain("the read was null, but not because the sidecar was unreachable."));
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

    // ── the mechanism behind #216, pinned deterministically ──

    /// <summary>
    /// A sidecar that is <b>working and merely slow</b> reads as exactly the same null as a
    /// sidecar that answered rubbish, once a per-request budget is imposed that it misses.
    ///
    /// <para>This is issue #216 reproduced on purpose rather than waited for. The failing run
    /// was <c>LiveSidecarShape_YieldsAddressAndGamePort</c> returning null against a
    /// <b>correct body from a listener in the same process</b>, one run in eight, because a
    /// fixed 700ms budget expired while the host was busy elsewhere. Nothing about that is
    /// specific to 700ms or to eight runs — any fixed budget on this path has a load at which
    /// it does this — so the fix cannot be a larger budget, and this test is what says so.
    /// A 50ms budget against a 300ms sidecar is the same fact at a speed that cannot
    /// flake.</para>
    ///
    /// <para>Note what the SDK does <i>not</i> do, which is the reason the flake was
    /// expensive: it does not distinguish. The address read returns null, the caller
    /// advertises its configured address, and the only trace is a log line the suite used to
    /// throw away.</para>
    /// </summary>
    [Fact]
    public async Task ASidecarSlowerThanItsBudget_ReadsAsTheSameNullAsAnUnparsableOne()
    {
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(
            LiveBody, responseDelay: TimeSpan.FromMilliseconds(300));
        using var sdk = new HttpAgonesSdk(
            log, sidecar.BaseAddress, TimeSpan.FromMilliseconds(50));

        var addr = await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync");

        Assert.Null(addr);
        Assert.True(log.Threw("TaskCanceledException"), log.Explain(
            "a sidecar held past the per-request budget should have surfaced as a cancelled "
            + "request; if it did not, this test is no longer reproducing #216's mechanism."));
    }

    /// <summary>
    /// The same slow sidecar, with no budget imposed, parses. Which is the whole fix: the
    /// tests in this class assert on a parsed address and never on how quickly it arrived, so
    /// the budget was machinery that could only subtract.
    /// </summary>
    [Fact]
    public async Task TheSameSlowSidecar_ParsesWhenNoBudgetIsImposed()
    {
        var log = new CapturingLogger();
        using var sidecar = new FakeStatusSidecar(
            LiveBody, responseDelay: TimeSpan.FromMilliseconds(300));
        using var sdk = new HttpAgonesSdk(log, sidecar.BaseAddress, NoDeadline);

        var addr = await ReadAsync(sdk.GetAddressAsync(), log, "GetAddressAsync");

        Assert.True(addr != null, log.Explain(
            "a slow but correct sidecar must still parse when nothing is timing it."));
        Assert.Equal("192.168.65.3:7691", addr!.ToString());
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
        private readonly TimeSpan _responseDelay;

        public readonly ConcurrentQueue<RecordedRequest> Requests = new();

        public string BaseAddress { get; }

        /// <param name="body">What <c>GET /gameserver</c> answers with.</param>
        /// <param name="statusCode">Status to answer with.</param>
        /// <param name="responseDelay">
        /// Held before answering. Used only by the two tests that pin what a per-request
        /// budget does to a sidecar that is working but slow — the mechanism behind #216.
        /// </param>
        public FakeStatusSidecar(string body, int statusCode = 200, TimeSpan responseDelay = default)
        {
            _body = body;
            _statusCode = statusCode;
            _responseDelay = responseDelay;

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

                    if (_responseDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_responseDelay, _cts.Token);
                    }

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
