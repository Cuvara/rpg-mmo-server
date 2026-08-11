using System;
using System.Collections.Generic;
using System.Threading;
using Arch.Core;
using GameServer.World.Components;
using Shared.GameLogic.Components;
using ArchWorld = Arch.Core.World;

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
/// The server's entity store, backed by <see href="https://github.com/genaray/Arch">Arch</see>
/// (ADR-10). Replaces the hand-rolled <c>GameWorld</c> dictionary: Arch owns entity
/// identity, component storage, queries and iteration order. Nothing else stores
/// entity state.
///
/// <para><b>What this class owns on top of Arch.</b> Three things Arch does not
/// provide and the server needs:</para>
/// <list type="number">
/// <item><description>A <c>userId -&gt; Entity</c> index. <see cref="EntityState.Id"/>
/// is still a string (ADR-10's integer simulation handle is a separate migration),
/// and every caller looks entities up by that string.</description></item>
/// <item><description>A <see cref="ReaderWriterLockSlim"/>. Arch's world is not
/// thread-safe, and network threads spawn/despawn entities and push input while the
/// tick loop reads. This is the same lock discipline <c>GameWorld</c> had.</description></item>
/// <item><description>A deferred structural-change phase. ADR-11 forbids
/// <c>Arch.Buffer.CommandBuffer</c> — it throws under NativeAOT even with hints — so
/// spawns and despawns requested while a query is being iterated are queued and
/// applied by <see cref="ApplyStructuralChanges"/> instead.</description></item>
/// </list>
///
/// <para><b>Boundary.</b> No <c>Arch.Core</c> type crosses into
/// <c>Shared.GameLogic</c>. This class composes an <see cref="EntityState"/> out of
/// components on the way out and writes the components back on the way in; the shared
/// static functions never see the ECS.</para>
/// </summary>
public sealed class EcsWorld : IDisposable
{
    private readonly ArchWorld _arch = ArchWorld.Create();
    private readonly Dictionary<string, Entity> _index = new();
    private readonly List<PendingInput> _pendingInputs = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly object _inputLock = new();

    /// <summary>Queued structural changes, drained by <see cref="ApplyStructuralChanges"/>.</summary>
    private readonly List<StructuralOp> _structural = new();

    /// <summary>
    /// Non-zero while THIS thread is inside an Arch query iteration. Thread-static
    /// rather than a plain field so concurrent readers cannot race on it; the
    /// reader/writer lock already prevents a writer from overlapping a reader, so
    /// what remains is same-thread re-entrancy, which is exactly what this catches.
    /// </summary>
    [ThreadStatic]
    private static int _iterationDepth;

    private static readonly QueryDescription AllEntities = new QueryDescription()
        .WithAll<EntityIdRef, EntityKind, Position, Health, Combat, Locomotion, InputCursor>();

    private static readonly QueryDescription Players = new QueryDescription()
        .WithAll<EntityIdRef, EntityKind, Position, Health, Combat, Locomotion, InputCursor, PlayerTag>();

    /// <summary>Current entity count.</summary>
    public int EntityCount
    {
        get
        {
            _rwLock.EnterReadLock();
            try { return _index.Count; }
            finally { _rwLock.ExitReadLock(); }
        }
    }

    /// <summary>Add or replace an entity in the world.</summary>
    /// <remarks>
    /// Replace overwrites every component in place. It only touches the archetype when
    /// the entity's player-ness changed, because an archetype move is the expensive
    /// case and a re-spawn of the same user id is otherwise the common one (reconnect).
    /// </remarks>
    public void AddEntity(EntityState entity)
    {
        _rwLock.EnterWriteLock();
        try { AddEntityLocked(entity); }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>Remove an entity by ID. A missing ID is a no-op.</summary>
    public void RemoveEntity(string id)
    {
        _rwLock.EnterWriteLock();
        try { RemoveEntityLocked(id); }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>Get an entity by ID. Returns null if not found.</summary>
    public EntityState? GetEntity(string id)
    {
        _rwLock.EnterReadLock();
        try { return GetEntityLocked(id); }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>
    /// Get all entities within <paramref name="radius"/> of <paramref name="center"/>.
    /// Iterates Arch chunks directly and materialises an <see cref="EntityState"/> only
    /// for the entities that pass the distance test.
    /// </summary>
    public List<EntityState> GetEntitiesInRange(Vec2 center, float radius)
    {
        var result = new List<EntityState>();
        float radiusSq = radius * radius;

        _rwLock.EnterReadLock();
        _iterationDepth++;
        try
        {
            foreach (ref var chunk in _arch.Query(in AllEntities).GetChunkIterator())
            {
                var positions = chunk.GetSpan<Position>();
                int count = chunk.Count;
                for (int i = 0; i < count; i++)
                {
                    // Vec2.DistanceSq is Shared.GameLogic: the AOI predicate the
                    // client predicts with, not a second copy of it here.
                    if (Vec2.DistanceSq(center, positions[i].Value) <= radiusSq)
                    {
                        result.Add(ComposeFromChunk(ref chunk, i));
                    }
                }
            }
        }
        finally
        {
            _iterationDepth--;
            _rwLock.ExitReadLock();
        }

        return result;
    }

    /// <summary>
    /// Take a write lock and call the action with a getter and setter.
    /// The setter writes the (possibly mutated) struct back into component storage.
    /// </summary>
    public void Update(Action<Func<string, EntityState?>, Action<string, EntityState>> action)
    {
        _rwLock.EnterWriteLock();
        try
        {
            action(GetEntityLocked, SetEntityLocked);
        }
        finally
        {
            ApplyStructuralChangesLocked();
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>Take a read lock and call the action with a getter.</summary>
    public void View(Action<Func<string, EntityState?>> action)
    {
        _rwLock.EnterReadLock();
        try { action(GetEntityLocked); }
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

    /// <summary>
    /// Get a snapshot of all player-type entities. An archetype query on
    /// <see cref="PlayerTag"/>, not a scan with a per-entity string comparison.
    /// </summary>
    public List<EntityState> PlayerStates()
    {
        var result = new List<EntityState>();

        _rwLock.EnterReadLock();
        _iterationDepth++;
        try
        {
            foreach (ref var chunk in _arch.Query(in Players).GetChunkIterator())
            {
                int count = chunk.Count;
                for (int i = 0; i < count; i++)
                {
                    result.Add(ComposeFromChunk(ref chunk, i));
                }
            }
        }
        finally
        {
            _iterationDepth--;
            _rwLock.ExitReadLock();
        }

        return result;
    }

    /// <summary>
    /// Apply any structural changes (spawn, despawn, archetype move) that were
    /// requested while a query was being iterated.
    ///
    /// <para>ADR-11 rules out <c>CommandBuffer</c>, so this explicit phase is how
    /// structural changes stay outside iteration. In the current call graph nothing
    /// mutates during iteration and the queue is normally empty on entry — it is the
    /// backstop that keeps that property from being an unstated assumption. The tick
    /// loop calls this once per tick so the phase is visible in the tick, not
    /// implicit in a lock release.</para>
    /// </summary>
    public void ApplyStructuralChanges()
    {
        _rwLock.EnterWriteLock();
        try { ApplyStructuralChangesLocked(); }
        finally { _rwLock.ExitWriteLock(); }
    }

    public void Dispose()
    {
        _rwLock.Dispose();
        ArchWorld.Destroy(_arch);
    }

    // ---------------------------------------------------------------- internals

    /// <summary>A deferred structural change. Only created when <see cref="_iterationDepth"/> &gt; 0.</summary>
    private readonly struct StructuralOp
    {
        public readonly bool IsRemoval;
        public readonly string Id;
        public readonly EntityState State;

        private StructuralOp(bool isRemoval, string id, EntityState state)
        {
            IsRemoval = isRemoval;
            Id = id;
            State = state;
        }

        public static StructuralOp Add(EntityState state) => new(false, state.Id, state);
        public static StructuralOp Remove(string id) => new(true, id, default);
    }

    private void ApplyStructuralChangesLocked()
    {
        if (_structural.Count == 0) return;

        // Copy and clear first: applying an op must not observe the queue it is
        // draining, and _iterationDepth is guaranteed 0 here (write lock held).
        var ops = _structural.ToArray();
        _structural.Clear();

        foreach (var op in ops)
        {
            if (op.IsRemoval) RemoveEntityLocked(op.Id);
            else AddEntityLocked(op.State);
        }
    }

    private void AddEntityLocked(EntityState state)
    {
        if (_iterationDepth > 0)
        {
            _structural.Add(StructuralOp.Add(state));
            return;
        }

        bool isPlayer = state.Type == PlayerType;

        if (_index.TryGetValue(state.Id, out var existing) && _arch.IsAlive(existing))
        {
            // Fix the archetype only if player-ness changed; then overwrite in place.
            bool wasPlayer = _arch.Has<PlayerTag>(existing);
            if (isPlayer && !wasPlayer) _arch.Add(existing, new PlayerTag());
            else if (!isPlayer && wasPlayer) _arch.Remove<PlayerTag>(existing);

            Store(existing, in state);
            return;
        }

        Entity entity = isPlayer
            ? _arch.Create(
                new EntityIdRef(state.Id),
                new EntityKind(state.Type),
                new Position(state.Position),
                default(Health),
                default(Combat),
                new Locomotion(state.Speed),
                new InputCursor(state.LastInputTick),
                default(PlayerTag))
            : _arch.Create(
                new EntityIdRef(state.Id),
                new EntityKind(state.Type),
                new Position(state.Position),
                default(Health),
                default(Combat),
                new Locomotion(state.Speed),
                new InputCursor(state.LastInputTick));

        Store(entity, in state);
        _index[state.Id] = entity;
    }

    private void RemoveEntityLocked(string id)
    {
        if (_iterationDepth > 0)
        {
            _structural.Add(StructuralOp.Remove(id));
            return;
        }

        if (!_index.Remove(id, out var entity)) return;
        if (_arch.IsAlive(entity)) _arch.Destroy(entity);
    }

    private EntityState? GetEntityLocked(string id)
    {
        if (id == null) return null;
        if (!_index.TryGetValue(id, out var entity)) return null;
        if (!_arch.IsAlive(entity)) return null;
        return Compose(entity);
    }

    private void SetEntityLocked(string id, EntityState state)
    {
        if (_index.TryGetValue(id, out var entity) && _arch.IsAlive(entity))
        {
            Store(entity, in state);
            return;
        }

        // Matches the old dictionary's `_entities[id] = state`: writing an unknown id
        // creates it. The id argument wins over state.Id, as the dictionary key did.
        if (state.Id != id) state.Id = id;
        AddEntityLocked(state);
    }

    private const string PlayerType = "player";

    private EntityState Compose(Entity entity)
    {
        ref var id = ref _arch.Get<EntityIdRef>(entity);
        ref var kind = ref _arch.Get<EntityKind>(entity);
        ref var position = ref _arch.Get<Position>(entity);
        ref var health = ref _arch.Get<Health>(entity);
        ref var combat = ref _arch.Get<Combat>(entity);
        ref var locomotion = ref _arch.Get<Locomotion>(entity);
        ref var cursor = ref _arch.Get<InputCursor>(entity);

        return new EntityState
        {
            Id = id.Value,
            Type = kind.Value,
            Position = position.Value,
            Hp = health.Hp,
            MaxHp = health.MaxHp,
            Dead = health.Dead,
            Attack = combat.Attack,
            Defense = combat.Defense,
            CooldownUntilTick = combat.CooldownUntilTick,
            Speed = locomotion.Speed,
            LastInputTick = cursor.LastInputTick,
        };
    }

    private static EntityState ComposeFromChunk(ref Chunk chunk, int i)
    {
        var ids = chunk.GetSpan<EntityIdRef>();
        var kinds = chunk.GetSpan<EntityKind>();
        var positions = chunk.GetSpan<Position>();
        var healths = chunk.GetSpan<Health>();
        var combats = chunk.GetSpan<Combat>();
        var locomotions = chunk.GetSpan<Locomotion>();
        var cursors = chunk.GetSpan<InputCursor>();

        return new EntityState
        {
            Id = ids[i].Value,
            Type = kinds[i].Value,
            Position = positions[i].Value,
            Hp = healths[i].Hp,
            MaxHp = healths[i].MaxHp,
            Dead = healths[i].Dead,
            Attack = combats[i].Attack,
            Defense = combats[i].Defense,
            CooldownUntilTick = combats[i].CooldownUntilTick,
            Speed = locomotions[i].Speed,
            LastInputTick = cursors[i].LastInputTick,
        };
    }

    private void Store(Entity entity, in EntityState state)
    {
        ref var id = ref _arch.Get<EntityIdRef>(entity);
        id.Value = state.Id;

        ref var kind = ref _arch.Get<EntityKind>(entity);
        kind.Value = state.Type;

        ref var position = ref _arch.Get<Position>(entity);
        position.Value = state.Position;

        ref var health = ref _arch.Get<Health>(entity);
        health.Hp = state.Hp;
        health.MaxHp = state.MaxHp;
        health.Dead = state.Dead;

        ref var combat = ref _arch.Get<Combat>(entity);
        combat.Attack = state.Attack;
        combat.Defense = state.Defense;
        combat.CooldownUntilTick = state.CooldownUntilTick;

        ref var locomotion = ref _arch.Get<Locomotion>(entity);
        locomotion.Speed = state.Speed;

        ref var cursor = ref _arch.Get<InputCursor>(entity);
        cursor.LastInputTick = state.LastInputTick;
    }
}
