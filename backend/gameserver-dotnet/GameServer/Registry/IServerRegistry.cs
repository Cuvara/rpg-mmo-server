namespace GameServer.Registry;

/// <summary>
/// How far a server publishes itself into the registry.
///
/// <para>The registry has two keys: the <c>servers:id:{server_id}</c> HASH, which is the
/// source of truth and carries the dialable address, and the <c>servers:map:{map_id}</c>
/// SET, which is the index <c>FindServer</c> searches. A map server writes both. A
/// dungeon instance writes only the hash (ADR-26 decision 8): the gateway allocates the
/// pod, learns its name, and then waits for exactly that hash to read the address from,
/// so the hash is mandatory — while an entry in the map index would let
/// <c>FindServer</c> hand one party's instance to an unrelated player, and on a dungeon
/// fleet (which pins no <c>GAMESERVER_MAP_ID</c>) the index key would be the empty
/// one.</para>
/// </summary>
public enum RegistrationScope
{
    /// <summary>Write the hash and add the server to its map index. Map servers.</summary>
    MapIndexed,

    /// <summary>
    /// Write the hash only, leaving the map index untouched. Dungeon instances, which are
    /// found through the gateway's per-party key and must never be found by map lookup.
    /// </summary>
    HashOnly,
}
/// <summary>
/// The server-registry operations a game server needs to publish and maintain
/// its own liveness. This is the write side of the registry; the gateway owns
/// the read side (<c>FindByMapID</c>) and is not modelled here.
/// </summary>
public interface IServerRegistry : IAsyncDisposable
{
    /// <summary>
    /// Write the server hash, arm its heartbeat TTL, and — when
    /// <paramref name="scope"/> is <see cref="RegistrationScope.MapIndexed"/> — index it
    /// under its map. Registering an already-registered server overwrites it — this is the
    /// re-registration path after a Redis outage, so it must be idempotent.
    /// </summary>
    /// <param name="info">How this server describes itself to the gateway.</param>
    /// <param name="scope">
    /// Whether the map index is written as well as the hash. The hash is written either
    /// way: it is what the gateway reads the dialable address from, for a dungeon pod as
    /// much as for a map server, and it is what carries the heartbeat TTL that makes a
    /// dead server disappear on its own.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task RegisterAsync(ServerInfo info, RegistrationScope scope, CancellationToken ct);

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
