using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace GameServer.Observability.Logging;

/// <summary>
/// Console formatter that writes every log event as exactly one JSON object followed by
/// <c>\n</c>, for a log shipper that reads stdout line by line.
/// </summary>
/// <remarks>
/// <para><b>Line shape.</b>
/// <c>{"ts","level","category","msg",["exception"],"game","map_id","server_id"}</c>, in that
/// order. <c>ts</c> is UTC RFC 3339 with milliseconds and a <c>Z</c> suffix
/// (<c>2026-01-02T03:04:05.678Z</c>). <c>level</c> is <c>TRACE</c>, <c>DEBUG</c>, <c>INFO</c>,
/// <c>WARN</c>, <c>ERROR</c> or <c>CRITICAL</c>, so the four levels the Go gateway's
/// <c>log/slog</c> JSON handler also emits (<c>DEBUG</c>, <c>INFO</c>, <c>WARN</c>,
/// <c>ERROR</c>) are spelled identically. <c>msg</c> is the formatted message and
/// <c>exception</c> is <see cref="Exception.ToString"/>, present only when there is one. A
/// message or exception that spans several lines is JSON-escaped and stays on one line.</para>
///
/// <para><b>Identity.</b> <c>game</c>, <c>map_id</c> and <c>server_id</c> are fixed for the life
/// of the process, so they are encoded once at construction and copied into every line: no
/// logger scope and no per-call lookup. Structured state properties and scopes are not
/// emitted.</para>
///
/// <para><b>AOT.</b> Written with <see cref="Utf8JsonWriter"/> only: no
/// <c>JsonSerializer</c>, no reflection, so it is trim- and NativeAOT-clean and does not trip
/// <c>JsonReflectionGuardTests</c>.</para>
///
/// <para><b>Cost.</b> Each thread keeps one buffer and one writer, reused for every line, so a
/// call allocates nothing in the formatter itself once the thread has logged. The finished line
/// goes to the <see cref="TextWriter"/> in a single <c>Write</c> call. Safe for concurrent use:
/// the only mutable state is thread-local.</para>
/// </remarks>
internal sealed class JsonLinesConsoleFormatter : ConsoleFormatter
{
    /// <summary>The name <c>ConsoleLoggerOptions.FormatterName</c> selects this formatter by.</summary>
    public const string FormatterName = "json-lines";

    /// <summary>A thread's buffer is dropped, not kept, once a line has grown it past this.</summary>
    private const int MaxRetainedBufferBytes = 64 * 1024;

    private const int InitialBufferBytes = 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Relaxed: keeps ' < > & + and non-ASCII readable. Control characters, quotes,
        // backslashes and the Unicode line/paragraph separators are still escaped, so a line
        // is always a single line. The output is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        // The structure written here is fixed, so the writer's state validation buys nothing.
        SkipValidation = true,
    };

    private static readonly JsonEncodedText TsName = JsonEncodedText.Encode("ts");
    private static readonly JsonEncodedText LevelName = JsonEncodedText.Encode("level");
    private static readonly JsonEncodedText CategoryName = JsonEncodedText.Encode("category");
    private static readonly JsonEncodedText MsgName = JsonEncodedText.Encode("msg");
    private static readonly JsonEncodedText ExceptionName = JsonEncodedText.Encode("exception");
    private static readonly JsonEncodedText GameName = JsonEncodedText.Encode("game");
    private static readonly JsonEncodedText MapIdName = JsonEncodedText.Encode("map_id");
    private static readonly JsonEncodedText ServerIdName = JsonEncodedText.Encode("server_id");

    private static readonly JsonEncodedText Trace = JsonEncodedText.Encode("TRACE");
    private static readonly JsonEncodedText Debug = JsonEncodedText.Encode("DEBUG");
    private static readonly JsonEncodedText Info = JsonEncodedText.Encode("INFO");
    private static readonly JsonEncodedText Warn = JsonEncodedText.Encode("WARN");
    private static readonly JsonEncodedText Error = JsonEncodedText.Encode("ERROR");
    private static readonly JsonEncodedText Critical = JsonEncodedText.Encode("CRITICAL");
    private static readonly JsonEncodedText None = JsonEncodedText.Encode("NONE");

    /// <summary>The <c>ts</c> format: fixed width, so one stack buffer always fits it.</summary>
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Length of <see cref="TimestampFormat"/> output, e.g. <c>2026-01-02T03:04:05.678Z</c>.</summary>
    private const int TimestampLength = 24;

    /// <summary>The line written when even the sanitised event cannot be encoded. Never throws.</summary>
    private const string EncodeFailureLine =
        "{\"level\":\"ERROR\",\"category\":\"JsonLinesConsoleFormatter\",\"msg\":\"log line could not be encoded\"}\n";

    private readonly JsonEncodedText _game;
    private readonly JsonEncodedText _mapId;
    private readonly JsonEncodedText _serverId;

    /// <summary>Per-thread reusable output state. Null until the thread first logs.</summary>
    [ThreadStatic]
    private static ThreadState? t_state;

    /// <summary>
    /// Creates a formatter that stamps every line with the given process identity.
    /// </summary>
    /// <param name="game">The game id this server serves (<c>game</c> field).</param>
    /// <param name="mapId">The map this server owns (<c>map_id</c> field).</param>
    /// <param name="serverId">The server id (<c>server_id</c> field).</param>
    public JsonLinesConsoleFormatter(string game, string mapId, string serverId)
        : base(FormatterName)
    {
        _game = EncodeIdentity(game);
        _mapId = EncodeIdentity(mapId);
        _serverId = EncodeIdentity(serverId);
    }

    /// <inheritdoc />
    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        string? message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        // Same rule as the console logger's own formatters: nothing to say, say nothing.
        if (logEntry.Exception == null && message == null)
            return;

        string? exception = ExceptionText(logEntry.Exception);
        DateTime now = DateTime.UtcNow;

        ThreadState state = t_state ??= new ThreadState();
        bool ok = TryEncode(state, now, logEntry.LogLevel, logEntry.Category, message, exception);
        if (!ok)
        {
            // Lone UTF-16 surrogates (a truncated string, a mangled user name) cannot be encoded
            // as JSON text. Replace them rather than lose the event or throw out of a log call.
            ok = TryEncode(
                state, now, logEntry.LogLevel, Sanitize(logEntry.Category), Sanitize(message), Sanitize(exception));
        }

        if (!ok)
        {
            textWriter.Write(EncodeFailureLine);
            return;
        }

        int byteCount = state.Buffer.WrittenCount;
        char[] chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(byteCount));
        try
        {
            int charCount = Encoding.UTF8.GetChars(state.Buffer.WrittenSpan, chars);
            // One Write call: the console logger's writer is a StringWriter, so the line is
            // appended whole and can never interleave with another thread's line.
            textWriter.Write(new ReadOnlySpan<char>(chars, 0, charCount));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }

        if (state.Buffer.Capacity > MaxRetainedBufferBytes)
            t_state = null; // one giant exception must not pin its buffer for the life of the thread
    }

    private bool TryEncode(
        ThreadState state, DateTime utcNow, LogLevel level, string? category, string? message, string? exception)
    {
        state.Buffer.ResetWrittenCount();
        state.Writer.Reset(state.Buffer);
        try
        {
            Utf8JsonWriter w = state.Writer;
            w.WriteStartObject();

            Span<byte> ts = stackalloc byte[TimestampLength];
            utcNow.TryFormat(ts, out int tsLength, TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
            w.WriteString(TsName, ts[..tsLength]);

            w.WriteString(LevelName, LevelText(level));
            w.WriteString(CategoryName, category ?? "");
            // An exception logged with no message still gets a (empty) msg, so the field is
            // always a string for a shipper's mapping.
            w.WriteString(MsgName, message ?? "");
            if (exception != null)
                w.WriteString(ExceptionName, exception);
            w.WriteString(GameName, _game);
            w.WriteString(MapIdName, _mapId);
            w.WriteString(ServerIdName, _serverId);

            w.WriteEndObject();
            w.Flush();

            Span<byte> newline = state.Buffer.GetSpan(1);
            newline[0] = (byte)'\n';
            state.Buffer.Advance(1);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static JsonEncodedText LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => Trace,
        LogLevel.Debug => Debug,
        LogLevel.Information => Info,
        LogLevel.Warning => Warn,
        LogLevel.Error => Error,
        LogLevel.Critical => Critical,
        _ => None,
    };

    private static string? ExceptionText(Exception? exception)
    {
        if (exception == null)
            return null;
        try
        {
            return exception.ToString();
        }
        catch (Exception)
        {
            // A hostile or buggy ToString override must not take the log call down with it.
            return exception.GetType().FullName + " (ToString failed)";
        }
    }

    private static JsonEncodedText EncodeIdentity(string value)
    {
        return JsonEncodedText.Encode(Sanitize(value) ?? "", JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
    }

    /// <summary>
    /// Replaces each unpaired UTF-16 surrogate with U+FFFD. Returns the input unchanged (same
    /// instance, no allocation) when it has none.
    /// </summary>
    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        StringBuilder? sb = null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool bad;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb?.Append(c).Append(value[i + 1]);
                    i++;
                    continue;
                }
                bad = true;
            }
            else
            {
                bad = char.IsLowSurrogate(c);
            }

            if (bad)
            {
                sb ??= new StringBuilder(value.Length).Append(value, 0, i);
                sb.Append('�');
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? value;
    }

    /// <summary>One thread's reusable output buffer and the writer bound to it.</summary>
    private sealed class ThreadState
    {
        public readonly ArrayBufferWriter<byte> Buffer = new(InitialBufferBytes);
        public readonly Utf8JsonWriter Writer;

        public ThreadState()
        {
            Writer = new Utf8JsonWriter(Buffer, WriterOptions);
        }
    }
}
