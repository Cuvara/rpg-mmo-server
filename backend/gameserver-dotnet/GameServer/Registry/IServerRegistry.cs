namespace GameServer.Registry;

/// <summary>
/// The server-registry operations a game server needs to publish and maintain
/// its own liveness. This is the write side of the registry; the gateway owns
/// the read side (<c>FindByMapID</c>) and is not modelled here.
/// </summary>
public interface IServerRegistry : IAsyncDisposable
{
    /// <summary>
    /// Write the server hash, arm its heartbeat TTL, and index it under its map.
    /// Registering an already-registered server overwrites it — this is the
    /// re-registration path after a Redis outage, so it must be idempotent.
    /// </summary>
    Task RegisterAsync(ServerInfo info, CancellationToken ct);

    /// <summary>
    /// Re-arm the TTL of a live entry.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the entry no longer exists — it expired, or Redis lost
    /// it. The caller must treat that as "re-register me", which is what makes
    /// the server self-healing after a Redis wipe.
    /// </returns>
    Task<bool> HeartbeatAsync(string serverId, CancellationToken ct);

    /// <summary>Remove the hash and its map-index entry. Used on graceful shutdown.</summary>
    Task DeregisterAsync(string serverId, string mapId, CancellationToken ct);

    /// <summary>
    /// Set the live player count.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the entry is gone, so a stale writer cannot resurrect an
    /// expired (and therefore TTL-less) entry. Mirrors the Lua guard in the Go
    /// implementation.
    /// </returns>
    Task<bool> UpdatePlayerCountAsync(string serverId, int count, CancellationToken ct);
}
