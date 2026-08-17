using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GameServer.Agones;

/// <summary>
/// The externally-dialable address Agones assigned to this GameServer: the node address
/// from <c>status.address</c> paired with the host port Agones bound for the container
/// port named <see cref="GamePortName"/>.
///
/// <para>Under <c>portPolicy: Dynamic</c> this pair is the <b>only</b> correct value to
/// advertise to clients. The container still listens on its configured port (<c>:9000</c>
/// in the fleet manifests) and no static configuration can name the host port, because
/// Agones picks it at scheduling time — ADR-15 decision 2, option (A).</para>
///
/// <para><b>Scope limit, and it is not theoretical — it was measured.</b>
/// <c>status.address</c> is the <i>node</i> address, routable from wherever that node is
/// routable and no further. On the k3d dev cluster it is the node container's address on
/// the Docker network (<c>172.20.0.3</c>), and a client cannot dial it: from WSL2 a
/// connection to <c>172.20.0.3:7008</c> is refused and from Windows — where the Unity
/// client runs — <c>Test-NetConnection</c> reports False, while <c>127.0.0.1:7008</c>,
/// published by the k3d serverlb, answers from both. So the <b>port</b> from the status is
/// authoritative and nothing else can supply it, while the <b>host</b> is a deployment fact
/// the cluster cannot know. That is what <see cref="WithHost"/> and
/// <c>GAMESERVER_ADVERTISE_HOST</c> exist for.</para>
/// </summary>
/// <param name="Address">Host part — from <c>status.address</c>, or an operator override.</param>
/// <param name="Port">Host port Agones bound for the <c>game</c> port. Never configurable.</param>
public sealed record AgonesGameServerAddress(string Address, int Port)
{
    /// <summary>
    /// The container port name the address is composed from. Matches <c>ports[].name</c> in
    /// <c>deploy/agones/fleet-*.yaml</c> and the <c>gamePortName</c> constant in the
    /// gateway's <c>registry/agones_allocator.go</c> — the three must agree, and selecting
    /// by name rather than by index is what keeps them agreeing when a fleet grows a second
    /// port (metrics, for instance) that lands ahead of the game port in the array.
    /// </summary>
    public const string GamePortName = "game";

    /// <summary>
    /// Replace the host while keeping the Agones-assigned port.
    ///
    /// <para>The port is deliberately not a parameter. It is the one part of this address
    /// that only Agones can supply, and letting configuration reach it would recreate the
    /// exact bug the status read exists to fix.</para>
    /// </summary>
    public AgonesGameServerAddress WithHost(string host) => this with { Address = host };

    /// <summary>
    /// Clean an operator-supplied advertise host into something that can be composed with a
    /// port. Returns null when there is nothing usable, in which case the caller keeps
    /// <c>status.address</c>.
    ///
    /// <para>Trims, treats blank as unset, and unwraps a bracketed IPv6 literal. It also
    /// accepts — with a warning — a value that already carries a port: someone setting
    /// <c>GAMESERVER_ADVERTISE_HOST</c> to <c>127.0.0.1:7000</c> has confused it with
    /// <c>GAMESERVER_PUBLIC_ADDR</c>, and their intent for the host part is unambiguous.
    /// Honouring the host and warning beats refusing, because refusing falls back to the
    /// node address that is already known not to work.</para>
    /// </summary>
    /// <param name="raw">Raw configured value; null or blank means unset.</param>
    /// <param name="logger">Optional; receives the confused-variable warning.</param>
    public static string? NormalizeHostOverride(string? raw, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();

        // "[::1]" or "[::1]:7000" — bracketed IPv6, with or without a port.
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']');
            if (close > 1)
            {
                var inner = value[1..close];
                if (close + 1 < value.Length)
                    WarnPortInHost(logger, value, inner);
                return string.IsNullOrWhiteSpace(inner) ? null : inner;
            }
            return null; // "[" with no "]" is not an address
        }

        // A bare IP literal, v4 or v6, is already exactly the host. Checked before the
        // colon heuristic below so "::1" and "2001:db8::1" are not mistaken for host:port.
        if (System.Net.IPAddress.TryParse(value, out _)) return value;

        // "host:port" — only when the tail is entirely digits. A hostname does not contain
        // a colon, so anything else with one is left alone rather than silently truncated.
        int colon = value.LastIndexOf(':');
        if (colon > 0 && colon < value.Length - 1)
        {
            var tail = value[(colon + 1)..];
            if (tail.All(char.IsAsciiDigit))
            {
                var host = value[..colon];
                WarnPortInHost(logger, value, host);
                return string.IsNullOrWhiteSpace(host) ? null : host;
            }
        }

        return value;
    }

    private static void WarnPortInHost(ILogger? logger, string raw, string host) =>
        logger?.LogWarning(
            "GAMESERVER_ADVERTISE_HOST is '{Raw}', which includes a port. It is HOST-ONLY — " +
            "the port always comes from the Agones GameServer status, because only Agones " +
            "knows it under portPolicy: Dynamic. Using '{Host}' and ignoring the rest; " +
            "GAMESERVER_PUBLIC_ADDR is the variable that takes a full host:port, and it is " +
            "only used when Agones is disabled.",
            raw, host);

    /// <summary>
    /// The <c>host:port</c> form written into the server registry. An IPv6 host is bracketed,
    /// since the gateway hands this string to clients verbatim and a bare
    /// <c>::1:7008</c> is not parsable as an endpoint.
    /// </summary>
    public override string ToString() =>
        System.Net.IPAddress.TryParse(Address, out var ip)
        && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{Address}]:{Port}"
            : $"{Address}:{Port}";
}

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

    /// <summary>
    /// Read the address Agones assigned to this GameServer, or null when it is unknown.
    ///
    /// <para>Null is the normal answer in three cases and none of them is an error the
    /// caller should escalate: Agones is not in use, the sidecar did not answer, or the
    /// status carries no usable <c>status.address</c> + <c>game</c> port pair. Every caller
    /// must fall back to its configured address on null — running outside a cluster has to
    /// keep behaving exactly as it did before this method existed.</para>
    ///
    /// <para>Only meaningful <b>after</b> <see cref="ReadyAsync"/>: the address is assigned
    /// when the pod is scheduled, so an earlier read races the scheduler.</para>
    /// </summary>
    Task<AgonesGameServerAddress?> GetAddressAsync();
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

    /// <inheritdoc/>
    /// <remarks>
    /// Always null: there is no Agones here, so there is no assigned address, and the
    /// caller keeps the address it was configured with.
    /// </remarks>
    public Task<AgonesGameServerAddress?> GetAddressAsync() =>
        Task.FromResult<AgonesGameServerAddress?>(null);
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

    /// <summary>
    /// Read the GameServer object from the sidecar and compose the dialable address from it.
    ///
    /// <para>Verified live against Agones <b>1.59.0</b>: <c>GET /gameserver</c> answers 200
    /// with the object in snake_case, of which only three fields are used —
    /// <c>status.address</c>, and the <c>name</c>/<c>port</c> of the entry in
    /// <c>status.ports</c> named <c>game</c>:</para>
    /// <code>
    /// {"object_meta":{...},
    ///  "status":{"state":"Ready","address":"192.168.65.3",
    ///            "ports":[{"name":"game","port":7691}], ...}}
    /// </code>
    ///
    /// <para>The port is selected <b>by name, never by index</b>. A fleet that later
    /// declares a second container port would silently start advertising the wrong one.</para>
    ///
    /// <para>Like every other method here, this never throws: transport failure, timeout,
    /// non-2xx, unparsable body and a status missing the fields all return null, and the
    /// caller advertises its configured address instead.</para>
    /// </summary>
    public async Task<AgonesGameServerAddress?> GetAddressAsync()
    {
        try
        {
            using var resp = await _http.GetAsync("gameserver").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Agones sidecar GET /gameserver returned {Status}",
                    (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Source-generated contract, not the reflection resolver: this module is
            // NativeAOT and GameServer.Tests/Aot/JsonReflectionGuardTests enforces it.
            var gs = JsonSerializer.Deserialize(body, AgonesJsonContext.Default.AgonesGameServerJson);

            var status = gs?.Status;
            if (status == null || string.IsNullOrWhiteSpace(status.Address))
            {
                _logger.LogWarning(
                    "Agones GameServer status carries no address; the pod may not be scheduled yet");
                return null;
            }

            var gamePort = status.Ports?.FirstOrDefault(
                p => string.Equals(p.Name, AgonesGameServerAddress.GamePortName, StringComparison.Ordinal));
            if (gamePort == null)
            {
                _logger.LogWarning(
                    "Agones GameServer status has no port named '{Name}' (found: {Found}); " +
                    "the fleet manifest's ports[].name and this server must agree",
                    AgonesGameServerAddress.GamePortName,
                    status.Ports == null || status.Ports.Count == 0
                        ? "none"
                        : string.Join(", ", status.Ports.Select(p => p.Name ?? "<unnamed>")));
                return null;
            }

            if (gamePort.Port <= 0 || gamePort.Port > 65535)
            {
                _logger.LogWarning(
                    "Agones GameServer status port '{Name}' is {Port}, which is not a usable port",
                    AgonesGameServerAddress.GamePortName, gamePort.Port);
                return null;
            }

            return new AgonesGameServerAddress(status.Address!.Trim(), gamePort.Port);
        }
        catch (Exception ex)
        {
            // HttpRequestException (no sidecar), TaskCanceledException (timeout),
            // JsonException (a body we cannot read), ObjectDisposedException (teardown).
            _logger.LogWarning(ex, "Agones sidecar GET /gameserver failed");
            return null;
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

// ── GameServer status DTOs ──
//
// Only the fields this server actually consumes are modelled. The sidecar's object is
// much larger (object_meta, spec.health, status.addresses, players, counters, lists);
// System.Text.Json ignores what is not declared, so an Agones upgrade that adds fields
// is a non-event, while one that renames status.address or status.ports[].name is a
// null return and a warning rather than a crash.
//
// snake_case, matching the sidecar's own wire form as observed on Agones 1.59.0.

/// <summary>The subset of the Agones GameServer object this server reads.</summary>
internal sealed class AgonesGameServerJson
{
    [JsonPropertyName("status")] public AgonesStatusJson? Status { get; set; }
}

/// <summary>The subset of <c>GameServer.status</c> this server reads.</summary>
internal sealed class AgonesStatusJson
{
    /// <summary>Node address the pod's host ports are published on.</summary>
    [JsonPropertyName("address")] public string? Address { get; set; }

    /// <summary>Host ports Agones bound, one per named container port.</summary>
    [JsonPropertyName("ports")] public List<AgonesPortJson>? Ports { get; set; }
}

/// <summary>One entry of <c>GameServer.status.ports</c>.</summary>
internal sealed class AgonesPortJson
{
    /// <summary>Container port name from the fleet manifest — <c>game</c> is the one used.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>Host port Agones assigned to it.</summary>
    [JsonPropertyName("port")] public int Port { get; set; }
}

/// <summary>AOT-safe JSON contract for the Agones GameServer status read.</summary>
[JsonSerializable(typeof(AgonesGameServerJson))]
internal sealed partial class AgonesJsonContext : JsonSerializerContext;

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
