namespace GameServer.Games;

/// <summary>Wire identifiers shared by the gateway and game server (ADR-33).</summary>
public static class GameId
{
    public const string DefaultGameId = "cuvara";

    public static string GameOrDefault(string? id) => string.IsNullOrEmpty(id) ? DefaultGameId : id;

    public static bool IsDefaultGame(string? game) =>
        string.Equals(GameOrDefault(game), DefaultGameId, StringComparison.Ordinal);

    public static bool IsValidGameId(string? id)
    {
        if (id == null || id.Length < 2 || id.Length > 24 || !IsLower(id[0])) return false;
        for (int i = 1; i < id.Length; i++)
            if (!IsLower(id[i]) && !IsDigit(id[i])) return false;
        return true;
    }

    public static bool IsValidMapId(string? id)
    {
        if (id == null || id.Length < 1 || id.Length > 64) return false;
        foreach (char c in id)
            if (!IsLower(c) && !(c >= 'A' && c <= 'Z') && !IsDigit(c) && c != '_' && c != '-')
                return false;
        return true;
    }

    public static bool IsValidServerId(string? game, string? id)
    {
        if (!string.IsNullOrEmpty(game) && !IsValidGameId(game)) return false;
        if (string.IsNullOrEmpty(id)) return false;
        if (IsDefaultGame(game)) return true;
        string prefix = game + "-";
        return id.Length > prefix.Length && id.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool IsLower(char c) => c >= 'a' && c <= 'z';
    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
