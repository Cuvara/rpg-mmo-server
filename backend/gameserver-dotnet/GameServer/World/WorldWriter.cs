using Arch.Core;
using GameServer.World.Components;
using Shared.GameLogic.Components;

namespace GameServer.World;

/// <summary>
/// An entity resolved to its storage location, valid only inside the
/// <see cref="EcsWorld.UpdateComponents"/> scope that produced it.
///
/// <para>This is the first step of ADR-10's "integer simulation handle": the string id
/// is paid for <b>once</b>, at resolve time, and every subsequent component touch in
/// that scope goes through the handle. It does not yet replace the string id on the
/// wire, in persistence, or in <see cref="EntityState.Id"/> — those are separate
/// migrations.</para>
///
/// <para>A handle must not be stored across scopes: a structural change (spawn,
/// despawn, archetype move) can move the entity's slot, and nothing here revalidates
/// it. Resolve again in the next scope.</para>
/// </summary>
public readonly struct EntityHandle
{
    internal readonly Entity Value;
    private readonly bool _valid;

    internal EntityHandle(Entity value)
    {
        Value = value;
        _valid = true;
    }

    /// <summary>True when the id resolved to a live entity.</summary>
    public bool IsValid => _valid;

    /// <summary>
    /// True when both handles denote the same entity. Systems that resolve two ids and
    /// then write through both need this: with <c>ref</c> component access, aliasing is
    /// no longer hidden behind a copy-then-write-back the way it was with
    /// <c>get</c>/<c>set</c>.
    /// </summary>
    public bool SameAs(in EntityHandle other) => _valid && other._valid && Value == other.Value;
}

/// <summary>
/// Component-level write access to <see cref="EcsWorld"/>, handed to the callback of
/// <see cref="EcsWorld.UpdateComponents"/> while the world write lock is held.
///
/// <para><b>Why this exists.</b> The older <c>Update(get, set)</c> pair addresses the
/// world as whole <see cref="EntityState"/> values: <c>get</c> reads all seven
/// components and materialises a struct, <c>set</c> writes all seven back — including
/// re-storing the <see cref="EntityIdRef"/> and <see cref="EntityKind"/> string
/// references, which never change after spawn but still cost a GC write barrier every
/// time. For per-tick input that is fourteen component lookups and two managed stores
/// to move a position and bump an input cursor.</para>
///
/// <para>This type exposes <c>ref</c> access to the individual components instead, so a
/// system reads what it reads and writes what it writes. It is a class, not a
/// <c>ref struct</c>, so it can be passed to the <see cref="System.Action{T}"/> the
/// lock scope is built from; <see cref="EcsWorld"/> keeps one instance per world, so
/// entering a scope allocates nothing.</para>
///
/// <para><b>Boundary unchanged.</b> No <c>Arch.Core</c> type is public here —
/// <see cref="EntityHandle"/> wraps <c>Entity</c> — and <c>Shared.GameLogic</c> still
/// never sees the ECS.</para>
/// </summary>
public sealed class WorldWriter
{
    private readonly EcsWorld _world;

    internal WorldWriter(EcsWorld world) => _world = world;

    /// <summary>
    /// Resolve an entity id to a handle. This is the only string-keyed lookup a system
    /// should perform per entity per scope. An unknown or dead id yields an invalid
    /// handle rather than null, so callers branch on <see cref="EntityHandle.IsValid"/>
    /// instead of unwrapping a nullable struct.
    /// </summary>
    public EntityHandle Resolve(string id) => _world.ResolveLocked(id);

    /// <summary>World-space position of the entity.</summary>
    public ref Position PositionOf(in EntityHandle handle) => ref _world.ArchInternal.Get<Position>(handle.Value);

    /// <summary>Hit points and liveness of the entity.</summary>
    public ref Health HealthOf(in EntityHandle handle) => ref _world.ArchInternal.Get<Health>(handle.Value);

    /// <summary>Offensive/defensive stats and attack cooldown of the entity.</summary>
    public ref Combat CombatOf(in EntityHandle handle) => ref _world.ArchInternal.Get<Combat>(handle.Value);

    /// <summary>Movement capability of the entity.</summary>
    public ref Locomotion LocomotionOf(in EntityHandle handle) => ref _world.ArchInternal.Get<Locomotion>(handle.Value);

    /// <summary>Newest accepted input tick of the entity.</summary>
    public ref InputCursor InputCursorOf(in EntityHandle handle) => ref _world.ArchInternal.Get<InputCursor>(handle.Value);

    /// <summary>
    /// Materialise a full <see cref="EntityState"/> for the entity.
    ///
    /// <para>This is the round-trip the rest of the migration is meant to delete, and it
    /// is kept only where a <c>Shared.GameLogic</c> entry point demands a whole
    /// <c>EntityState</c> (combat validation, the death callback). Reading it is cheap
    /// enough; what it must never be paired with is a whole-struct write-back — write
    /// the individual components instead.</para>
    /// </summary>
    public EntityState Compose(in EntityHandle handle) => _world.ComposeLocked(handle.Value);
}
