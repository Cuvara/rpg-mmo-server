using System.Globalization;
using System.Text.Json;
using GameServer.Observability.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Observability.Logging;

/// <summary>
/// The shape of one log line, driving <see cref="JsonLinesConsoleFormatter"/> directly (no
/// console, so nothing here shares process-global state with another test class).
/// </summary>
public class JsonLinesConsoleFormatterTests
{
    private const string Game = "cuvara";
    private const string MapId = "map_01";
    private const string ServerId = "gs-test-7";

    private static JsonLinesConsoleFormatter NewFormatter() => new(Game, MapId, ServerId);

    private static string Format(
        LogLevel level, string category, string? message, Exception? exception = null,
        JsonLinesConsoleFormatter? formatter = null)
    {
        var writer = new StringWriter();
        var entry = new LogEntry<string?>(
            level, category, new EventId(0), message, exception, (s, _) => s!);
        (formatter ?? NewFormatter()).Write(in entry, scopeProvider: null, writer);
        return writer.ToString();
    }

    /// <summary>Asserts the text is exactly one line ending in one newline, and parses it.</summary>
    private static JsonDocument ParseSingleLine(string output)
    {
        Assert.EndsWith("\n", output, StringComparison.Ordinal);
        string body = output[..^1];
        Assert.DoesNotContain('\n', body);
        Assert.DoesNotContain('\r', body);
        return JsonDocument.Parse(body);
    }

    [Fact]
    public void AnEvent_IsOneLineThatParsesAsOneJsonObjectWithAllTheFields()
    {
        string output = Format(LogLevel.Information, "GameServer.Net.Listener", "player joined");

        using JsonDocument doc = ParseSingleLine(output);
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("INFO", root.GetProperty("level").GetString());
        Assert.Equal("GameServer.Net.Listener", root.GetProperty("category").GetString());
        Assert.Equal("player joined", root.GetProperty("msg").GetString());
        Assert.Equal(Game, root.GetProperty("game").GetString());
        Assert.Equal(MapId, root.GetProperty("map_id").GetString());
        Assert.Equal(ServerId, root.GetProperty("server_id").GetString());
        Assert.True(root.TryGetProperty("ts", out _));
    }

    [Fact]
    public void Ts_IsUtcRfc3339_AndIsNow()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);
        string output = Format(LogLevel.Warning, "c", "m");
        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(1);

        using JsonDocument doc = ParseSingleLine(output);
        string ts = doc.RootElement.GetProperty("ts").GetString()!;

        Assert.EndsWith("Z", ts, StringComparison.Ordinal);
        Assert.True(
            DateTimeOffset.TryParseExact(
                ts, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed),
            $"ts '{ts}' is not RFC 3339 with milliseconds");
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.InRange(parsed, before, after);

        // The generic reader a shipper would use agrees.
        Assert.True(DateTimeOffset.TryParse(
            ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset generic));
        Assert.Equal(TimeSpan.Zero, generic.Offset);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRACE")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARN")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public void EachLevel_MapsToItsDocumentedValue(LogLevel level, string expected)
    {
        using JsonDocument doc = ParseSingleLine(Format(level, "c", "m"));
        Assert.Equal(expected, doc.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public void FourLevels_AreSpelledLikeTheGoGatewaysSlogJson()
    {
        // slog's JSON handler writes DEBUG, INFO, WARN and ERROR; a shipper maps one field.
        string[] slogLevels = { "DEBUG", "INFO", "WARN", "ERROR" };
        LogLevel[] ours = { LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error };
        for (int i = 0; i < ours.Length; i++)
        {
            using JsonDocument doc = ParseSingleLine(Format(ours[i], "c", "m"));
            Assert.Equal(slogLevels[i], doc.RootElement.GetProperty("level").GetString());
        }
    }

    [Fact]
    public void FieldOrder_IsTsLevelCategoryMsgThenIdentity()
    {
        using JsonDocument doc = ParseSingleLine(Format(LogLevel.Information, "c", "m"));
        string[] names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "ts", "level", "category", "msg", "game", "map_id", "server_id" }, names);
    }

    [Fact]
    public void WithoutAnException_ThereIsNoExceptionField()
    {
        using JsonDocument doc = ParseSingleLine(Format(LogLevel.Error, "c", "no exception here"));
        Assert.False(doc.RootElement.TryGetProperty("exception", out _));
    }

    [Fact]
    public void AMultiLineMessage_StaysOnOneLine_AndRoundTrips()
    {
        const string message = "line one\nline two\r\nline three\rend\ttab";

        using JsonDocument doc = ParseSingleLine(Format(LogLevel.Information, "c", message));

        Assert.Equal(message, doc.RootElement.GetProperty("msg").GetString());
    }

    [Fact]
    public void AnExceptionWithAMultiLineStackTrace_StaysOnOneLine_AndRoundTrips()
    {
        Exception thrown = ThrowAndCatchNested();
        string expected = thrown.ToString();
        Assert.Contains('\n', expected); // the premise: the stack trace really is multi-line

        string output = Format(LogLevel.Error, "GameServer.Tick", "tick failed", thrown);

        using JsonDocument doc = ParseSingleLine(output);
        Assert.Equal("tick failed", doc.RootElement.GetProperty("msg").GetString());
        Assert.Equal(expected, doc.RootElement.GetProperty("exception").GetString());
        Assert.Contains(nameof(ThrowAndCatchNested), doc.RootElement.GetProperty("exception").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionWithNoMessage_StillWritesAnEmptyMsgString()
    {
        var writer = new StringWriter();
        var entry = new LogEntry<string?>(
            LogLevel.Error, "c", new EventId(0), null, new InvalidOperationException("boom"), (s, _) => s!);
        NewFormatter().Write(in entry, null, writer);

        using JsonDocument doc = ParseSingleLine(writer.ToString());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("msg").ValueKind);
        Assert.Equal("", doc.RootElement.GetProperty("msg").GetString());
        Assert.Contains("boom", doc.RootElement.GetProperty("exception").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoMessageAndNoException_WritesNothing()
    {
        Assert.Equal("", Format(LogLevel.Information, "c", null));
    }

    [Theory]
    [InlineData("say \"hello\"")]
    [InlineData(@"C:\path\to\file and \n literal backslash-n")]
    [InlineData("tab\there, bell\a, nul\0, esc\u001b[31m, unit-sep\u001f")]
    [InlineData("non-ASCII: h\u00e9llo w\u00f6rld, Ti\u1ebfng Vi\u1ec7t, \u65e5\u672c\u8a9e, emoji \ud83d\ude00")]
    [InlineData("html-ish <b>&amp;</b> 'single' + plus")]
    [InlineData("separators: \u2028 line sep, \u2029 para sep, \u0085 next line")]
    [InlineData("")]
    public void CharactersThatNeedEscaping_RoundTripExactly(string message)
    {
        string output = Format(LogLevel.Information, "cat\"egory\\x", message);

        using JsonDocument doc = ParseSingleLine(output);
        Assert.Equal(message, doc.RootElement.GetProperty("msg").GetString());
        Assert.Equal("cat\"egory\\x", doc.RootElement.GetProperty("category").GetString());
    }

    [Fact]
    public void ALoneSurrogate_IsReplacedRatherThanLosingTheEventOrThrowing()
    {
        string bad = "before \ud83d after";

        string output = Format(LogLevel.Warning, "c", bad);

        using JsonDocument doc = ParseSingleLine(output);
        string msg = doc.RootElement.GetProperty("msg").GetString()!;
        Assert.StartsWith("before ", msg, StringComparison.Ordinal);
        Assert.EndsWith(" after", msg, StringComparison.Ordinal);
        Assert.Equal("WARN", doc.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public void IdentityValuesNeedingEscaping_RoundTripOnEveryLine()
    {
        var formatter = new JsonLinesConsoleFormatter("ga\"me", "map\\01", "srv\n1");

        using JsonDocument doc = ParseSingleLine(Format(LogLevel.Information, "c", "m", formatter: formatter));

        Assert.Equal("ga\"me", doc.RootElement.GetProperty("game").GetString());
        Assert.Equal("map\\01", doc.RootElement.GetProperty("map_id").GetString());
        Assert.Equal("srv\n1", doc.RootElement.GetProperty("server_id").GetString());
    }

    [Fact]
    public void ALargeMessage_StillFormsOneValidLine_AndTheNextLineIsUnaffected()
    {
        // Larger than the per-thread buffer's retention cap, so the buffer is dropped and rebuilt.
        string big = new string('x', 200 * 1024) + "\ntail";
        JsonLinesConsoleFormatter formatter = NewFormatter();

        using (JsonDocument first = ParseSingleLine(Format(LogLevel.Information, "c", big, formatter: formatter)))
            Assert.Equal(big, first.RootElement.GetProperty("msg").GetString());

        using JsonDocument second = ParseSingleLine(Format(LogLevel.Information, "c", "after", formatter: formatter));
        Assert.Equal("after", second.RootElement.GetProperty("msg").GetString());
    }

    [Fact]
    public void TemplatedState_IsFormattedIntoMsg()
    {
        var sink = new StringWriter();
        ILogger logger = new FormatterDrivenLogger(NewFormatter(), sink);

        logger.LogInformation("player {Name} joined map {Map}", "ann \"the\" bold", 3);

        using JsonDocument doc = ParseSingleLine(sink.ToString());
        Assert.Equal("player ann \"the\" bold joined map 3", doc.RootElement.GetProperty("msg").GetString());
    }

    private static Exception ThrowAndCatchNested()
    {
        try
        {
            try
            {
                throw new InvalidOperationException("inner\nwith newline");
            }
            catch (Exception inner)
            {
                throw new ApplicationException("outer \"quoted\"", inner);
            }
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>A minimal logger that hands every call to the formatter, as the console logger does.</summary>
    private sealed class FormatterDrivenLogger(JsonLinesConsoleFormatter formatter, TextWriter sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> messageFormatter)
        {
            var entry = new LogEntry<TState>(logLevel, "driven", eventId, state, exception, messageFormatter);
            formatter.Write(in entry, null, sink);
        }
    }
}
