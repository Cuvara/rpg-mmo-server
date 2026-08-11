using System.Net;
using System.Text;
using GameServer.Net.Transport;
using GameServer.Persistence;
using GameServer.Server;
using GameServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Net;

/// <summary>
/// The tests that actually matter for this feature: a Go client built against
/// <c>backend/shared/transport</c> — the same package the gateway and any Go
/// client use — must complete a session against the C# KCP listener.
/// </summary>
/// <remarks>
/// <para>
/// A C#-to-C# loopback proves nothing here. The C# KCP stack is a port of
/// kcp-go's protocol; the only way to know the port is faithful is to put the
/// real kcp-go on the other end. That is what <c>interop/kcpprobe</c> is for.
/// </para>
/// <para>
/// These tests skip (not fail) when no Go toolchain is present, because the
/// dotnet CI image has no Go. The Go-side CI and local runs cover them.
/// </para>
/// </remarks>
public class KcpInteropTests
{
    private const string TestKeyHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [SkippableFact]
    public void GoDeriveKey_MatchesCSharpDeriveKey()
    {
        var go = GoProbe.Require();

        // A hex key must be decoded verbatim by both sides.
        string goHex = go.Run("derivekey", TestKeyHex).Trim();
        Assert.Equal(TestKeyHex, goHex);
        Assert.Equal(TestKeyHex, Convert.ToHexString(KcpCrypto.DeriveKey(TestKeyHex)).ToLowerInvariant());

        // A passphrase must be stretched to the same 32 bytes by Go's HKDF and .NET's.
        // If this drifts, an operator setting the same TRANSPORT_KEY on both halves
        // silently gets two different keys and every join times out with no error.
        const string passphrase = "correct horse battery staple";
        string goDerived = go.Run("derivekey", passphrase).Trim();
        string csDerived = Convert.ToHexString(KcpCrypto.DeriveKey(passphrase)).ToLowerInvariant();
        Assert.Equal(goDerived, csDerived);
    }

    [SkippableTheory]
    [InlineData("")]            // plaintext
    [InlineData(TestKeyHex)]    // AES-256, hex key
    [InlineData("shared-pass")] // AES-256, passphrase stretched through HKDF
    public async Task GoClient_EchoesThroughCSharpListener(string key)
    {
        var go = GoProbe.Require();

        using var listener = new KcpListener(new IPEndPoint(IPAddress.Loopback, 0), key, NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var echo = EchoOnceAsync(listener, cts.Token);

        string output = go.Run("echo", $"127.0.0.1:{listener.LocalEndPoint.Port}", key, "hello-realtime");
        Assert.Contains("ECHO hello-realtime", output);

        Assert.Equal("hello-realtime", await echo);
    }

    [SkippableTheory]
    // Listener encrypted, client plaintext.
    [InlineData(TestKeyHex, "")]
    // Listener plaintext, client encrypted.
    [InlineData("", TestKeyHex)]
    // Both encrypted, different keys.
    [InlineData(TestKeyHex, "a-different-passphrase")]
    public void MismatchedTransportKey_FailsClosed(string listenerKey, string clientKey)
    {
        var go = GoProbe.Require();

        using var listener = new KcpListener(new IPEndPoint(IPAddress.Loopback, 0), listenerKey, NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = EchoOnceAsync(listener, cts.Token);

        // There is no negotiation and no error frame: a peer without the right key
        // emits datagrams that fail the CRC and are dropped, so the client's read
        // simply times out. Silence IS the expected failure.
        var (exitCode, stdout, stderr) = go.TryRun("echo", $"127.0.0.1:{listener.LocalEndPoint.Port}", clientKey, "hello-realtime");
        Assert.True(exitCode != 0,
            $"a peer with the wrong key must never complete a session; got exit 0 with stdout={stdout} stderr={stderr}");
        Assert.DoesNotContain("ECHO hello-realtime", stdout);
    }

    [SkippableTheory]
    [InlineData("")]         // plaintext KCP
    [InlineData(TestKeyHex)] // encrypted KCP
    public async Task GoClient_CompletesAFullJoinAgainstTheRealServer(string key)
    {
        var go = GoProbe.Require();

        const string serverId = "gs-kcp-interop";
        const string joinSecret = "join-secret-32-bytes-kcpkcpkcpkcp";
        int port = FreeUdpPort();

        var server = new GameServerHost(new ServerOptions
        {
            ServerAddr = $":{port}",
            Transport = TransportKind.Kcp,
            TransportKey = key,
            ServerId = serverId,
            MapId = "map_kcp_interop",
            TickRate = 20,
            Capacity = 8,
            JoinTokenSecret = joinSecret,
            SaveInterval = TimeSpan.FromSeconds(30),
            HoldTtl = TimeSpan.FromSeconds(1),
            PlayerStore = new MemoryPlayerStore(),
            LoggerFactory = NullLoggerFactory.Instance
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runTask = server.RunAsync($":{port}", cts.Token);

        try
        {
            string token = TestHelpers.CreateTestJwt("user-kcp", serverId, joinSecret);
            string output = go.Run("join", $"127.0.0.1:{port}", key, token);

            // The whole gameplay hop over KCP: join accepted, inputs applied, snapshots
            // flowing back. "moved=True" means the inputs really reached the tick loop
            // rather than the client merely reading its own keyframe.
            Assert.Contains("JOINED user=user-kcp", output);
            Assert.Contains("OK", output);
            Assert.Contains("moved=true", output);
        }
        finally
        {
            cts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
            await server.DisposeAsync();
        }
    }

    /// <summary>Picks a free UDP port by binding one and releasing it.</summary>
    private static int FreeUdpPort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>Accepts one session and echoes the first chunk back.</summary>
    private static async Task<string> EchoOnceAsync(KcpListener listener, CancellationToken ct)
    {
        var session = await listener.AcceptAsync(ct);
        var chunk = await session.ReadChunkAsync(ct);
        if (chunk == null) return "";
        session.Write(chunk);
        return Encoding.UTF8.GetString(chunk);
    }
}

/// <summary>
/// Locates and runs the <c>interop/kcpprobe</c> Go harness, or reports that no
/// Go toolchain is available so the caller can skip.
/// </summary>
internal sealed class GoProbe
{
    private static readonly Lazy<GoProbe?> Instance = new(TryCreate, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string _binary;

    private GoProbe(string binary) => _binary = binary;

    /// <summary>Returns the harness, skipping the calling test when Go is unavailable.</summary>
    public static GoProbe Require()
    {
        var probe = Instance.Value;
        Skip.If(probe == null,
            "No Go toolchain found (tried 'go' and ~/go/bin/go), or the kcpprobe harness failed to build. " +
            "KCP interop is verified against the real kcp-go client, so it cannot run here.");
        return probe!;
    }

    private static GoProbe? TryCreate()
    {
        string? dir = FindProbeDir();
        if (dir == null) return null;

        foreach (string candidate in GoCandidates())
        {
            try
            {
                if (TestDocker.Exec(candidate, "version", TimeSpan.FromSeconds(30)).ExitCode != 0) continue;
            }
            catch { continue; }

            // Build once up front: `go run` per invocation would pay compilation on
            // every test and muddy the timeouts the mismatch tests rely on.
            string output = Path.Combine(Path.GetTempPath(), "kcpprobe-" + Environment.ProcessId);
            var build = ExecIn(dir, candidate, $"build -o \"{output}\" .", TimeSpan.FromMinutes(5));
            if (build.ExitCode != 0) continue;
            return new GoProbe(output);
        }
        return null;
    }

    private static IEnumerable<string> GoCandidates()
    {
        yield return "go";
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "go", "bin", "go");
    }

    /// <summary>Walks up from the test binary to the harness source directory.</summary>
    private static string? FindProbeDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "interop", "kcpprobe");
            if (File.Exists(Path.Combine(candidate, "go.mod"))) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Runs the harness and fails the test on a non-zero exit.</summary>
    public string Run(params string[] args)
    {
        var (exitCode, stdout, stderr) = TryRun(args);
        Assert.True(exitCode == 0, $"kcpprobe {string.Join(' ', args)} failed ({exitCode}): {stderr}\n{stdout}");
        return stdout;
    }

    /// <summary>Runs the harness and returns its result without asserting.</summary>
    public (int ExitCode, string StdOut, string StdErr) TryRun(params string[] args) =>
        TestDocker.Exec(_binary, string.Join(' ', args.Select(Quote)), TimeSpan.FromSeconds(60));

    private static string Quote(string arg) => arg.Length == 0 ? "\"\"" : arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static (int ExitCode, string StdOut, string StdErr) ExecIn(
        string workingDir, string file, string args, TimeSpan timeout)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // A Go install under $HOME/go doubles as GOROOT here, which leaves GOPATH and
        // the module cache unset unless we say so.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, "go", "bin", "go")))
        {
            psi.Environment["GOROOT"] = Path.Combine(home, "go");
            psi.Environment.TryAdd("GOPATH", Path.Combine(home, "gopath"));
            psi.Environment.TryAdd("GOMODCACHE", Path.Combine(home, "go", "pkg", "mod"));
        }

        using var proc = System.Diagnostics.Process.Start(psi)!;

        // Reads started, not awaited — see TestDocker.ExecOnce. `go build` on a cold
        // module cache is exactly the shape that punishes the old order: minutes of
        // work with progress on stderr while the caller blocks reading stdout.
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (-1, Drain(stdout), Drain(stderr) + "\n[timeout]");
        }
        return (proc.ExitCode, Drain(stdout), Drain(stderr));
    }

    private static string Drain(Task<string> read)
    {
        try { return read.Wait(TimeSpan.FromSeconds(10)) ? read.Result : ""; }
        catch { return ""; }
    }
}
