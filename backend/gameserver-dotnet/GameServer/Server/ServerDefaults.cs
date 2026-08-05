namespace GameServer.Server;

/// <summary>
/// Server-side default values for player entities.
/// These are not in Shared.GameLogic because they are server configuration,
/// not shared game logic.
/// </summary>
public static class ServerDefaults
{
    public const int DefaultPlayerHp = 100;

    /// <summary>
    /// Default player movement speed in <b>world units per second</b>.
    /// EntityState.Speed changed meaning with the tick-integrated movement model:
    /// it is no longer a per-tick displacement multiplier. 5 u/s crosses the default
    /// 1000x1000 map in ~200s and matches the old per-tick move cap of 5 units.
    /// </summary>
    public const float DefaultPlayerSpeed = 5.0f;
    public const int DefaultPlayerAttack = 10;
    public const int DefaultPlayerDefense = 5;
}
