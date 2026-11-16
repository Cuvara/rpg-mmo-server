using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace GameServer.Observability.Logging;

/// <summary>
/// Builds the process's <see cref="ILoggerFactory"/> from validated <see cref="LogSettings"/>.
/// Used by <c>Program.cs</c> and by the <c>GameServerHost</c> fallback, so both log the same way.
/// </summary>
internal static class ServerLogging
{
    /// <summary>
    /// Creates a console logger factory writing to stdout.
    /// </summary>
    /// <remarks>
    /// <para><see cref="LogFormat.Json"/>: one JSON object per line from
    /// <see cref="JsonLinesConsoleFormatter"/>, stamped with <paramref name="game"/>,
    /// <paramref name="mapId"/> and <paramref name="serverId"/>. The formatter instance is
    /// registered as a <see cref="ConsoleFormatter"/> singleton and selected by
    /// <see cref="ConsoleLoggerOptions.FormatterName"/>, which avoids
    /// <c>AddConsoleFormatter&lt;TFormatter, TOptions&gt;</c> and the option-binding it does
    /// through reflection (trim/AOT-unsafe). <see cref="LogFormat.Text"/>: plain
    /// <c>AddConsole()</c>, the console logger's default output, unchanged.</para>
    /// <para>The minimum level is applied by the factory's own filter, so a call below it is
    /// rejected by <c>IsEnabled</c> before any message is built, as it was before.</para>
    /// </remarks>
    /// <param name="settings">Validated minimum level and format.</param>
    /// <param name="game">Game id for the <c>game</c> field (json format only).</param>
    /// <param name="mapId">Map id for the <c>map_id</c> field (json format only).</param>
    /// <param name="serverId">Server id for the <c>server_id</c> field (json format only).</param>
    /// <returns>A factory the caller owns and must dispose.</returns>
    public static ILoggerFactory CreateFactory(LogSettings settings, string game, string mapId, string serverId)
    {
        return LoggerFactory.Create(builder =>
        {
            if (settings.Format == LogFormat.Json)
            {
                builder.Services.AddSingleton<ConsoleFormatter>(new JsonLinesConsoleFormatter(game, mapId, serverId));
                builder.AddConsole(options => options.FormatterName = JsonLinesConsoleFormatter.FormatterName);
            }
            else
            {
                builder.AddConsole();
            }

            builder.SetMinimumLevel(settings.Level);
        });
    }
}
