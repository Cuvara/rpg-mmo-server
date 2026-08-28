using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GameServer.Tests.Infrastructure;

/// <summary>
/// An <see cref="ILogger"/> that keeps what it was told, so a test asserting on a value that
/// swallowed its own failure can say why the value is what it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this exists for.</b> Several components here are deliberately built to
/// never throw — <see cref="GameServer.Agones.HttpAgonesSdk"/> is the clearest case: a
/// sidecar fault must not be able to take the game server down, so transport failure,
/// timeout, non-2xx, unparsable body and a well-formed body missing a field all return the
/// same bare <c>null</c>. That is right for production and blind for a test. When such a test
/// fails it prints <c>Assert.NotNull() Failure: Value is null</c> and nothing else, and the
/// five causes are indistinguishable — issue #216 needed eight TRX-logged full-suite runs to
/// get as far as knowing <i>which test</i>, and could still only reason about why.
/// </para>
/// <para>
/// The information was never missing. The SDK logs a distinct warning on every one of those
/// paths, and the suite throws it away by passing <c>NullLogger.Instance</c>. Passing this
/// instead costs nothing and turns the next occurrence into a message that names itself.
/// </para>
/// <para>
/// <b>Thread-safe by construction.</b> The subject may log from a background loop rather than
/// from the caller's thread, so entries land in a <see cref="ConcurrentQueue{T}"/> and
/// <see cref="Entries"/> hands back a snapshot.
/// </para>
/// </remarks>
public sealed class CapturingLogger : ILogger
{
    private readonly ConcurrentQueue<Entry> _entries = new();

    /// <summary>One captured line: the level, the formatted message and the exception, if any.</summary>
    /// <param name="Level">Severity it was logged at.</param>
    /// <param name="Message">The message after formatting, arguments substituted.</param>
    /// <param name="ExceptionType">
    /// Type name of the accompanying exception, or null. This is the field that separates a
    /// timeout from an absent listener from an unreadable body: all three reach the same
    /// catch and log the same text, and only the exception type tells them apart.
    /// </param>
    /// <param name="ExceptionMessage">The exception's message, or null.</param>
    public readonly record struct Entry(
        LogLevel Level, string Message, string? ExceptionType, string? ExceptionMessage);

    /// <summary>Everything captured so far, oldest first.</summary>
    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    /// <summary>Whether any captured message contains <paramref name="fragment"/>, ordinally.</summary>
    public bool Logged(string fragment) =>
        _entries.Any(e => e.Message.Contains(fragment, StringComparison.Ordinal));

    /// <summary>
    /// Whether any captured entry carries an exception of type <paramref name="typeName"/> —
    /// <c>TaskCanceledException</c> for a timeout, <c>HttpRequestException</c> for nothing
    /// listening, <c>JsonException</c> for a body that could not be read.
    /// </summary>
    public bool Threw(string typeName) =>
        _entries.Any(e => string.Equals(e.ExceptionType, typeName, StringComparison.Ordinal));

    /// <summary>
    /// <paramref name="claim"/> followed by every captured line, for use as an assertion
    /// message. Says so explicitly when nothing was logged, because "the subject logged
    /// nothing" is itself a finding — it means the failure happened before the subject ran.
    /// </summary>
    public string Explain(string claim)
    {
        var sb = new StringBuilder(claim);
        var entries = Entries;

        if (entries.Count == 0)
        {
            sb.Append(" The subject logged nothing at all, so it did not reach any of its own "
                    + "failure paths — look before it, not inside it.");
            return sb.ToString();
        }

        sb.Append(" What the subject logged, in order:");
        foreach (var e in entries)
        {
            sb.Append("\n  [").Append(e.Level).Append("] ").Append(e.Message);
            if (e.ExceptionType != null)
            {
                sb.Append("\n      ").Append(e.ExceptionType).Append(": ").Append(e.ExceptionMessage);
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>Always true: a test that discards a level is back to guessing.</summary>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new Entry(
            logLevel,
            formatter(state, exception),
            exception?.GetType().Name,
            exception?.Message));
}
