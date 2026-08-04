namespace GameServer.Server;

/// <summary>
/// Server-side default values for player entities.
/// These are not in Shared.GameLogic because they are server configuration,
/// not shared game logic.
/// </summary>
public static class ServerDefaults
{
    public const int DefaultPlayerHp = 100;
    public const float DefaultPlayerSpeed = 1.0f;
    public const int DefaultPlayerAttack = 10;
    public const int DefaultPlayerDefense = 5;
}
