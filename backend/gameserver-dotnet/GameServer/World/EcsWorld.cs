using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// tick loop reads. This is the same lock discipline <c>GameWorld</c> had — plus one
/// rule the lock alone cannot express, because <b>Arch's read path is not a read</b>: a
/// read path may only iterate a query out of <c>_readQueries</c>, refreshed by the write
/// scope that preceded it. See that field and issue #176.</description></item>
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

    /// <summary>
    /// Stable integer key per entity-id string — see <see cref="EntityIdRef.Stable"/>.
    /// Entries are <b>never removed</b>: a despawn/respawn of the same id must get the
    /// same key, or an int-keyed consumer would see a different entity where a
    /// string-keyed one saw the same. Growth is bounded by distinct ids ever seen,
    /// which the (string-keyed) delta states already paid for implicitly.
    /// </summary>
    private readonly Dictionary<string, int> _stableIds = new(StringComparer.Ordinal);

    /// <summary>Next stable key. Starts at 1 so 0 stays "no key" in diagnostics.</summary>
    private int _nextStableId = 1;
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

    /// <summary>
    /// The <see cref="Query"/> objects the <b>read</b> paths iterate, resolved once and
    /// refreshed only while the write lock is held.
    ///
    /// <para><b>Why this exists (issue #176).</b> Arch's read path is not a read. Two
    /// things it does are writes to state shared by every concurrent reader:</para>
    /// <list type="number">
    /// <item><description><c>Arch.Core.World.Query(in QueryDescription)</c> memoises into
    /// a plain <c>Dictionary&lt;QueryDescription, Query&gt;</c> and <b>inserts on a
    /// miss</b>. Two readers running two different descriptions insert concurrently; a
    /// <see cref="Dictionary{TKey,TValue}"/> torn that way does not throw at the tear —
    /// measured, it silently ends up with more entries than were inserted — and the
    /// corruption surfaces later, anywhere.</description></item>
    /// <item><description>The returned <c>Query</c> lazily rebuilds its own matching
    /// archetype list the first time it is used after the archetype <i>set</i> changed
    /// (adding entities to an existing archetype does not invalidate it; creating a new
    /// archetype does). The rebuild clears and refills a list that other readers are
    /// enumerating — which is the reported
    /// <c>NullReferenceException at Arch.Core.QueryArchetypeEnumerator.MoveNext()</c>.
    /// </description></item>
    /// </list>
    ///
    /// <para><b>What is safe.</b> Measured directly against Arch 2.1.0-beta: eight
    /// threads iterating one shared <c>Query</c> whose memo is stale faulted in 20 of 200
    /// attempts <b>with no writer running at all</b>; the same eight threads iterating the
    /// same <c>Query</c> with the memo already up to date faulted 0 of 200. So iteration
    /// of an up-to-date query is a genuine pure read, and the whole hazard is the lazy
    /// refresh and the cache insert.</para>
    ///
    /// <para><b>The rule that follows.</b> A read path may only iterate a query out of
    /// this list, and every write scope refreshes the list on its way out — see
    /// <see cref="ExitWriteScope"/>. That keeps the reader/writer lock as the primitive
    /// and keeps <see cref="ReadAllParallel"/> genuinely parallel, which a mutex over the
    /// AOI gather would not: the gather is 77-83% of a 200-viewer tick.</para>
    /// </summary>
    private readonly List<Query> _readQueries = new();

    /// <summary>The AOI scan's query. Read paths only; refreshed under the write lock.</summary>
    private readonly Query _allEntitiesQuery;

    /// <summary>The player snapshot's query. Read paths only; refreshed under the write lock.</summary>
    private readonly Query _playersQuery;

    public EcsWorld() : this(1) { }

    /// <summary>
    /// Create a world whose deferred-structural queue has room for
    /// <paramref name="maxWorkerSlots"/> concurrent producers.
    ///
    /// <para>One slot is the serial world and the default: identical storage to the
    /// single list this replaced. More than one is only useful to
    /// <see cref="UpdateComponentsParallel"/>, and the slots are allocated up front
    /// because a worker must never allocate its queue on the hot path.</para>
    /// </summary>
    public EcsWorld(int maxWorkerSlots)
    {
        if (maxWorkerSlots < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxWorkerSlots), maxWorkerSlots, "A world needs at least one structural slot.");
        }

        _writer = new WorldWriter(this);
        _reader = new WorldReader(this);

        // Resolved here, on the only thread that can see this world, so the query cache
        // is never written from a read path. See _readQueries.
        _allEntitiesQuery = _arch.Query(in AllEntities);
        _playersQuery = _arch.Query(in Players);
        _readQueries.Add(_allEntitiesQuery);
        _readQueries.Add(_playersQuery);

        _structuralSlots = new List<StructuralOp>[maxWorkerSlots];
        for (int i = 0; i < maxWorkerSlots; i++) _structuralSlots[i] = new List<StructuralOp>();

        // No thread is started here: the pool starts a worker the first time a region
        // actually asks for it, so a one-slot world -- and the many multi-slot worlds the
        // tests build and drop -- own nothing.
        _pool = new SimWorkerPool(this, maxWorkerSlots);
    }

    /// <summary>The world's parked simulation workers. See <see cref="SimWorkerPool"/>.</summary>
    private readonly SimWorkerPool _pool;

    /// <summary>
    /// Queued structural changes, drained by <see cref="ApplyStructuralChanges"/>, one
    /// list per worker slot.
    ///
    /// <para><b>Why per-slot rather than one locked list.</b> The hazard a shared queue
    /// carries is not the data race — a lock fixes that — it is the <b>order</b>. Ops are
    /// replayed in queue order, and replay calls <c>Arch.Create</c>, so queue order
    /// decides the order entities are created, which decides chunk layout, which decides
    /// iteration order, which decides the order floats accumulate. Under a shared queue
    /// that order is arrival order, i.e. whichever worker happened to get there first.
    /// The golden vectors and the byte-identical snapshot digests would then break
    /// intermittently — the failure mode that is hardest to attribute and hardest to
    /// reproduce.</para>
    ///
    /// <para>With one list per slot and a drain that walks slots in index order, the
    /// replay order is a function of (slot index, position within slot) and of nothing
    /// else. It does not depend on how the workers were scheduled, so the same inputs
    /// produce the same world on every run and on every core count.</para>
    /// </summary>
    private readonly List<StructuralOp>[] _structuralSlots;

    /// <summary>
    /// Number of ops queued across <see cref="_structuralSlots"/>, maintained with
    /// <see cref="Interlocked"/> at the two enqueue sites and reset in the drain. Lets
    /// <see cref="ApplyStructuralChanges"/> skip the exclusive lock in the normal case:
    /// the queue is almost always empty, and taking a ReaderWriterLockSlim write lock
    /// at 60 Hz blocks new readers and drains in-flight ones — the network threads,
    /// which acquire the read lock on every inbound packet (#249). Workers that
    /// enqueue do so inside a region the tick thread joins before draining, so the
    /// count is never behind the slots when it is read here.
    /// </summary>
    private int _queuedStructuralOps;

    /// <summary>
    /// Which structural slot THIS thread writes into. Zero — the slot the serial world
    /// uses — unless <see cref="UpdateComponentsParallel"/> assigned one.
    ///
    /// <para>Thread-static is right here and wrong for <see cref="_iterationDepth"/>,
    /// which is the distinction that took the longest to see: "which worker am I" is
    /// genuinely a property of the thread, whereas "is anything iterating this world" is
    /// a property of the world.</para>
    /// </summary>
    [ThreadStatic]
    private static int _workerSlot;

    /// <summary>
    /// True between the start and the join of a <see cref="UpdateComponentsParallel"/>
    /// region.
    ///
    /// <para>This is the world-level half of the deferral decision that
    /// <see cref="_iterationDepth"/> cannot answer. That flag is thread-static, so under
    /// workers it says "is <i>this thread</i> iterating" — one worker could take the
    /// immediate path, mutating archetypes, while another is mid-iteration over them.
    /// Deferral has to be a property of the world, and inside a parallel region the
    /// answer is unconditionally yes.</para>
    ///
    /// <para>Written only by the thread that opens and closes the region, and read by
    /// the workers it starts; the thread start and the join are themselves barriers, so
    /// no interlocked access is needed and the query hot path keeps a plain read.</para>
    /// </summary>
    private volatile bool _parallelRegion;

    /// <summary>
    /// Whether a structural change must be queued rather than applied now.
    ///
    /// <para>Either because this thread is mid-iteration and mutating archetypes under
    /// it would invalidate the iteration, or because a parallel region is open and
    /// immediate application is not a safe thing for any worker to do.</para>
    /// </summary>
    private bool DeferStructural => _iterationDepth > 0 || _parallelRegion;

    /// <summary>
    /// Non-zero while THIS thread is inside an Arch query iteration. Thread-static
    /// rather than a plain field so concurrent readers cannot race on it; the
    /// reader/writer lock already prevents a writer from overlapping a reader, so
    /// what remains is same-thread re-entrancy, which is exactly what this catches.
    ///
    /// <para>It is deliberately <b>not</b> the whole deferral rule — see
    /// <see cref="_parallelRegion"/> and <see cref="DeferStructural"/>.</para>
    /// </summary>
    [ThreadStatic]
    private static int _iterationDepth;

    private static readonly QueryDescription AllEntities = new QueryDescription()
        .WithAll<EntityIdRef, EntityKind, Position, Health, Combat, Locomotion, InputCursor>();

    private static readonly QueryDescription Players = new QueryDescription()
        .WithAll<EntityIdRef, EntityKind, Position, Health, Combat, Locomotion, InputCursor, PlayerTag>();

    /// <summary>
    /// Leave the write lock, refreshing the read-path queries first.
    ///
    /// <para><b>Every</b> write scope exits through here rather than calling
    /// <c>_rwLock.ExitWriteLock()</c> directly, and that is the whole enforcement of the
    /// rule in <see cref="_readQueries"/>: a write scope is the only place an archetype
    /// can be created, and the exclusive lock is the only moment at which a query's
    /// memoised archetype list can be rebuilt without another reader watching. Refreshing
    /// anywhere else would be refreshing under the shared lock, which is the defect.</para>
    /// </summary>
    private void ExitWriteScope()
    {
        // The release is in a finally of its own: this runs from the finally of every
        // write scope, and a throw here would leave the write lock held forever, which
        // is a hung server rather than a failed operation.
        try { RefreshReadQueriesLocked(); }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Force every read-path query to re-derive its matching archetypes now, while this
    /// thread holds the write lock, so that no reader ever triggers the rebuild.
    ///
    /// <para>Constructing the chunk iterator is what performs the refresh — verified
    /// against Arch 2.1.0-beta by watching <c>Query._allArchetypesHashCode</c> change on
    /// the constructor alone — so the iterator is built and dropped without enumerating.
    /// It is O(number of queries) when nothing changed, which is the normal case: the
    /// memo is only invalidated by a <i>new archetype</i>, and this server creates a
    /// handful of them in total.</para>
    /// </summary>
    private void RefreshReadQueriesLocked()
    {
        for (int i = 0; i < _readQueries.Count; i++)
        {
            _ = _readQueries[i].GetChunkIterator();
        }
    }

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
        finally { ExitWriteScope(); }
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
        finally { ExitWriteScope(); }
    }

    /// <summary>
    /// Number of live entities carrying <typeparamref name="TTag"/>.
    ///
    /// <para>Generic because the core must not name the gameplay: this replaced an
    /// <c>EnemyCount</c> property, which meant <c>World/</c> knew what an enemy was. The
    /// tag is supplied by whoever owns the content.</para>
    ///
    /// <para><b>Exclusive, not shared</b>, and deliberately so (issue #176). The tag set is
    /// open — a closed generic first appears at its first call — so this query cannot be
    /// resolved up front into <see cref="_readQueries"/> the way the AOI and player queries
    /// are, and <c>Arch.Core.World.CountEntities</c> resolves it through the world's query
    /// cache, which is a write. Nothing on the tick's hot path calls this: it answers a
    /// diagnostics gauge and the scaffolding spawner's <c>AliveCount</c>. Taking the write
    /// lock for it costs nothing measurable and removes the whole question.</para>
    /// </summary>
    public int CountWith<TTag>() where TTag : struct
    {
        _rwLock.EnterWriteLock();
        try { return _arch.CountEntities(in TaggedQuery<TTag>.Description); }
        finally { ExitWriteScope(); }
    }

    /// <summary>Remove an entity by ID. A missing ID is a no-op.</summary>
    public void RemoveEntity(string id)
    {
        _rwLock.EnterWriteLock();
        try { RemoveEntityLocked(id); }
        finally { ExitWriteScope(); }
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

        // _allEntitiesQuery, never _arch.Query(...): this runs under the SHARED lock, and
        // resolving or refreshing a query there is what issue #176 is.
        foreach (ref var chunk in _allEntitiesQuery.GetChunkIterator())
        {
            var positions = chunk.GetSpan<Position>();
            // Hoisted once per chunk rather than fetched per match. Composing through
            // ComposeFromChunk did seven GetSpan lookups per matching entity, and the
            // scan's cost is composing matches, not the distance tests (BENCHMARK.md
            // Part V): an A/B at 200 entities / 20 matches measured the per-match form
            // at 3.3-9.1 us/scan against 2.2-2.9 us/scan for this one — at least 1.5x,
            // reproducibly. The gather is 77-83% of a 200-viewer tick, so the per-match
            // lookups were the dominant cost of the tick's dominant phase.
            var ids = chunk.GetSpan<EntityIdRef>();
            var kinds = chunk.GetSpan<EntityKind>();
            var healths = chunk.GetSpan<Health>();
            var combats = chunk.GetSpan<Combat>();
            var locomotions = chunk.GetSpan<Locomotion>();
            var cursors = chunk.GetSpan<InputCursor>();
            int count = chunk.Count;
            for (int i = 0; i < count; i++)
            {
                // Vec2.DistanceSq is Shared.GameLogic: the AOI predicate the
                // client predicts with, not a second copy of it here.
                if (Vec2.DistanceSq(center, positions[i].Value) > radiusSq) continue;

                if (sink != null || matches < destination.Length)
                {
                    var composed = new EntityState
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
                    if (sink != null) sink.Add(composed);
                    else destination[matches] = composed;
                }

                matches++;
            }
        }

        return matches;
    }

    /// <summary>
    /// The snapshot gather's AOI scan: same query, same iteration order and the same
    /// distance predicate as <see cref="ScanRangeLocked"/>, composing the trimmed
    /// <see cref="EntityView"/> per match instead of a full <see cref="EntityState"/>.
    ///
    /// <para><b>Why a second scan body exists.</b> The snapshot encoder consumes only
    /// Id/Type/X/Y/Hp/MaxHp/Speed, so the full compose paid for the <c>Combat</c> and
    /// <c>InputCursor</c> span fetches per chunk and five unread fields per match — on
    /// the phase Part V measured as compose-bound and 77-83% of a 200-viewer tick
    /// (issue #237). The predicate and iteration order are pinned to the full scan by
    /// <c>TrimmedGatherByteIdentityTests</c>: if the two bodies ever diverge, the wire
    /// digest moves and that test names the tick.</para>
    ///
    /// <para>Same count-don't-saturate overflow contract as
    /// <see cref="GetEntitiesInRange(Vec2, float, Span{EntityState})"/>.</para>
    /// </summary>
    private int ScanRangeViewsLocked(Vec2 center, float radius, Span<EntityView> destination)
    {
        float radiusSq = radius * radius;
        int matches = 0;

        // _allEntitiesQuery, never _arch.Query(...): shared lock — see issue #176.
        foreach (ref var chunk in _allEntitiesQuery.GetChunkIterator())
        {
            var positions = chunk.GetSpan<Position>();
            var ids = chunk.GetSpan<EntityIdRef>();
            var kinds = chunk.GetSpan<EntityKind>();
            var healths = chunk.GetSpan<Health>();
            var locomotions = chunk.GetSpan<Locomotion>();
            // No Combat span, no InputCursor span: the two fetches the trimmed
            // compose exists to drop. Nothing below reads them.
            int count = chunk.Count;
            for (int i = 0; i < count; i++)
            {
                // Identical predicate to ScanRangeLocked — Shared.GameLogic's, not a copy.
                if (Vec2.DistanceSq(center, positions[i].Value) > radiusSq) continue;

                if (matches < destination.Length)
                {
                    destination[matches] = new EntityView(
                        ids[i].Stable,
                        ids[i].Value,
                        kinds[i].Value,
                        positions[i].Value,
                        healths[i].Hp,
                        healths[i].MaxHp,
                        locomotions[i].Speed);
                }

                matches++;
            }
        }

        return matches;
    }

    /// <summary>
    /// Snapshot-view AOI scan for <see cref="WorldReader"/>; the read lock is already
    /// held. The form <see cref="Net.Connection"/>'s gather uses.
    /// </summary>
    internal int ScanRangeViewsLockedForReader(Vec2 center, float radius, Span<EntityView> destination) =>
        ScanRangeViewsLocked(center, radius, destination);

    /// <summary>
    /// Fill <paramref name="destination"/> with the trimmed snapshot view of every
    /// entity within <paramref name="radius"/> of <paramref name="center"/>. Same
    /// overflow contract as the <see cref="EntityState"/> overload.
    /// </summary>
    public int GetEntitiesInRange(Vec2 center, float radius, Span<EntityView> destination)
    {
        _rwLock.EnterReadLock();
        _iterationDepth++;
        try
        {
            return ScanRangeViewsLocked(center, radius, destination);
        }
        finally
        {
            _iterationDepth--;
            _rwLock.ExitReadLock();
        }
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
            ExitWriteScope();
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

    /// <summary>
    /// Take the read lock <b>once</b> and run <paramref name="body"/> on
    /// <paramref name="workerCount"/> workers against that one consistent view.
    ///
    /// <para>The read-side counterpart of <see cref="UpdateComponentsParallel"/>, and the
    /// reason the worker pool exists at all: the AOI gather is N independent per-viewer
    /// range queries, each writing into a buffer its own connection owns, and it is
    /// 77-83% of a 200-viewer tick (docs/DESIGN.md, "Where the tick budget goes").</para>
    ///
    /// <para><b>No determinism machinery, deliberately.</b> The write-side region needs
    /// per-slot structural queues and slot-ordered replay because <c>Arch.Create</c> order
    /// decides chunk layout and therefore float accumulation order. A read region has no
    /// structural ops to order: <see cref="WorldReader"/> exposes only
    /// <see cref="WorldReader.TryGetSnapshotAnchor"/> and
    /// <see cref="WorldReader.GetEntitiesInRange"/>, both pure reads, and the world is not
    /// mutated at all while the read lock is held. Output therefore cannot depend on how
    /// the workers interleaved, so <see cref="_parallelRegion"/> is <b>not</b> set here and
    /// no drain follows. Adding the slot machinery anyway would cost time and imply a
    /// hazard that does not exist.</para>
    ///
    /// <para><b>"Pure read" is a property of this class, not of Arch</b> (issue #176).
    /// Arch's own read path writes: it resolves queries through a shared dictionary and
    /// rebuilds a query's matching-archetype list lazily, both under whatever lock the
    /// caller happens to hold. Several workers doing that at once corrupted the enumerator
    /// and, twice, the heap. What makes this region safe is that <see cref="WorldReader"/>
    /// only ever iterates a query out of <see cref="_readQueries"/>, already refreshed by
    /// the write scope that preceded this one — see <see cref="ExitWriteScope"/>.</para>
    ///
    /// <para><b>The lock is held by the owner, not by the workers</b>, and that is
    /// sufficient: the calling thread enters the read lock before any worker is woken and
    /// does not leave the scope until every worker has rendezvoused, so the workers run
    /// strictly inside a read-locked interval and no writer can enter. The scan path they
    /// use takes no lock of its own.</para>
    ///
    /// <para><b>What the caller must still guarantee</b> is that the per-worker bodies do
    /// not share mutable state with each other. That is a property of the callback, not of
    /// this method, exactly as it is for the write-side region.</para>
    /// </summary>
    /// <param name="workerCount">1 to <c>maxWorkerSlots</c>. One runs inline on the
    /// caller and starts no thread.</param>
    /// <param name="body">Called once per worker with the shared reader and that worker's
    /// index. Worker 0 runs on the calling thread.</param>
    public void ReadAllParallel(int workerCount, Action<WorldReader, int> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (workerCount < 1 || workerCount > _structuralSlots.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerCount), workerCount,
                $"This world was built with {_structuralSlots.Length} worker slot(s).");
        }

        if (workerCount == 1)
        {
            // Identical to ReadAll, including the iteration-depth bookkeeping. Nothing is
            // dispatched, so a one-worker read region costs what the serial one costs.
            _rwLock.EnterReadLock();
            _iterationDepth++;
            try { body(_reader, 0); }
            finally
            {
                _iterationDepth--;
                _rwLock.ExitReadLock();
            }
            return;
        }

        _rwLock.EnterReadLock();
        try
        {
            var failures = new Exception?[workerCount];
            _pool.RunReadRegion(workerCount, body, failures);

            List<Exception>? thrown = null;
            for (int i = 0; i < failures.Length; i++)
            {
                if (failures[i] is { } ex) (thrown ??= new List<Exception>()).Add(ex);
            }

            if (thrown is not null)
            {
                throw thrown.Count == 1
                    ? thrown[0]
                    : new AggregateException("One or more gather workers failed.", thrown);
            }
        }
        finally { _rwLock.ExitReadLock(); }
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
    public void UpdateComponents<TState>(TState state, Action<TState, WorldWriter> action)
    {
        _rwLock.EnterWriteLock();
        try
        {
            action(state, _writer);
        }
        finally
        {
            ApplyStructuralChangesLocked();
            ExitWriteScope();
        }
    }

    /// <inheritdoc cref="UpdateComponents{TState}(TState, Action{TState, WorldWriter})"/>
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
            ExitWriteScope();
        }
    }

    /// <summary>
    /// Run <paramref name="body"/> on <paramref name="workerCount"/> threads inside one
    /// write scope, then apply the structural changes they queued.
    ///
    /// <para><b>Nothing in the tick loop calls this.</b> It exists so that the two
    /// preconditions ADR-12 records as blocking parallel simulation can be
    /// <i>demonstrated</i> rather than asserted: the per-slot structural queue and the
    /// world-level deferral flag are only exercised by a region that actually starts
    /// workers. Wiring it into the schedule is a separate decision and needs a workload
    /// that benefits — today every pair of systems in the schedule conflicts, so a
    /// parallel step would run them one at a time anyway.</para>
    ///
    /// <para><b>What this does not do.</b> It does not check that the bodies are safe to
    /// run together. That is <see cref="Server.ComponentAccess.IsDisjointFrom"/>'s job and
    /// it belongs to whoever builds the schedule; this primitive assumes the caller has
    /// already established disjointness. Two bodies writing the same component through
    /// this will race, exactly as they would through any other parallel-for.</para>
    ///
    /// <para><b>What it guarantees.</b> Every worker sees <see cref="DeferStructural"/>
    /// true, so spawns and despawns are queued rather than applied under another worker's
    /// iteration; each worker queues into its own slot, so the queues themselves never
    /// race; and the drain replays slots in index order, so the resulting world does not
    /// depend on the order the workers finished. The last of those is the property the
    /// golden vectors need.</para>
    /// </summary>
    /// <param name="workerCount">
    /// Number of workers, from 1 to the <c>maxWorkerSlots</c> the world was built with.
    /// One is a legitimate value and is what the determinism harness compares against.
    /// </param>
    /// <param name="body">
    /// Called once per worker with the shared writer and that worker's index. Worker 0
    /// runs on the calling thread, so a single-worker region starts no threads at all.
    /// </param>
    public void UpdateComponentsParallel(int workerCount, Action<WorldWriter, int> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (workerCount < 1 || workerCount > _structuralSlots.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerCount), workerCount,
                $"This world was built with {_structuralSlots.Length} structural slot(s). " +
                "Construct it with a larger maxWorkerSlots to run more workers -- slots are " +
                "allocated up front precisely so a worker never allocates its queue.");
        }

        _rwLock.EnterWriteLock();
        _parallelRegion = true;
        try
        {
            var failures = new Exception?[workerCount];

            if (workerCount == 1)
            {
                // No rendezvous, no pool, no thread: the single-worker region is the
                // determinism harness's baseline and must stay exactly as cheap as a
                // serial scope.
                _workerSlot = 0;
                try { body(_writer, 0); }
                catch (Exception ex) { failures[0] = ex; }
            }
            else
            {
                // Dedicated, world-owned threads rather than the thread pool: a pool
                // thread carries _workerSlot away with it after the region ends, and the
                // pool is shared with the connection handlers, so borrowing from it here
                // couples simulation latency to network load. The threads are parked
                // between regions rather than created per region -- see SimWorkerPool.
                _pool.RunWriteRegion(workerCount, body, failures);
            }

            // Rethrow only after every worker has rendezvoused. Leaving a worker running
            // while the write lock unwinds would let it touch the world outside the
            // region, which is worse than the original fault.
            List<Exception>? thrown = null;
            for (int i = 0; i < failures.Length; i++)
            {
                if (failures[i] is { } ex) (thrown ??= new List<Exception>()).Add(ex);
            }

            if (thrown is not null)
            {
                throw thrown.Count == 1
                    ? thrown[0]
                    : new AggregateException("One or more simulation workers failed.", thrown);
            }
        }
        finally
        {
            _parallelRegion = false;
            ApplyStructuralChangesLocked();
            ExitWriteScope();
        }
    }

    /// <summary>
    /// The world's own simulation worker threads, parked between regions.
    ///
    /// <para><b>Why a pool at all.</b> The first shape of
    /// <see cref="UpdateComponentsParallel"/> started a fresh <see cref="Thread"/> per
    /// worker per region and joined it at the end. That is correct, and it is also the
    /// whole cost: measured on this box, 165-225 microseconds per additional worker, paid
    /// before a single component is touched, which put break-even against the serial path
    /// near 70 000 entities. Parking N threads once and waking them per region removes
    /// thread creation from the region entirely.</para>
    ///
    /// <para><b>What it must keep.</b> Two properties of the per-region-thread shape are
    /// load-bearing and are preserved here. First, the threads are <i>dedicated</i>: a
    /// thread-pool thread would carry <see cref="_workerSlot"/> away with it after the
    /// region, and that pool is shared with the connection handlers, so borrowing from it
    /// would couple simulation latency to network load. Second, slot identity is stable:
    /// each thread sets <see cref="_workerSlot"/> once, at start, and never changes it, so
    /// the mapping from worker to structural queue is fixed for the life of the world. It
    /// can no longer drift with scheduling, which is what slot-ordered replay -- and
    /// therefore the golden vectors -- depend on.</para>
    ///
    /// <para><b>Wake protocol.</b> One generation counter per worker, on its own cache
    /// line, plus a <see cref="ManualResetEventSlim"/> used only when a worker gives up
    /// spinning. A worker resets its event <i>before</i> re-reading its generation, so a
    /// signal that lands in the gap is never lost: if the owner bumped the generation
    /// before the reset, the read after the reset sees it; if the owner sets the event
    /// after the reset, the event stays signalled and the following wait returns at once.
    /// The owner bumps only the generations of the workers a region actually uses, so a
    /// w=2 region does not wake six parked threads.</para>
    ///
    /// <para><b>Who spins.</b> The region owner spins while it waits for the rendezvous;
    /// a finished worker parks immediately. That asymmetry is measured, not stylistic --
    /// see <c>WorkerParkSpinMicros</c>, which records what a worker-side spin bought and
    /// what it cost the code that ran next.</para>
    ///
    /// <para><b>What it costs now.</b> An empty region on this 12-core host: 24 / 44 / 103
    /// microseconds at 2 / 4 / 8 workers, i.e. roughly 15-24 microseconds per additional
    /// worker, against 156-178 microseconds per additional worker for the per-region
    /// threads this replaced. Break-even against a serial pass over the same component
    /// work moved from about 70 000 entities to about 8 000. Both are re-measurable with
    /// <c>BENCH_PARALLEL=1</c>.</para>
    /// </summary>
    private sealed class SimWorkerPool : IDisposable
    {
        /// <summary>
        /// How long the region owner spins waiting for its workers before blocking.
        ///
        /// <para>Free in the sense that matters: the owner has nothing else to do until the
        /// rendezvous, and the spin ends the instant the last worker decrements. It does not
        /// outlive the region, so it cannot inflate whatever the caller runs next.</para>
        /// </summary>
        private const double OwnerWaitSpinMicros = 100.0;

        /// <summary>
        /// How long a finished worker spins on its generation before parking. <b>Zero, and
        /// that is a measured choice.</b>
        ///
        /// <para>A 25 microsecond worker spin made back-to-back regions ~1.7x cheaper
        /// (w=4 empty region 31 us hot against 59 us parked). It also inflated whatever ran
        /// in the following 25 microseconds by up to 6x: with the spin on, a serial pass
        /// over 30 entities immediately after a region measured 12.5 us; with it off, the
        /// same pass measured 1.9 us. The live tick dispatches at most one region per 66 ms
        /// and then immediately serializes snapshots on the calling thread, so the spin
        /// could only ever collect the cost and never the benefit. Restore it only with a
        /// workload that actually issues regions back to back, and re-measure what runs
        /// after them when you do.</para>
        /// </summary>
        private const double WorkerParkSpinMicros = 0.0;

        /// <summary>Ints per generation cell, so two workers never share a cache line.</summary>
        private const int Stride = 16;

        private static readonly long OwnerSpinTicks =
            (long)(OwnerWaitSpinMicros * Stopwatch.Frequency / 1_000_000.0);

        private static readonly long WorkerSpinTicks =
            (long)(WorkerParkSpinMicros * Stopwatch.Frequency / 1_000_000.0);

        private readonly EcsWorld _world;
        private readonly Thread?[] _threads;
        private readonly ManualResetEventSlim?[] _wake;

        /// <summary>Per-worker generation, strided one per cache line. The worker reads
        /// its own cell; the owner writes it to publish a region.</summary>
        private readonly int[] _generation;

        /// <summary>The owner's copy of each generation, so publishing is a plain volatile
        /// write rather than a read-modify-write on a shared cell.</summary>
        private readonly int[] _published;

        private readonly ManualResetEventSlim _done = new(false, 0);
        private readonly object _startLock = new();

        private Action<WorldWriter, int>? _writeBody;
        private Action<WorldReader, int>? _readBody;
        private Exception?[]? _failures;
        private int _pending;
        private volatile bool _shutdown;

        internal SimWorkerPool(EcsWorld world, int maxWorkerSlots)
        {
            _world = world;
            _threads = new Thread?[maxWorkerSlots];
            _wake = new ManualResetEventSlim?[maxWorkerSlots];
            _generation = new int[maxWorkerSlots * Stride];
            _published = new int[maxWorkerSlots];
        }

        /// <summary>How many worker threads this world has started so far. The whole point
        /// of the pool is that this stops growing, so the tests read it.</summary>
        internal int StartedThreadCount
        {
            get
            {
                int n = 0;
                lock (_startLock)
                {
                    for (int i = 0; i < _threads.Length; i++) if (_threads[i] is not null) n++;
                }
                return n;
            }
        }

        /// <summary>
        /// Run <paramref name="body"/> on slots 1..<paramref name="workerCount"/>-1 and
        /// return only once every one of them has finished. Slot 0 belongs to the caller
        /// and is run here on the calling thread, between publishing the work and waiting
        /// for it, so the owner's own share overlaps the workers' wake-up.
        /// </summary>
        internal void RunWriteRegion(int workerCount, Action<WorldWriter, int> body, Exception?[] failures) =>
            Dispatch(workerCount, body, null, failures);

        /// <summary>
        /// The read-side region. Same threads, same rendezvous, no slot machinery: a
        /// <see cref="WorldReader"/> cannot request a structural change, so there is
        /// nothing to queue and nothing whose replay order could matter.
        /// </summary>
        internal void RunReadRegion(int workerCount, Action<WorldReader, int> body, Exception?[] failures) =>
            Dispatch(workerCount, null, body, failures);

        private void Dispatch(
            int workerCount,
            Action<WorldWriter, int>? writeBody,
            Action<WorldReader, int>? readBody,
            Exception?[] failures)
        {
            EnsureStarted(workerCount);

            _writeBody = writeBody;
            _readBody = readBody;
            _failures = failures;
            _done.Reset();
            Volatile.Write(ref _pending, workerCount - 1);

            // Publishing the generation is the release that makes the body, _failures and
            // _pending visible to a worker, which reads its generation first.
            for (int slot = 1; slot < workerCount; slot++)
            {
                Volatile.Write(ref _generation[slot * Stride], ++_published[slot]);
                _wake[slot]!.Set();
            }

            _workerSlot = 0;
            try { Invoke(writeBody, readBody, 0); }
            catch (Exception ex) { failures[0] = ex; }

            WaitForWorkers();

            _writeBody = null;
            _readBody = null;
            _failures = null;
        }

        private void Invoke(Action<WorldWriter, int>? writeBody, Action<WorldReader, int>? readBody, int slot)
        {
            if (writeBody is not null)
            {
                writeBody(_world._writer, slot);
                return;
            }

            if (readBody is null) return;

            // Mirror the serial ReadAll scope: every thread that iterates the world says
            // so, so the deferral rule reads the same on a worker as on the tick thread.
            _iterationDepth++;
            try { readBody(_world._reader, slot); }
            finally { _iterationDepth--; }
        }

        private void WaitForWorkers()
        {
            long deadline = Stopwatch.GetTimestamp() + OwnerSpinTicks;
            while (Volatile.Read(ref _pending) != 0)
            {
                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    // Park. The last worker out sets _done, and _done was reset before any
                    // worker could run, so this cannot miss the signal.
                    while (Volatile.Read(ref _pending) != 0) _done.Wait();
                    return;
                }
                Thread.SpinWait(20);
            }
        }

        private void EnsureStarted(int workerCount)
        {
            lock (_startLock)
            {
                ObjectDisposedException.ThrowIf(_shutdown, this);

                for (int slot = 1; slot < workerCount; slot++)
                {
                    if (_threads[slot] is not null) continue;

                    _wake[slot] = new ManualResetEventSlim(false, 0);
                    int captured = slot;
                    var t = new Thread(() => WorkerLoop(captured))
                    {
                        IsBackground = true,
                        Name = $"sim-worker-{slot}",
                    };
                    _threads[slot] = t;
                    t.Start();
                }
            }
        }

        private void WorkerLoop(int slot)
        {
            // Set once, for the life of the thread. This is the invariant the per-region
            // threads could only re-establish on every region.
            _workerSlot = slot;

            ManualResetEventSlim wake = _wake[slot]!;
            int cell = slot * Stride;
            int seen = 0;

            while (true)
            {
                // Reset before the read: a Set that lands after this reset survives to the
                // Wait below, and a generation bump from before it is caught by the read
                // that follows. The barrier stops the store and the load being reordered.
                wake.Reset();
                Interlocked.MemoryBarrier();

                if (Volatile.Read(ref _generation[cell]) == seen)
                {
                    long deadline = Stopwatch.GetTimestamp() + WorkerSpinTicks;
                    while (Volatile.Read(ref _generation[cell]) == seen)
                    {
                        if (Stopwatch.GetTimestamp() >= deadline)
                        {
                            wake.Wait();
                            break;
                        }
                        Thread.SpinWait(20);
                    }

                    if (Volatile.Read(ref _generation[cell]) == seen) continue; // spurious
                }

                seen = Volatile.Read(ref _generation[cell]);
                if (_shutdown) return;

                var writeBody = _writeBody;
                var readBody = _readBody;
                var failures = _failures;
                try { Invoke(writeBody, readBody, slot); }
                catch (Exception ex) { if (failures is not null) failures[slot] = ex; }

                // A full fence, so the body's writes and the failure cell are visible to
                // the owner before it can observe the count reaching zero.
                if (Interlocked.Decrement(ref _pending) == 0) _done.Set();
            }
        }

        public void Dispose()
        {
            lock (_startLock)
            {
                if (_shutdown) return;
                _shutdown = true;

                for (int slot = 1; slot < _threads.Length; slot++)
                {
                    if (_threads[slot] is null) continue;
                    Volatile.Write(ref _generation[slot * Stride], ++_published[slot]);
                    _wake[slot]!.Set();
                }
            }

            for (int slot = 1; slot < _threads.Length; slot++)
            {
                // Bounded: the threads are background threads, so a worker wedged inside a
                // caller's body must not turn disposing a world into a hang.
                _threads[slot]?.Join(TimeSpan.FromSeconds(5));
                _threads[slot] = null;
            }

            for (int slot = 1; slot < _wake.Length; slot++) _wake[slot]?.Dispose();
            _done.Dispose();
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
            // _playersQuery, never _arch.Query(...) — see ScanRangeLocked and #176.
            foreach (ref var chunk in _playersQuery.GetChunkIterator())
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
        if (Volatile.Read(ref _queuedStructuralOps) == 0) return;

        _rwLock.EnterWriteLock();
        try { ApplyStructuralChangesLocked(); }
        finally { ExitWriteScope(); }
    }

    public void Dispose()
    {
        // Before the lock and the Arch world go: a parked worker must never wake to find
        // either of them disposed.
        _pool.Dispose();
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
    /// Collect handles for every live entity carrying <typeparamref name="TTag"/> into
    /// <paramref name="destination"/>. Caller holds the write lock.
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
    internal int QueryWithLocked<TTag>(Span<EntityHandle> destination) where TTag : struct
    {
        int matches = 0;

        _iterationDepth++;
        try
        {
            foreach (ref var chunk in _arch.Query(in TaggedQuery<TTag>.Description).GetChunkIterator())
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

    /// <summary>
    /// Per-tag query description, built once per closed generic rather than per call.
    ///
    /// <para>A static generic field is the allocation-free way to memoise this. It is also
    /// AOT-safe: every instantiation is reached from concrete code, so ILC sees it — no
    /// reflection and no runtime type construction, which is what ADR-11 actually
    /// forbids.</para>
    /// </summary>
    private static class TaggedQuery<TTag> where TTag : struct
    {
        public static readonly QueryDescription Description = new QueryDescription()
            .WithAll<EntityIdRef, EntityKind, Position, Health, Combat, Locomotion, InputCursor, TTag>();
    }

    /// <summary>
    /// Run <paramref name="visitor"/> over every chunk of entities carrying
    /// <typeparamref name="TTag"/>, handing it the component arrays directly. Caller
    /// holds the write lock.
    ///
    /// <para>This is the shape the core's own scans use and the shape ECS exists to give:
    /// the visitor walks contiguous <see cref="Span{T}"/>s rather than resolving one
    /// entity at a time. <typeparamref name="TVisitor"/> is a <c>struct</c> constrained to
    /// the interface and passed by <c>ref</c>, so the call devirtualises and nothing is
    /// boxed or allocated per chunk.</para>
    /// </summary>
    internal void VisitChunksLocked<TTag, TVisitor>(ref TVisitor visitor)
        where TTag : struct
        where TVisitor : struct, ISimChunkVisitor
    {
        _iterationDepth++;
        try
        {
            foreach (ref var chunk in _arch.Query(in TaggedQuery<TTag>.Description).GetChunkIterator())
            {
                var view = new SimChunk(
                    chunk.GetSpan<Position>(), chunk.GetSpan<Health>(),
                    chunk.GetSpan<Locomotion>(), chunk.Count);
                visitor.Visit(in view);
            }
        }
        finally { _iterationDepth--; }
    }

    /// <summary>
    /// A reference to the single entity carrying <typeparamref name="T"/>, creating it if
    /// it does not exist. Caller holds the write lock.
    ///
    /// <para>This is where per-system state belongs. A system that keeps a counter in a
    /// private field owns simulation state the world cannot see: it cannot be snapshotted,
    /// saved, or reset with the world, and two instances of the system would silently
    /// disagree. Putting it on an entity makes it ordinary world data.</para>
    ///
    /// <para>The entity carries <b>only</b> this component, so it matches none of the
    /// queries that require the seven standard ones — it can never appear in a snapshot,
    /// an AOI scan, or the persistence sweep.</para>
    ///
    /// <para>The returned <c>ref</c> points into chunk storage and is invalidated by any
    /// structural change. Re-take it after one.</para>
    /// </summary>
    internal ref T SingletonLocked<T>() where T : struct
    {
        foreach (ref var chunk in _arch.Query(in SingletonQuery<T>.Description).GetChunkIterator())
        {
            if (chunk.Count > 0) return ref chunk.GetSpan<T>()[0];
        }

        Entity created = _arch.Create(default(T));
        return ref _arch.Get<T>(created);
    }

    private static class SingletonQuery<T> where T : struct
    {
        public static readonly QueryDescription Description = new QueryDescription().WithAll<T>();
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

    /// <summary>
    /// A deferred structural change. Only created when <see cref="DeferStructural"/>.
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
        int total = 0;
        for (int s = 0; s < _structuralSlots.Length; s++) total += _structuralSlots[s].Count;
        Volatile.Write(ref _queuedStructuralOps, 0);
        if (total == 0) return;

        // Copy and clear first: applying an op must not observe the queue it is draining.
        // Both deferral conditions are false here -- the write lock is held, so no
        // parallel region is open, and nothing is iterating -- so the replayed ops take
        // the immediate path rather than re-queueing themselves.
        //
        // Slots are walked in index order, and each slot in insertion order. That, and
        // not the order the workers finished in, is what makes the replay deterministic.
        var ops = new StructuralOp[total];
        int n = 0;
        for (int s = 0; s < _structuralSlots.Length; s++)
        {
            List<StructuralOp> slot = _structuralSlots[s];
            for (int i = 0; i < slot.Count; i++) ops[n++] = slot[i];
            slot.Clear();
        }

        foreach (var op in ops)
        {
            if (op.IsRemoval) RemoveEntityLocked(op.Id);
            else AddEntityLocked(op.State, op.Tags);
        }
    }

    private void AddEntityLocked(EntityState state) => AddEntityLocked(state, EntityTags.None);

    private void AddEntityLocked(EntityState state, EntityTags tags)
    {
        if (DeferStructural)
        {
            _structuralSlots[_workerSlot].Add(StructuralOp.Add(state, tags));
            Interlocked.Increment(ref _queuedStructuralOps);
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

        // Assigned once per distinct id string, for the life of the world — see _stableIds.
        if (!_stableIds.TryGetValue(state.Id, out int stable))
        {
            stable = _nextStableId++;
            _stableIds[state.Id] = stable;
        }

        Entity entity = isEnemy
            ? _arch.Create(
                new EntityIdRef(state.Id, stable),
                new EntityKind(state.Type),
                new Position(state.Position),
                default(Health),
                default(Combat),
                new Locomotion(state.Speed),
                new InputCursor(state.LastInputTick),
                default(EnemyAi))
            : isPlayer
            ? _arch.Create(
                new EntityIdRef(state.Id, stable),
                new EntityKind(state.Type),
                new Position(state.Position),
                default(Health),
                default(Combat),
                new Locomotion(state.Speed),
                new InputCursor(state.LastInputTick),
                default(PlayerTag))
            : _arch.Create(
                new EntityIdRef(state.Id, stable),
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
        if (DeferStructural)
        {
            _structuralSlots[_workerSlot].Add(StructuralOp.Remove(id));
            Interlocked.Increment(ref _queuedStructuralOps);
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
