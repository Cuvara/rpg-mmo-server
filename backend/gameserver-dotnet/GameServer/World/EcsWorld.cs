using System;
using System.Collections.Generic;
using System.Threading;
using Arch.Core;
using GameServer.World.Components;
using Shared.GameLogic.Components;
using ArchWorld = Arch.Core.World;

namespace GameServer.World;

/// <summary>
/// Pending input queued for processing in the next tick, already bound to the entity
/// it addresses.
///
/// <para><b>Why the handle is here.</b> Input arrives keyed by a user id string, and
/// the tick loop used to pay for that twice per input: once to coalesce movement in a
/// <c>Dictionary&lt;string, int&gt;</c> and again inside the handler to reach the
/// entity. Both were string hashes on the simulation thread. The id is now resolved on
/// the <b>network</b> thread at <see cref="EcsWorld.PushInput"/>, under a read lock that
/// contends with nothing, so the tick sees an <see cref="EntityHandle"/>.</para>
///
/// <para><see cref="UserId"/> is kept because the handle can go stale: a disconnect
/// inside the hold window destroys the entity and a reconnect creates a new one, so an
/// input queued before that addresses a dead handle. <see cref="EcsWorld.RebindStale"/>
/// re-resolves exactly those at drain time — the string path still exists, it is just
/// no longer the common one.</para>
/// </summary>
public readonly struct PendingInput
{
    /// <summary>User id the input arrived on. The fallback key, and the log key.</summary>
    public readonly string UserId;

    /// <summary>Entity this input addresses, resolved at ingest. May be stale.</summary>
    public readonly EntityHandle Handle;

    public readonly InputData Input;

    public PendingInput(string userId, EntityHandle handle, InputData input)
    {
        UserId = userId;
        Handle = handle;
        Input = input;
    }

    /// <summary>Same input, rebound to a freshly resolved handle.</summary>
    public PendingInput WithHandle(EntityHandle handle) => new(UserId, handle, Input);
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
/// <summary>
/// Archetype tags applied at spawn. Not derived from entity data: see
/// <see cref="EnemyAi"/> for why enemy-ness cannot be inferred from
/// <c>EntityKind.Value == "mob"</c>.
/// </summary>
[Flags]
public enum EntityTags
{
    /// <summary>No extra tags. The archetype is chosen from the entity type alone.</summary>
    None = 0,

    /// <summary>Driven by the enemy AI systems. Adds <see cref="EnemyAi"/>.</summary>
    EnemyAi = 1,
}

public sealed class EcsWorld : IDisposable
{
    private readonly ArchWorld _arch = ArchWorld.Create();
    private readonly Dictionary<string, Entity> _index = new();
    private readonly List<PendingInput> _pendingInputs = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly object _inputLock = new();

    /// <summary>
    /// The component-level write scope handed to <see cref="UpdateComponents"/>.
    /// One per world, created once: entering a scope must not allocate, because the
    /// input phase enters one every tick.
    /// </summary>
    private readonly WorldWriter _writer;

    /// <summary>The read scope handed to <see cref="ReadAll"/>. One per world, so
    /// entering the snapshot broadcast allocates nothing.</summary>
    private readonly WorldReader _reader;

    public EcsWorld()
    {
        _writer = new WorldWriter(this);
        _reader = new WorldReader(this);
    }

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

    private static readonly QueryDescription Enemies = new QueryDescription()
        .WithAll<EntityIdRef, EntityKind, Position, Health, Combat, Locomotion, InputCursor, EnemyAi>();

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

    /// <summary>
    /// Create an entity carrying archetype <paramref name="tags"/>.
    ///
    /// <para>The only way to acquire <see cref="EntityTags.EnemyAi"/>. Tags are a
    /// spawn-time fact: <see cref="AddEntity"/> updating an existing entity preserves
    /// whatever tags it already has rather than re-deriving them, so writing a mutated
    /// copy back — which the combat path and the tests both do — cannot silently
    /// un-enemy an enemy and strand it outside the reaper's query.</para>
    /// </summary>
    public void Spawn(EntityState entity, EntityTags tags)
    {
        _rwLock.EnterWriteLock();
        try { AddEntityLocked(entity, tags); }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Number of live entities driven by the enemy AI. A count over the
    /// <see cref="EnemyAi"/> archetype, so it cannot drift from the world the way a
    /// parallel <c>List&lt;string&gt;</c> of ids could.
    /// </summary>
    public int EnemyCount
    {
        get
        {
            _rwLock.EnterReadLock();
            try { return _arch.CountEntities(in Enemies); }
            finally { _rwLock.ExitReadLock(); }
        }
    }

    /// <summary>Remove an entity by ID. A missing ID is a no-op.</summary>
    public void RemoveEntity(string id)
    {
        _rwLock.EnterWriteLock();
        try { RemoveEntityLocked(id); }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Whether a handle still denotes a live entity. Handles held across a tick boundary
    /// — queued input is the only case today — must be checked before use, because a
    /// disconnect can destroy the entity in between.
    /// </summary>
    public bool IsAlive(in EntityHandle handle)
    {
        _rwLock.EnterReadLock();
        try { return IsAliveLocked(in handle); }
        finally { _rwLock.ExitReadLock(); }
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

        _rwLock.EnterReadLock();
        _iterationDepth++;
        try
        {
            ScanRangeLocked(center, radius, Span<EntityState>.Empty, result);
        }
        finally
        {
            _iterationDepth--;
            _rwLock.ExitReadLock();
        }

        return result;
    }

    /// <summary>
    /// Fill <paramref name="destination"/> with every entity within
    /// <paramref name="radius"/> of <paramref name="center"/>, allocating nothing.
    ///
    /// <para>This is the form the tick loop uses. The <see cref="List{T}"/> overload
    /// allocated a list <b>per connected client per tick</b>, plus its growth
    /// reallocations — at 15 Hz that is one throwaway list per player every 67 ms, for
    /// no reason other than that the caller had nowhere to put the results. The caller
    /// now owns a buffer it reuses.</para>
    /// </summary>
    /// <returns>
    /// The <b>total number of matches</b>, which may exceed
    /// <paramref name="destination"/>'s length.
    /// <para>
    /// <b>Overflow contract — count, do not saturate.</b> Deliberately identical to
    /// <c>Shared.GameLogic.Systems.AoiLogic.GetNearbyEntities</c>: when the buffer is
    /// too small the first <c>destination.Length</c> matches are written and the scan
    /// continues, so the return value is the size the buffer needed to be. The caller
    /// detects truncation with <c>count &gt; destination.Length</c>, resizes, and calls
    /// again once. A saturating variant would make "full" indistinguishable from
    /// "exactly full", which is silent AOI truncation — entities missing from a
    /// keyframe with no error anywhere. Two AOI functions in one server with two
    /// different overflow contracts would be worse than either contract.
    /// </para>
    /// </returns>
    public int GetEntitiesInRange(Vec2 center, float radius, Span<EntityState> destination)
    {
        _rwLock.EnterReadLock();
        _iterationDepth++;
        try
        {
            return ScanRangeLocked(center, radius, destination, null);
        }
        finally
        {
            _iterationDepth--;
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// The one AOI scan. Writes to <paramref name="destination"/> and/or
    /// <paramref name="sink"/>; both forms share it so the predicate and the iteration
    /// order cannot diverge between them.
    /// </summary>
    private int ScanRangeLocked(
        Vec2 center, float radius, Span<EntityState> destination, List<EntityState>? sink)
    {
        float radiusSq = radius * radius;
        int matches = 0;

        foreach (ref var chunk in _arch.Query(in AllEntities).GetChunkIterator())
        {
            var positions = chunk.GetSpan<Position>();
            int count = chunk.Count;
            for (int i = 0; i < count; i++)
            {
                // Vec2.DistanceSq is Shared.GameLogic: the AOI predicate the
                // client predicts with, not a second copy of it here.
                if (Vec2.DistanceSq(center, positions[i].Value) > radiusSq) continue;

                if (sink != null)
                {
                    sink.Add(ComposeFromChunk(ref chunk, i));
                }
                else if (matches < destination.Length)
                {
                    destination[matches] = ComposeFromChunk(ref chunk, i);
                }

                matches++;
            }
        }

        return matches;
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

    /// <summary>
    /// Take the read lock <b>once</b> and run <paramref name="action"/> against a
    /// consistent view of the world.
    ///
    /// <para>The snapshot broadcast's entry point. It replaced two lock acquisitions per
    /// connected client per tick with one for the whole broadcast, and — more to the
    /// point — draws a line the previous shape did not have: everything inside this
    /// scope reads the world, and everything that serializes happens outside it.</para>
    /// </summary>
    public void ReadAll(Action<WorldReader> action)
    {
        _rwLock.EnterReadLock();
        _iterationDepth++;
        try { action(_reader); }
        finally
        {
            _iterationDepth--;
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>AOI scan for <see cref="WorldReader"/>; the read lock is already held.</summary>
    internal int ScanRangeLockedForReader(Vec2 center, float radius, Span<EntityState> destination) =>
        ScanRangeLocked(center, radius, destination, null);

    /// <summary>
    /// Take the write lock and run a system against component storage.
    ///
    /// <para>The component-level counterpart of <see cref="Update"/>. Same lock and same
    /// deferred structural phase; the difference is what the callback is handed —
    /// a <see cref="WorldWriter"/> giving <c>ref</c> access to individual components,
    /// rather than a getter/setter pair that round-trips a whole
    /// <see cref="EntityState"/> through seven component lookups in each direction.</para>
    /// </summary>
    public void UpdateComponents(Action<WorldWriter> action)
    {
        _rwLock.EnterWriteLock();
        try
        {
            action(_writer);
        }
        finally
        {
            ApplyStructuralChangesLocked();
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Read the two fields the snapshot broadcast needs from a player's own entity:
    /// the AOI centre and the input tick to acknowledge.
    ///
    /// <para>Called once per connection per tick. It exists because the previous form —
    /// <c>View(get =&gt; ...)</c> — composed a whole <see cref="EntityState"/> (seven
    /// component lookups, two string references) to read two fields, and the closure
    /// capturing the two <c>out</c> values allocated a display class per connection per
    /// tick. This reads exactly <see cref="Position"/> and <see cref="InputCursor"/> and
    /// allocates nothing.</para>
    /// </summary>
    /// <returns>False when the connection's entity is gone; outputs are then defaults,
    /// which is the same anchor the previous code used in that case.</returns>
    public bool TryGetSnapshotAnchor(string userId, out Vec2 position, out ulong lastInputTick)
    {
        position = default;
        lastInputTick = 0;

        _rwLock.EnterReadLock();
        try
        {
            var handle = ResolveLocked(userId);
            if (!handle.IsValid) return false;

            position = _arch.Get<Position>(handle.Value).Value;
            lastInputTick = _arch.Get<InputCursor>(handle.Value).LastInputTick;
            return true;
        }
        finally { _rwLock.ExitReadLock(); }
    }

    /// <summary>
    /// Queue an input for processing in the next tick, resolving the user id to an
    /// entity handle here — on the network thread — so the tick loop never has to.
    /// </summary>
    /// <remarks>
    /// The read lock and the input lock are taken in sequence, never nested, so this
    /// introduces no lock-ordering edge. The read lock is shared and the only writer is
    /// the tick loop's own structural/update phase, so the cost is a barrier, not a
    /// wait for the simulation.
    /// </remarks>
    public void PushInput(string userId, InputData input)
    {
        EntityHandle handle;
        _rwLock.EnterReadLock();
        try { handle = ResolveLocked(userId); }
        finally { _rwLock.ExitReadLock(); }

        lock (_inputLock)
        {
            _pendingInputs.Add(new PendingInput(userId, handle, input));
        }
    }

    /// <summary>Drain all pending inputs (returns the list and clears the queue).</summary>
    public List<PendingInput> DrainInputs()
    {
        var destination = new List<PendingInput>();
        DrainInputs(destination);
        return destination;
    }

    /// <summary>
    /// Drain all pending inputs into a caller-owned list, which is cleared first.
    /// The tick loop reuses one list, so draining allocates nothing.
    /// </summary>
    public void DrainInputs(List<PendingInput> destination)
    {
        destination.Clear();
        lock (_inputLock)
        {
            destination.AddRange(_pendingInputs);
            _pendingInputs.Clear();
        }
    }

    /// <summary>
    /// Re-resolve any queued input whose handle went stale between ingest and now —
    /// the disconnect-inside-the-hold-window case, where the entity was destroyed and
    /// recreated after the input was queued.
    ///
    /// <para>Called once at the top of the input phase so that everything downstream —
    /// movement coalescing included — can key on a handle and never on a string. Costs
    /// a dictionary lookup only for the entries that are actually stale, which is
    /// normally none.</para>
    /// </summary>
    public void RebindStale(List<PendingInput> inputs)
    {
        _rwLock.EnterReadLock();
        try
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].Handle.IsValid && _arch.IsAlive(inputs[i].Handle.Value)) continue;
                inputs[i] = inputs[i].WithHandle(ResolveLocked(inputs[i].UserId));
            }
        }
        finally { _rwLock.ExitReadLock(); }
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

    // ------------------------------------------------- component-scope internals

    /// <summary>Arch world, for <see cref="WorldWriter"/>'s <c>ref</c> component access.
    /// Internal: no <c>Arch.Core</c> type is public anywhere in this assembly.</summary>
    internal ArchWorld ArchInternal => _arch;

    /// <summary>
    /// Resolve an id to a live entity handle. The caller must already hold the read or
    /// write lock. An unknown id, a null id, or an index entry whose entity has since
    /// been destroyed all yield an invalid handle.
    /// </summary>
    internal EntityHandle ResolveLocked(string id)
    {
        if (id == null) return default;
        if (!_index.TryGetValue(id, out var entity)) return default;
        if (!_arch.IsAlive(entity)) return default;
        return new EntityHandle(entity);
    }

    /// <summary>
    /// Collect handles for every live enemy into <paramref name="destination"/>.
    /// Caller holds the write lock.
    ///
    /// <para>Same count-don't-saturate contract as the AOI scan: the return value is the
    /// total match count and may exceed the buffer, so the caller resizes and retries
    /// once rather than silently processing a prefix. Silently dropping enemies here
    /// would leave them un-moved and un-reaped — a stuck enemy nobody can kill.</para>
    ///
    /// <para>Handles rather than a chunk iterator so that the systems, which live
    /// outside <c>World/</c>, never see an <c>Arch.Core</c> type. Structural changes are
    /// safe against the returned handles: Arch's <c>Entity</c> is a stable identity, and
    /// the reaper revalidates liveness before touching one.</para>
    /// </summary>
    internal int QueryEnemiesLocked(Span<EntityHandle> destination)
    {
        int matches = 0;

        _iterationDepth++;
        try
        {
            foreach (ref var chunk in _arch.Query(in Enemies).GetChunkIterator())
            {
                int count = chunk.Count;
                for (int i = 0; i < count; i++)
                {
                    if (matches < destination.Length)
                    {
                        destination[matches] = new EntityHandle(chunk.Entity(i));
                    }
                    matches++;
                }
            }
        }
        finally { _iterationDepth--; }

        return matches;
    }

    /// <summary>Enqueue or apply a removal for a resolved entity. Caller holds the write lock.</summary>
    internal void DespawnLocked(in EntityHandle handle)
    {
        if (!IsAliveLocked(in handle)) return;
        RemoveEntityLocked(_arch.Get<EntityIdRef>(handle.Value).Value);
    }

    /// <summary>Create a tagged entity from inside a write scope. Caller holds the write lock.</summary>
    internal void SpawnLocked(EntityState state, EntityTags tags) => AddEntityLocked(state, tags);

    /// <summary>Whether a handle still denotes a live entity. Caller holds a lock.</summary>
    internal bool IsAliveLocked(in EntityHandle handle) =>
        handle.IsValid && _arch.IsAlive(handle.Value);

    /// <summary>Compose a full <see cref="EntityState"/> for <see cref="WorldWriter"/>.</summary>
    internal EntityState ComposeLocked(Entity entity) => Compose(entity);

    // ---------------------------------------------------------------- internals

    /// <summary>A deferred structural change. Only created when <see cref="_iterationDepth"/> &gt; 0.</summary>
    /// <summary>
    /// A deferred structural change. Only created when <see cref="_iterationDepth"/> &gt; 0.
    ///
    /// <para><b>Two kinds, still: add and remove.</b> Adding the enemy systems did not
    /// need a third — <c>EnemyAi</c> is applied at creation, so it rides on the add as a
    /// tag payload rather than as an add-component op. No add/remove-component operation
    /// exists because nothing needs one yet (ADR-12 decision 2); the moment something
    /// does, it belongs here and not in Arch's <c>CommandBuffer</c>, which throws under
    /// NativeAOT.</para>
    /// </summary>
    private readonly struct StructuralOp
    {
        public readonly bool IsRemoval;
        public readonly string Id;
        public readonly EntityState State;
        public readonly EntityTags Tags;

        private StructuralOp(bool isRemoval, string id, EntityState state, EntityTags tags)
        {
            IsRemoval = isRemoval;
            Id = id;
            State = state;
            Tags = tags;
        }

        public static StructuralOp Add(EntityState state, EntityTags tags) =>
            new(false, state.Id, state, tags);

        public static StructuralOp Remove(string id) => new(true, id, default, EntityTags.None);
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
            else AddEntityLocked(op.State, op.Tags);
        }
    }

    private void AddEntityLocked(EntityState state) => AddEntityLocked(state, EntityTags.None);

    private void AddEntityLocked(EntityState state, EntityTags tags)
    {
        if (_iterationDepth > 0)
        {
            _structural.Add(StructuralOp.Add(state, tags));
            return;
        }

        bool isPlayer = state.Type == PlayerType;

        if (_index.TryGetValue(state.Id, out var existing) && _arch.IsAlive(existing))
        {
            // Fix the archetype only if player-ness changed; then overwrite in place.
            // EnemyAi is deliberately NOT reconciled here: it is a spawn-time fact, and
            // re-deriving it would strip the tag from every entity written back through
            // the plain AddEntity path.
            bool wasPlayer = _arch.Has<PlayerTag>(existing);
            if (isPlayer && !wasPlayer) _arch.Add(existing, new PlayerTag());
            else if (!isPlayer && wasPlayer) _arch.Remove<PlayerTag>(existing);

            Store(existing, in state);
            return;
        }

        bool isEnemy = (tags & EntityTags.EnemyAi) != 0;

        Entity entity = isEnemy
            ? _arch.Create(
                new EntityIdRef(state.Id),
                new EntityKind(state.Type),
                new Position(state.Position),
                default(Health),
                default(Combat),
                new Locomotion(state.Speed),
                new InputCursor(state.LastInputTick),
                default(EnemyAi))
            : isPlayer
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
