using System.Text.Json;
using GameServer.Observability.Logging;
using GameServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GameServer.Tests.Observability.Logging;

/// <summary>
/// Serialises every test that redirects <see cref="Console.Out"/> or builds a console-backed
/// <see cref="ILoggerFactory"/>. <c>Console.SetOut</c> is process-global, so two classes doing
/// it at once would steal each other's lines.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleLoggingCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "ConsoleLogging";
}

/// <summary>
/// End to end through <see cref="ServerLogging.CreateFactory"/>: a real console logger, stdout
/// redirected to a writer, the factory disposed (which drains the logger's queue) before the
/// output is read.
/// </summary>
[Collection(ConsoleLoggingCollection.Name)]
public class ServerLoggingTests
{
    private const string Game = "cuvara";
    private const string MapId = "map_07";
    private const string ServerId = "gs-e2e-1";

    private static readonly LogLevel[] AllLevels =
    {
        LogLevel.Trace, LogLevel.Debug, LogLevel.Information,
        LogLevel.Warning, LogLevel.Error, LogLevel.Critical,
    };

    /// <summary>
    /// Builds a factory with stdout redirected, runs <paramref name="act"/>, disposes the factory
    /// and returns everything written to stdout.
    /// </summary>
    private static string RunCapturingStdout(LogSettings settings, Action<ILoggerFactory> act)
    {
        TextWriter original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(captured));
        try
        {
            using (ILoggerFactory factory = ServerLogging.CreateFactory(settings, Game, MapId, ServerId))
            {
                act(factory);
            } // disposing the factory flushes the console logger's queue
        }
        finally
        {
            Console.SetOut(original);
        }

        return captured.ToString();
    }

    private static string[] Lines(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string[] LevelsOf(string stdout) =>
        Lines(stdout).Select(l =>
        {
            using JsonDocument d = JsonDocument.Parse(l);
            return d.RootElement.GetProperty("level").GetString()!;
        }).ToArray();

    private static void LogOneAtEveryLevel(ILogger logger)
    {
        foreach (LogLevel level in AllLevels)
            logger.Log(level, "event at {Level}", level);
    }

    // ── The line, end to end ─────────────────────────────────────────────────

    [Fact]
    public void EveryStdoutLine_ParsesAsOneJsonObjectWithTheRequiredFields()
    {
        string stdout = RunCapturingStdout(LogSettings.Default, f =>
        {
            ILogger a = f.CreateLogger("GameServer.Alpha");
            ILogger b = f.CreateLogger("GameServer.Beta");
            a.LogInformation("first");
            b.LogWarning("second with {Value}", 42);
            a.LogError(new InvalidOperationException("kaput"), "third");
        });

        string[] lines = Lines(stdout);
        Assert.Equal(3, lines.Length);
        foreach (string line in lines)
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            foreach (string field in new[] { "ts", "level", "category", "msg", "game", "map_id", "server_id" })
                Assert.True(root.TryGetProperty(field, out _), $"missing '{field}' in: {line}");
            Assert.Equal(Game, root.GetProperty("game").GetString());
            Assert.Equal(MapId, root.GetProperty("map_id").GetString());
            Assert.Equal(ServerId, root.GetProperty("server_id").GetString());
            Assert.Equal(TimeSpan.Zero, root.GetProperty("ts").GetDateTimeOffset().Offset);
        }
    }

    [Fact]
    public void MultiLineMessagesAndExceptions_StayOnOneStdoutLine()
    {
        Exception ex;
        try { throw new InvalidOperationException("multi\nline\r\nexception"); }
        catch (Exception e) { ex = e; }

        string stdout = RunCapturingStdout(LogSettings.Default, f =>
        {
            ILogger l = f.CreateLogger("Multi");
            l.LogInformation("a\nb\r\nc");
            l.LogError(ex, "failed\nbadly");
        });

        string[] lines = Lines(stdout);
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal("a\nb\r\nc", first.RootElement.GetProperty("msg").GetString());
        Assert.False(first.RootElement.TryGetProperty("exception", out _));
        Assert.Equal("failed\nbadly", second.RootElement.GetProperty("msg").GetString());
        Assert.Equal(ex.ToString(), second.RootElement.GetProperty("exception").GetString());
    }

    [Fact]
    public void TextFormat_KeepsTheConsoleLoggersOwnOutput_NotJson()
    {
        string stdout = RunCapturingStdout(new LogSettings(LogLevel.Information, LogFormat.Text), f =>
            f.CreateLogger("GameServer.TextCat").LogInformation("hello text"));

        Assert.Contains("hello text", stdout, StringComparison.Ordinal);
        Assert.Contains("GameServer.TextCat", stdout, StringComparison.Ordinal);
        Assert.Contains("info", stdout, StringComparison.Ordinal);
        string firstLine = Lines(stdout)[0].TrimStart();
        Assert.False(firstLine.StartsWith('{'), $"text format printed JSON: {firstLine}");
        Assert.DoesNotContain("\"map_id\"", stdout, StringComparison.Ordinal);
    }

    // ── The level knob ───────────────────────────────────────────────────────

    [Fact]
    public void AtTheDefault_OnlyInformationAndAboveComeOut()
    {
        string stdout = RunCapturingStdout(LogSettings.Default, f => LogOneAtEveryLevel(f.CreateLogger("Lvl")));

        Assert.Equal(new[] { "INFO", "WARN", "ERROR", "CRITICAL" }, LevelsOf(stdout));
    }

    [Fact]
    public void AtDebug_DebugComesOut_ButTraceDoesNot()
    {
        string stdout = RunCapturingStdout(
            new LogSettings(LogLevel.Debug, LogFormat.Json), f => LogOneAtEveryLevel(f.CreateLogger("Lvl")));

        string[] levels = LevelsOf(stdout);
        Assert.Contains("DEBUG", levels);
        Assert.DoesNotContain("TRACE", levels);
        Assert.Equal(new[] { "DEBUG", "INFO", "WARN", "ERROR", "CRITICAL" }, levels);
    }

    [Fact]
    public void AtError_WarningDoesNotComeOut()
    {
        string stdout = RunCapturingStdout(
            new LogSettings(LogLevel.Error, LogFormat.Json), f => LogOneAtEveryLevel(f.CreateLogger("Lvl")));

        string[] levels = LevelsOf(stdout);
        Assert.DoesNotContain("WARN", levels);
        Assert.Equal(new[] { "ERROR", "CRITICAL" }, levels);
    }

    [Theory]
    [InlineData(LogLevel.Trace, 6)]
    [InlineData(LogLevel.Debug, 5)]
    [InlineData(LogLevel.Information, 4)]
    [InlineData(LogLevel.Warning, 3)]
    [InlineData(LogLevel.Error, 2)]
    [InlineData(LogLevel.Critical, 1)]
    public void EveryMinimumLevel_PassesExactlyTheLevelsAtOrAboveIt(LogLevel minimum, int expectedCount)
    {
        string stdout = RunCapturingStdout(
            new LogSettings(minimum, LogFormat.Json), f => LogOneAtEveryLevel(f.CreateLogger("Lvl")));

        Assert.Equal(expectedCount, Lines(stdout).Length);
        string[] expectedLevels = new[] { "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "CRITICAL" }
            .Skip(6 - expectedCount).ToArray();
        Assert.Equal(expectedLevels, LevelsOf(stdout));
    }

    [Fact]
    public void AFilteredOutCall_NeverBuildsItsMessage()
    {
        bool built = false;
        RunCapturingStdout(LogSettings.Default, f =>
        {
            ILogger l = f.CreateLogger("Lazy");
            Assert.False(l.IsEnabled(LogLevel.Debug));
            Assert.True(l.IsEnabled(LogLevel.Information));
            l.Log(LogLevel.Debug, new EventId(0), new Spy(() => built = true), null, (s, _) => s.ToString());
        });

        Assert.False(built, "the message formatter ran for a call below the minimum level");
    }

    // ── Concurrency ──────────────────────────────────────────────────────────

    [Fact]
    public void SeveralThreadsLoggingAtOnce_ProduceOnlyWholeLines()
    {
        const int threads = 8;
        const int perThread = 250;
        string pad = new string('p', 300);

        string stdout = RunCapturingStdout(LogSettings.Default, f =>
        {
            using var start = new ManualResetEventSlim(false);
            Thread[] workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                ILogger l = f.CreateLogger($"Worker.{t}");
                start.Wait();
                for (int i = 0; i < perThread; i++)
                    l.LogInformation("t={Thread} i={Index} multi\nline {Pad}", t, i, pad);
            })).ToArray();
            foreach (Thread w in workers) w.Start();
            start.Set();
            foreach (Thread w in workers) w.Join();
        });

        string[] lines = Lines(stdout);
        Assert.Equal(threads * perThread, lines.Length);

        var seen = new HashSet<(int, int)>();
        foreach (string line in lines)
        {
            using JsonDocument doc = JsonDocument.Parse(line); // a torn or interleaved line throws here
            string msg = doc.RootElement.GetProperty("msg").GetString()!;
            string head = msg[..msg.IndexOf(" multi", StringComparison.Ordinal)];
            string[] parts = head.Split(' ');
            int t = int.Parse(parts[0]["t=".Length..]);
            int i = int.Parse(parts[1]["i=".Length..]);
            Assert.EndsWith(pad, msg, StringComparison.Ordinal);
            Assert.Equal($"Worker.{t}", doc.RootElement.GetProperty("category").GetString());
            Assert.True(seen.Add((t, i)), $"duplicate event t={t} i={i}");
        }

        Assert.Equal(threads * perThread, seen.Count);
    }

    // ── Allocation ───────────────────────────────────────────────────────────

    /// <summary>
    /// With the default level, a call below it costs the calling thread nothing. This is what
    /// keeps a stray <c>LogDebug</c> on the tick thread free. Measured the way the other
    /// zero-allocation guards here are: <see cref="ThreadAllocation.Begin"/>, an exact zero.
    /// The tick path itself is covered by the existing tick allocation tests (see the report:
    /// <c>GameplayAllocationTests</c>, <c>SnapshotAllocationTests</c>, <c>SnapshotV3EncoderTests</c>);
    /// this test covers the logger call that could be added to it.
    /// </summary>
    [Fact]
    public void AtTheDefaultLevel_AFilteredOutCall_AllocatesNothingOnTheCallingThread()
    {
        TextWriter original = Console.Out;
        Console.SetOut(TextWriter.Synchronized(new StringWriter()));
        try
        {
            using ILoggerFactory factory = ServerLogging.CreateFactory(LogSettings.Default, Game, MapId, ServerId);
            ILogger logger = factory.CreateLogger("GameServer.Tick");
            Assert.False(logger.IsEnabled(LogLevel.Debug));
            Assert.False(logger.IsEnabled(LogLevel.Trace));

            void Round()
            {
                logger.LogDebug("no arguments, filtered out");
                logger.LogTrace("no arguments either");
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("guarded {Value}", 1);
            }

            for (int i = 0; i < 200; i++) Round(); // warm-up: JIT, tiering, first-call statics

            const int trials = 5;
            for (int trial = 0; trial < trials; trial++)
            {
                long before = ThreadAllocation.Begin();
                for (int i = 0; i < 1000; i++) Round();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(0L, allocated);
            }
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private sealed class Spy(Action onToString)
    {
        public override string ToString()
        {
            onToString();
            return "spy";
        }
    }
}
