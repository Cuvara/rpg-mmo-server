using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Games;

namespace GameServer.Content;

/// <summary>Checks that a content tree belongs to this game and a compatible package major.</summary>
public static class GameManifest
{
    public const string FileName = "game.json";

    /// <summary>Shared.GameLogic package version compiled into this binary; package.json is not compiled in.</summary>
    public const string CompiledSharedGameLogicVersion = "0.7.0";

    public static GameManifestResult Check(string contentDir, string serverGameId)
    {
        string path = Path.Combine(contentDir, FileName);
        ManifestData? manifest;
        try
        {
            string json = File.ReadAllText(path);
            manifest = JsonSerializer.Deserialize(json, ManifestJsonContext.Default.ManifestData);
        }
        catch (FileNotFoundException)
        {
            if (GameId.IsDefaultGame(serverGameId))
                return new(null, $"'{path}' is absent; content is treated as cuvara so an old content tree still boots. Add game.json to declare its game and version.", null, null);
            return new($"'{path}' is absent; add game.json for server game '{GameId.GameOrDefault(serverGameId)}' with its Shared.GameLogic version.", null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new($"Cannot read or parse '{path}': {ex.Message} Fix the manifest and restart.", null, null, null);
        }

        if (manifest == null || !GameId.IsValidGameId(manifest.GameId))
            return new($"'{path}' has a missing or invalid game_id; set it to a valid game ID.", null, null, null);
        if (!TryMajor(manifest.SharedGameLogicVersion, out int major))
            return new($"'{path}' has a missing or invalid shared_gamelogic_version; set it to the authored Shared.GameLogic version.", null, null, null);

        string effectiveGame = GameId.GameOrDefault(serverGameId);
        if (!string.Equals(manifest.GameId, effectiveGame, StringComparison.Ordinal))
            return new($"'{path}' declares game '{manifest.GameId}' but the server serves '{effectiveGame}'; use content for the server game or change GAMESERVER_GAME_ID.", null, null, null);
        TryMajor(CompiledSharedGameLogicVersion, out int compiledMajor);
        if (major != compiledMajor)
            return new($"'{path}' declares Shared.GameLogic version '{manifest.SharedGameLogicVersion}' but the server compiles '{CompiledSharedGameLogicVersion}'; use content and a server with matching major versions.", null, null, null);

        return new(null, null, manifest.GameId, manifest.SharedGameLogicVersion);
    }

    private static bool TryMajor(string? version, out int major)
    {
        major = 0;
        if (string.IsNullOrEmpty(version)) return false;
        int end = 0;
        while (end < version.Length && version[end] >= '0' && version[end] <= '9') end++;
        return end > 0 && (end == version.Length || version[end] is '.' or '-' or '+') &&
            int.TryParse(version.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out major);
    }
}

public sealed record GameManifestResult(string? Refusal, string? Warning, string? GameId, string? Version);

internal sealed class ManifestData
{
    [JsonPropertyName("game_id")]
    public string? GameId { get; set; }

    [JsonPropertyName("shared_gamelogic_version")]
    public string? SharedGameLogicVersion { get; set; }
}

[JsonSerializable(typeof(ManifestData))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
