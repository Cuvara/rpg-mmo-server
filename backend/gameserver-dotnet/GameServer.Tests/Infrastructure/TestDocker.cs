using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// Docker plumbing shared by the throwaway-container fixtures (postgres, redis).
///
/// Everything here degrades instead of throwing: a missing docker binary or a
/// failed launch is reported as "unavailable" so a fixture can skip its tests
/// rather than fail them with an environment error.
/// </summary>
internal static class TestDocker
{
    /// <summary>
    /// The docker binary usable on this machine, or null when there is none.
    /// <c>docker.exe</c> is tried too, for WSL developers driving Docker Desktop.
    /// </summary>
    public static string? Find()
    {
        foreach (var candidate in new[] { "docker", "docker.exe" })
        {
            try
            {
                if (Exec(candidate, "version --format {{.Server.Version}}", TimeSpan.FromSeconds(30)).ExitCode == 0)
                    return candidate;
            }
            catch
            {
                // Binary not on PATH — try the next candidate.
            }
        }
        return null;
    }

    /// <summary>
    /// Run a process and capture its output. Never throws: a launch failure is
    /// reported as a non-zero exit code.
    /// </summary>
    public static (int ExitCode, string StdOut, string StdErr) Exec(string file, string args, TimeSpan timeout)
    {
        // Spawning processes can fail transiently under memory pressure
        // (Windows: "The paging file is too small for this operation to complete").
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return ExecOnce(file, args, timeout);
            }
            catch (Exception ex) when (attempt < 2)
            {
                Console.WriteLine($"[TestDocker] '{file} {args}' failed to start ({ex.Message}); retrying");
                Thread.Sleep(2000);
            }
            catch (Exception ex)
            {
                return (-127, "", ex.Message);
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) ExecOnce(string file, string args, TimeSpan timeout)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            return (-1, stdout, "timed out");
        }
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>A free loopback port, so containers never collide with live services.</summary>
    public static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
