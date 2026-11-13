namespace GameServer.Registry;

/// <summary>Registration refused because the live server id belongs to another game.</summary>
public sealed class RegistryGameMismatchException : InvalidOperationException
{
    public string ServerId { get; }
    public string GameId { get; }

    public RegistryGameMismatchException(string serverId, string gameId)
        : base($"Cannot register server '{serverId}' for game '{gameId}': the live registry entry belongs to another game.")
    {
        ServerId = serverId;
        GameId = global::GameServer.Games.GameId.GameOrDefault(gameId);
    }
}
