using Shared.GameLogic.Components;

namespace GameServer.World;

/// <summary>Pending input queued for processing in the next tick.</summary>
public readonly struct PendingInput
{
    public readonly string UserId;
    public readonly InputData Input;

    public PendingInput(string userId, InputData input)
    {
        UserId = userId;
        Input = input;
    }
}

/// <summary>
/// Thread-safe entity container for the game world.
/// Uses ReaderWriterLockSlim (same pattern as Go's RWMutex).
/// </summary>
public sealed class GameWorld : IDisposable
{
    private readonly Dictionary<string, EntityState> _entities = new();
    private readonly List<PendingInput> _pendingInputs = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly object _inputLock = new();

    /// <summary>Current entity count.</summary>
    public int EntityCount
    {
        get
        {
            _rwLock.EnterReadLock();
            try { return _entities.Count; }
            finally { _rwLock.ExitReadLock(); }
        }
    }

    /// <summary>Add or replace an entity in the world.</summary>
    public void AddEntity(EntityState entity)
    {
        _rwLock.EnterWriteLock();
        try { _entities[entity.Id] = entity; }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>Remove an entity by ID.</summary>
    public void RemoveEntity(string id)
    {
        _rwLock.EnterWriteLock();
        try { _entities.Remove(id); }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>Get an entity by ID. Returns null if not found.</summary>
    public EntityState? GetEntity(string id)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _entities.TryGetValue(id, out var e) ? e : null;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>Get all entities within radius of center position.</summary>
    public List<EntityState> GetEntitiesInRange(Vec2 center, float radius)
    {
        var result = new List<EntityState>();
        float r2 = radius * radius;
        _rwLock.EnterReadLock();
        try
        {
            foreach (var e in _entities.Values)
            {
                float dx = e.Position.X - center.X;
                float dy = e.Position.Y - center.Y;
                if (dx * dx + dy * dy <= r2)
                    result.Add(e);
            }
        }
        finally { _rwLock.ExitReadLock(); }
        return result;
    }

    /// <summary>
    /// Take a write lock and call the action with a getter and setter.
    /// The setter writes the (possibly mutated) struct back to the dictionary.
    /// </summary>
    public void Update(Action<Func<string, EntityState?>, Action<string, EntityState>> action)
    {
        _rwLock.EnterWriteLock();
        try
        {
            EntityState? Getter(string id) =>
                _entities.TryGetValue(id, out var e) ? e : null;

            void Setter(string id, EntityState state) =>
                _entities[id] = state;

            action(Getter, Setter);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Take a read lock and call the action with a getter.
    /// </summary>
    public void View(Action<Func<string, EntityState?>> action)
    {
        _rwLock.EnterReadLock();
        try
        {
            EntityState? Getter(string id) =>
                _entities.TryGetValue(id, out var e) ? e : null;

            action(Getter);
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>Queue an input for processing in the next tick.</summary>
    public void PushInput(string userId, InputData input)
    {
        lock (_inputLock)
        {
            _pendingInputs.Add(new PendingInput(userId, input));
        }
    }

    /// <summary>Drain all pending inputs (returns the list and clears the queue).</summary>
    public List<PendingInput> DrainInputs()
    {
        lock (_inputLock)
        {
            var copy = new List<PendingInput>(_pendingInputs);
            _pendingInputs.Clear();
            return copy;
        }
    }

    /// <summary>Get a snapshot of all player-type entities.</summary>
    public List<EntityState> PlayerStates()
    {
        var result = new List<EntityState>();
        _rwLock.EnterReadLock();
        try
        {
            foreach (var e in _entities.Values)
            {
                if (e.Type == "player")
                    result.Add(e);
            }
        }
        finally { _rwLock.ExitReadLock(); }
        return result;
    }

    public void Dispose()
    {
        _rwLock.Dispose();
    }
}
