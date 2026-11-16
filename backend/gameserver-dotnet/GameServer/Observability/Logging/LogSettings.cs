using Microsoft.Extensions.Logging;

namespace GameServer.Observability.Logging;

/// <summary>The stdout shape of a server log line.</summary>
internal enum LogFormat
{
    /// <summary>One JSON object per line (the default), for a log shipper.</summary>
    Json,

    /// <summary>The Microsoft console logger's default output, for reading locally.</summary>
    Text,
}

/// <summary>
/// The validated logging configuration: a minimum level and an output format.
/// Built by <see cref="TryParse"/> from <c>GAMESERVER_LOG_LEVEL</c> / <c>GAMESERVER_LOG_FORMAT</c>
/// (or <c>--log-level</c> / <c>--log-format</c>).
/// </summary>
/// <param name="Level">Minimum level that reaches stdout. Default <see cref="LogLevel.Information"/>.</param>
/// <param name="Format">Output format. Default <see cref="LogFormat.Json"/>.</param>
internal readonly record struct LogSettings(LogLevel Level, LogFormat Format)
{
    /// <summary>The settings a server runs with when both variables are unset.</summary>
    public static LogSettings Default => new(LogLevel.Information, LogFormat.Json);

    /// <summary>
    /// Parses the two raw values. Unset or empty (after trimming) selects the default for that
    /// knob. Anything else must be exactly one of the accepted names, ASCII case-insensitive.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT use <c>Enum.TryParse</c>: it accepts integers (<c>"3"</c>,
    /// <c>"99"</c>) and <c>"None"</c>, and a typo that silently turned logging off, or picked an
    /// arbitrary numeric level, is exactly the misconfiguration the fail-fast rule exists for.
    /// </remarks>
    /// <param name="levelRaw">Raw <c>GAMESERVER_LOG_LEVEL</c> value, or null.</param>
    /// <param name="formatRaw">Raw <c>GAMESERVER_LOG_FORMAT</c> value, or null.</param>
    /// <param name="settings">The parsed settings; <see cref="Default"/> when the method returns false.</param>
    /// <param name="error">
    /// When refused, a message naming the variable, the value and the accepted values; otherwise null.
    /// </param>
    /// <returns>True when both values are usable.</returns>
    public static bool TryParse(string? levelRaw, string? formatRaw, out LogSettings settings, out string? error)
    {
        settings = Default;
        error = null;

        LogLevel level = LogLevel.Information;
        string? levelValue = levelRaw?.Trim();
        if (!string.IsNullOrEmpty(levelValue))
        {
            if (levelValue.Equals("Trace", StringComparison.OrdinalIgnoreCase)) level = LogLevel.Trace;
            else if (levelValue.Equals("Debug", StringComparison.OrdinalIgnoreCase)) level = LogLevel.Debug;
            else if (levelValue.Equals("Information", StringComparison.OrdinalIgnoreCase)) level = LogLevel.Information;
            else if (levelValue.Equals("Warning", StringComparison.OrdinalIgnoreCase)) level = LogLevel.Warning;
            else if (levelValue.Equals("Error", StringComparison.OrdinalIgnoreCase)) level = LogLevel.Error;
            else if (levelValue.Equals("Critical", StringComparison.OrdinalIgnoreCase)) level = LogLevel.Critical;
            else
            {
                error = $"invalid {ServerEnv.LogLevel}='{levelRaw}': expected one of " +
                        "Trace, Debug, Information, Warning, Error, Critical";
                return false;
            }
        }

        LogFormat format = LogFormat.Json;
        string? formatValue = formatRaw?.Trim();
        if (!string.IsNullOrEmpty(formatValue))
        {
            if (formatValue.Equals("json", StringComparison.OrdinalIgnoreCase)) format = LogFormat.Json;
            else if (formatValue.Equals("text", StringComparison.OrdinalIgnoreCase)) format = LogFormat.Text;
            else
            {
                error = $"invalid {ServerEnv.LogFormat}='{formatRaw}': expected one of json, text";
                return false;
            }
        }

        settings = new LogSettings(level, format);
        return true;
    }
}
