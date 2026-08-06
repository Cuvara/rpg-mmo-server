namespace GameServer.Registry;

/// <summary>
/// A game server's registry entry, as the Go gateway reads it.
///
/// Field names here are the C# mirror of <c>shared/storage.ServerInfo</c>; the
/// Redis hash field names that carry them are defined in
/// <see cref="RedisServerRegistry"/> and MUST stay byte-identical to
/// <c>shared/storage/redisstore/registry.go</c>, because the gateway parses
/// them directly.
/// </summary>
/// <param name="ServerId">Registry id, unique per running server.</param>
/// <param name="MapId">Map this server owns. Also the index key.</param>
/// <param name="Addr">
/// The address handed to clients verbatim in <c>MsgEnterWorldResp.ServerAddr</c>.
/// It must be dialable BY THE CLIENT, which is not necessarily the address this
/// process listens on — see <see cref="RegistrationOptions.PublicAddr"/>.
/// </param>
/// <param name="Transport">Transport clients must use ("tcp" or "kcp").</param>
/// <param name="Capacity">Maximum concurrent players.</param>
/// <param name="PlayerCount">Current player count, refreshed on join/leave.</param>
public sealed record ServerInfo(
    string ServerId,
    string MapId,
    string Addr,
    string Transport,
    int Capacity,
    int PlayerCount);
