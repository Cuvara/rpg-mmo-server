namespace GameServer.Games;

/// <summary>Resolves and validates the game identity for a server process (ADR-33).</summary>
public static class ServerGame
{
    /// <summary>
    /// Defaults an absent game to cuvara and requires other games to use a server id
    /// prefixed by their game id, preserving existing default-game server ids.
    /// </summary>
    /// <param name="configured">The game configured by --game-id or GAMESERVER_GAME_ID.</param>
    /// <param name="serverId">The server id after flag, environment and generated-id resolution.</param>
    /// <param name="gameId">The configured game id, or the default game when absent.</param>
    /// <param name="error">A startup refusal explaining invalid configuration, or null on success.</param>
    /// <returns>Whether the game and server identity can be used together.</returns>
    public static bool TryResolve(string? configured, string serverId, out string gameId, out string? error)
    {
        gameId = GameId.GameOrDefault(configured);
        error = null;

        if (!GameId.IsValidGameId(gameId))
        {
            error = $"Invalid GAMESERVER_GAME_ID value '{configured}': must match ^[a-z][a-z0-9]{{1,23}}$ " +
                    "and must not be gs, map or dungeon -- refusing to start.";
            return false;
        }

        if (!GameId.IsDefaultGame(gameId) && !GameId.IsValidServerId(gameId, serverId))
        {
            error = $"Server id '{serverId}' for GAMESERVER_GAME_ID '{gameId}' must start with '{gameId}-' " +
                    "and have a non-empty suffix -- refusing to start. Server ids resolve from --server-id, " +
                    "GAMESERVER_ID, POD_NAME, then a generated gs-<hex> id; " +
                    $"fleets must be named '{gameId}-...'.";
            return false;
        }

        return true;
    }
}
