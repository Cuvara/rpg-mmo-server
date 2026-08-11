using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using GameServer.Observability;
using GameServer.World;

namespace GameServer.Persistence;

/// <summary>Persistent player state record.</summary>
public record PlayerState(string UserId, float X, float Y, int Hp, int MaxHp, string MapId);

/// <summary>Abstraction for player data persistence.</summary>
public interface IPlayerStore
{
    Task SavePlayerAsync(PlayerState state, CancellationToken ct);
    Task<PlayerState?> LoadPlayerAsync(string userId, CancellationToken ct);
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

    public AsyncSaver(
        IPlayerStore store,
        EcsWorld world,
        string mapId,
        TimeSpan interval,
        ILogger logger,
        GameMetrics? metrics = null)
    {
        _store = store;
        _world = world;
        _mapId = mapId;
        _interval = interval;
        _logger = logger;
        _metrics = metrics;
    }

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
            var state = new PlayerState(p.Id, p.Position.X, p.Position.Y, p.Hp, p.MaxHp, _mapId);
            await _store.SavePlayerAsync(state, CancellationToken.None);
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
                var state = new PlayerState(p.Id, p.Position.X, p.Position.Y, p.Hp, p.MaxHp, _mapId);
                await _store.SavePlayerAsync(state, CancellationToken.None);
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
