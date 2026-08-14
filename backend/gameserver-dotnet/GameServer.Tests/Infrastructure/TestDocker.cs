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

        // Start both reads WITHOUT blocking on them. A synchronous ReadToEnd() here
        // makes the timeout below unreachable: the read returns only when the child
        // closes the pipe, so a child that never exits parks this thread forever and
        // WaitForExit is never called. Reading stdout to completion before touching
        // stderr deadlocks for a second reason — a child that fills the 64 KiB stderr
        // buffer blocks on the write while we wait on the other pipe.
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (-1, Drain(stdout), "timed out");
        }
        return (proc.ExitCode, Drain(stdout), Drain(stderr));
    }

    /// <summary>
    /// Collect a pipe read that has already been started. The process is gone by the
    /// time this runs, so the task is finished or about to be; the bound is there so a
    /// stuck pipe degrades into an empty string instead of reintroducing the hang.
    /// </summary>
    private static string Drain(Task<string> read)
    {
        try { return read.Wait(TimeSpan.FromSeconds(10)) ? read.Result : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// The loopback port docker published <paramref name="containerPort"/> on, or null if
    /// the container is gone or publishes nothing.
    /// <para>
    /// Containers are started with <c>-p 127.0.0.1:0:&lt;port&gt;</c> and asked afterwards,
    /// rather than being told a port picked in advance. Picking one first meant binding
    /// port 0, reading it, releasing it, and hoping it survived until <c>docker run</c> got
    /// round to binding it — a gap any concurrent fixture could take, and the source of the
    /// "Address already in use" container launches. Docker's own bind is the allocation, so
    /// there is nothing left to race: the port is occupied from the moment it exists.
    /// </para>
    /// </summary>
    public static int? PublishedPort(string docker, string container, int containerPort)
    {
        var r = Exec(docker, $"port {container} {containerPort}/tcp", TimeSpan.FromSeconds(30));
        if (r.ExitCode != 0) return null;

        // One mapping per line, "127.0.0.1:49154" (or "0.0.0.0:49154"). Take the first.
        foreach (string line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string s = line.Trim();
            int idx = s.LastIndexOf(':');
            if (idx >= 0 && int.TryParse(s[(idx + 1)..], out int port) && port > 0)
                return port;
        }
        return null;
    }
}
