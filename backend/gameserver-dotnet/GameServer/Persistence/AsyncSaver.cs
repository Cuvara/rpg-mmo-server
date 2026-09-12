using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Observability;
using GameServer.World;

namespace GameServer.Persistence;

/// <summary>Persistent player state record.</summary>
public record PlayerState(string UserId, float X, float Y, int Hp, int MaxHp, string MapId);

/// <summary>
/// What a saving server writes out of a player.
///
/// <para><c>player_states</c> holds exactly ONE row per player, with a single
/// <c>map_id</c>, and <see cref="PlayerSpawn.Resolve"/> discards saved coordinates whose
/// row belongs to another map. So "which fields may I write" is a property of the server
/// doing the writing, not of the row: a dungeon instance that wrote the row in full would
/// stamp its own id over the player's origin map and send them back to that map's spawn
/// point instead of where they left (ADR-26 decision 5).</para>
/// </summary>
public enum PlayerSaveScope
{
    /// <summary>
    /// Position, HP and the writing server's map id. What a map server writes, and the
    /// default everywhere.
    /// </summary>
    Full,

    /// <summary>
    /// The map-independent fields only — HP and max HP. <c>map_id</c>, <c>x</c> and
    /// <c>y</c> are left exactly as the origin map wrote them. What a dungeon instance
    /// writes; the stated cost is that position inside a dungeon is not durable.
    /// </summary>
    StatsOnly,
}

/// <summary>Abstraction for player data persistence.</summary>
public interface IPlayerStore
{
    /// <summary>Write the whole row: map id, position and stats.</summary>
    Task SavePlayerAsync(PlayerState state, CancellationToken ct);

    /// <summary>Read a player's row, or null when they have none.</summary>
    Task<PlayerState?> LoadPlayerAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Write only the map-independent fields, leaving <c>map_id</c>, <c>x</c> and
    /// <c>y</c> as they already stand. A player with no row yet gets one whose map id is
    /// empty — <see cref="PlayerSpawn.SameMap"/> reads an empty id as "unattributable",
    /// so the next join spawns them at that map's spawn point with the HP saved here,
    /// which is the same outcome as no row at all except that the HP survives.
    /// </summary>
    /// <remarks>
    /// The default implementation is a read-modify-write and is therefore NOT atomic: two
    /// servers saving the same player concurrently can lose one update. That cannot happen
    /// in this system — a player is live on exactly one server at a time — but a store
    /// that can express the merge in one statement should override this, and
    /// <see cref="PostgresPlayerStore"/> does.
    /// </remarks>
    /// <param name="userId">Player whose stats to write.</param>
    /// <param name="hp">Current HP.</param>
    /// <param name="maxHp">Maximum HP.</param>
    /// <param name="ct">Cancellation token.</param>
    async Task SavePlayerStatsAsync(string userId, int hp, int maxHp, CancellationToken ct)
    {
        var existing = await LoadPlayerAsync(userId, ct);
        var merged = existing is null
            ? new PlayerState(userId, 0, 0, hp, maxHp, string.Empty)
            : existing with { Hp = hp, MaxHp = maxHp };
        await SavePlayerAsync(merged, ct);
    }
}

/// <summary>In-memory player store for development and testing.</summary>
public sealed class MemoryPlayerStore : IPlayerStore
{
    private readonly ConcurrentDictionary<string, PlayerState> _store = new();

    public Task SavePlayerAsync(PlayerState state, CancellationToken ct)
    {
        _store[state.UserId] = state;
        return Task.CompletedTask;
    }

    public Task<PlayerState?> LoadPlayerAsync(string userId, CancellationToken ct)
    {
        _store.TryGetValue(userId, out var state);
        return Task.FromResult(state);
    }

    /// <inheritdoc />
    public Task SavePlayerStatsAsync(string userId, int hp, int maxHp, CancellationToken ct)
    {
        // AddOrUpdate rather than the interface's load-then-save: the dictionary can do
        // the merge atomically, so a concurrent full save cannot be lost between them.
        _store.AddOrUpdate(
            userId,
            static (id, args) => new PlayerState(id, 0, 0, args.hp, args.maxHp, string.Empty),
            static (_, existing, args) => existing with { Hp = args.hp, MaxHp = args.maxHp },
            (hp, maxHp));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Periodic background saver that snapshots all player entities to the store.
/// Port of Go persistence/saver.go.
/// </summary>
public sealed class AsyncSaver
{
    private readonly IPlayerStore _store;
    private readonly EcsWorld _world;
    private readonly string _mapId;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly GameMetrics? _metrics;
    private readonly PlayerSaveScope _scope;

    /// <summary>Build a saver over <paramref name="world"/>.</summary>
    /// <param name="store">Where player rows are written.</param>
    /// <param name="world">World whose player entities are snapshotted.</param>
    /// <param name="mapId">Map id stamped on every full save.</param>
    /// <param name="interval">Delay between sweeps.</param>
    /// <param name="logger">Destination for per-sweep diagnostics.</param>
    /// <param name="metrics">Optional counters; null runs uninstrumented.</param>
    /// <param name="scope">
    /// Which fields of a player are written. <see cref="PlayerSaveScope.Full"/> is the
    /// default and what every map server uses; a dungeon instance passes
    /// <see cref="PlayerSaveScope.StatsOnly"/> so it cannot overwrite the origin map's
    /// id and coordinates (ADR-26 decision 5).
    /// </param>
    public AsyncSaver(
        IPlayerStore store,
        EcsWorld world,
        string mapId,
        TimeSpan interval,
        ILogger logger,
        GameMetrics? metrics = null,
        PlayerSaveScope scope = PlayerSaveScope.Full)
    {
        _store = store;
        _world = world;
        _mapId = mapId;
        _interval = interval;
        _logger = logger;
        _metrics = metrics;
        _scope = scope;
    }

    /// <summary>Which fields this saver writes. Diagnostics and tests.</summary>
    public PlayerSaveScope Scope => _scope;

    /// <summary>
    /// Persist one player entity under the configured <see cref="Scope"/>.
    /// </summary>
    private Task PersistAsync(EntityState p) => _scope == PlayerSaveScope.StatsOnly
        ? _store.SavePlayerStatsAsync(p.Id, p.Hp, p.MaxHp, CancellationToken.None)
        : _store.SavePlayerAsync(
            new PlayerState(p.Id, p.Position.X, p.Position.Y, p.Hp, p.MaxHp, _mapId),
            CancellationToken.None);

    /// <summary>Run the periodic save loop until cancellation.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("AsyncSaver started (interval: {Interval})", _interval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException) { break; }

            await SaveAllAsync();
        }

        // Final save on shutdown
        await SaveAllAsync();
        _logger.LogInformation("AsyncSaver stopped");
    }

    /// <summary>
    /// Save a single player entity by id. Used when an entity is about to leave
    /// the world (reconnect hold expiry): once it is removed, the periodic
    /// <see cref="SaveAllAsync"/> sweep can no longer see it, so anything the
    /// player did since the last sweep would be lost.
    /// </summary>
    /// <returns>True when the entity existed and was persisted.</returns>
    public async Task<bool> SavePlayerAsync(string userId)
    {
        var entity = _world.GetEntity(userId);
        if (entity == null || entity.Value.Type != "player") return false;

        var p = entity.Value;
        try
        {
            await PersistAsync(p);
            _metrics?.RecordPlayerSaveOk();
            return true;
        }
        catch (Exception ex)
        {
            _metrics?.RecordPlayerSaveError();
            _logger.LogWarning(ex, "Failed to save player {UserId} on removal", p.Id);
            return false;
        }
    }

    /// <summary>Save all current player entities to the store.</summary>
    public async Task SaveAllAsync()
    {
        var players = _world.PlayerStates();
        if (players.Count == 0) return;

        int saved = 0;
        foreach (var p in players)
        {
            try
            {
                await PersistAsync(p);
                saved++;
                _metrics?.RecordPlayerSaveOk();
            }
            catch (Exception ex)
            {
                _metrics?.RecordPlayerSaveError();
                _logger.LogWarning(ex, "Failed to save player {UserId}", p.Id);
            }
        }

        _logger.LogDebug("Saved {Count} players", saved);
    }
}
