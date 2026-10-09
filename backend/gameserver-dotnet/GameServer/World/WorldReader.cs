using System;
using GameServer.World.Components;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.World;

/// <summary>
/// Read access to <see cref="EcsWorld"/> for the duration of one
/// <see cref="EcsWorld.ReadAll"/> scope, with the world read lock already held.
///
/// <para><b>Why this exists.</b> The snapshot broadcast took the read lock twice per
/// connected client per tick — once for the AOI anchor, once for the AOI scan — so a
/// 200-player tick acquired it 400 times. Every one of those was a separate chance for a
/// writer to interleave, which is not a property anything wanted; the broadcast is
/// supposed to see one consistent world, and it happened to because nothing writes from
/// the tick thread during it, not because it was arranged.</para>
///
/// <para>This makes it one acquisition and one consistent view for the whole broadcast.
/// The trade is real and worth stating: a join or leave arriving mid-broadcast now waits
/// for the whole gather rather than slipping between two viewers. The gather is position
/// tests over chunk spans with no serialization in it, which is precisely why
/// serialization was moved out of the locked phase rather than left inside it.</para>
/// </summary>
public sealed class WorldReader
{
    private readonly EcsWorld _world;

    internal WorldReader(EcsWorld world) => _world = world;

    /// <summary>
    /// The two fields a viewer needs about its own entity: the AOI centre and the input
    /// tick to acknowledge. False when the entity is gone, leaving the defaults — the
    /// same anchor the previous code used in that case.
    /// </summary>
    public bool TryGetSnapshotAnchor(string userId, out Vec2 position, out ulong lastInputTick)
    {
        position = default;
        lastInputTick = 0;

        EntityHandle handle = _world.ResolveLocked(userId);
        if (!handle.IsValid) return false;

        position = _world.ArchInternal.Get<Position>(handle.Value).Value;
        lastInputTick = _world.ArchInternal.Get<InputCursor>(handle.Value).LastInputTick;
        return true;
    }

    /// <summary>
    /// <see cref="TryGetSnapshotAnchor(string, out Vec2, out ulong)"/> plus the base tick that
    /// applied the acknowledged input (<c>ack_applied_tick</c>). Both ticks are read from the
    /// one cursor in the one lookup, so they always describe the same input.
    /// </summary>
    public bool TryGetSnapshotAnchor(
        string userId, out Vec2 position, out ulong lastInputTick, out ulong lastInputAppliedTick)
    {
        position = default;
        lastInputTick = 0;
        lastInputAppliedTick = 0;

        EntityHandle handle = _world.ResolveLocked(userId);
        if (!handle.IsValid) return false;

        position = _world.ArchInternal.Get<Position>(handle.Value).Value;
        ref readonly InputCursor cursor = ref _world.ArchInternal.Get<InputCursor>(handle.Value);
        lastInputTick = cursor.LastInputTick;
        lastInputAppliedTick = cursor.LastInputAppliedTick;
        return true;
    }

    /// <summary>
    /// The world-stable key of <paramref name="userId"/>'s entity, if it has one.
    /// </summary>
    /// <remarks>
    /// Exists so a connection can recognise events addressed to its own entity without
    /// comparing id strings per event per tick. See
    /// <see cref="Components.EntityIdRef.Stable"/> for why the key is interchangeable with
    /// the id: it is assigned once per id string for the life of the world and never
    /// reused, so a despawn and respawn of the same player resolves to the same key.
    /// </remarks>
    public bool TryGetStableKey(string userId, out int stable)
    {
        stable = 0;

        EntityHandle handle = _world.ResolveLocked(userId);
        if (!handle.IsValid) return false;

        stable = _world.ArchInternal.Get<EntityIdRef>(handle.Value).Stable;
        return true;
    }

    /// <summary>
    /// Fill <paramref name="destination"/> with the entities within
    /// <paramref name="radius"/> of <paramref name="center"/>.
    ///
    /// <para>Same count-don't-saturate contract as everywhere else in this server and as
    /// <c>AoiLogic.GetNearbyEntities</c>: the return value is the total match count and
    /// may exceed the buffer, so the caller resizes and retries once rather than
    /// silently sending a truncated view of the world.</para>
    /// </summary>
    public int GetEntitiesInRange(Vec2 center, float radius, Span<EntityState> destination) =>
        _world.ScanRangeLockedForReader(center, radius, destination);

    /// <summary>
    /// Trimmed snapshot-view form of the scan — the one the gather uses (issue #237):
    /// composes only the seven fields the snapshot encoder consumes, plus the stable
    /// key the delta state maps are keyed on. Same order, same predicate, same
    /// count-don't-saturate contract as the <see cref="EntityState"/> overload.
    /// </summary>
    public int GetEntitiesInRange(Vec2 center, float radius, Span<EntityView> destination) =>
        _world.ScanRangeViewsLockedForReader(center, radius, destination);

    // ── Protocol 3 data (ADR-28..30) ─────────────────────────────────────────
    //
    // Everything below is a pure read of the world's GameplayState under the read lock this
    // scope already holds, and allocates nothing. Keys are EntityView.Key (the world-stable
    // key). Scalar v3 fields (z, velocity, owner, spawn_seq, versions) are already on the
    // EntityView; these are the variable-length parts.

    /// <summary>
    /// The viewer's own anchor in 3D: <see cref="TryGetSnapshotAnchor"/> plus the height.
    /// </summary>
    public bool TryGetSnapshotAnchor(string userId, out Vec2 position, out float z, out ulong lastInputTick)
    {
        z = 0f;
        if (!TryGetSnapshotAnchor(userId, out position, out lastInputTick)) return false;

        EntityHandle handle = _world.ResolveLocked(userId);
        z = _world.ArchInternal.Get<Position>(handle.Value).Z;
        return true;
    }

    /// <summary>
    /// Number of stats in the entity's replicated stat block (0 for entities without one:
    /// projectiles, items, unknown keys). Equal to <see cref="GameplayState.StatIds"/>' length
    /// for every actor, so one buffer of that size always suffices.
    /// </summary>
    public int StatCount(int key)
    {
        SimRecord? rec = _world.Gameplay.Get(key);
        return rec is { Kind: SimKind.Actor, StatValues: { } v } ? v.Length : 0;
    }

    /// <summary>
    /// Write the entity's stat block — <c>(stat id, EFFECTIVE value)</c>, ordered by stat id —
    /// into <paramref name="destination"/>. Returns the number of stats the entity has, which
    /// may exceed the buffer (count-don't-saturate, as everywhere in this reader).
    /// </summary>
    /// <remarks>
    /// Effective means status modifiers on content stats are applied (ADR-30); with no such
    /// status the value is the base value. A changed result is always accompanied by a
    /// changed <see cref="EntityView.StatsVersion"/>.
    /// </remarks>
    public int CopyStats(int key, Span<StatValueData> destination)
    {
        SimRecord? rec = _world.Gameplay.Get(key);
        if (rec is not { Kind: SimKind.Actor, StatValues: { } values }) return 0;

        uint[] ids = _world.Gameplay.StatIds;
        int n = Math.Min(values.Length, ids.Length);
        StatusSet? statuses = rec.Statuses;
        bool modified = statuses is { Count: > 0 };
        for (int i = 0; i < n && i < destination.Length; i++)
        {
            int v = modified ? statuses!.EffectiveStat(ids[i], values[i]) : values[i];
            destination[i] = new StatValueData(ids[i], v);
        }

        return n;
    }

    /// <summary>Number of active statuses on the entity (0 when it has none or no record).</summary>
    public int StatusCount(int key)
    {
        SimRecord? rec = _world.Gameplay.Get(key);
        return rec is { Kind: SimKind.Actor, Statuses: { } s } ? s.Count : 0;
    }

    /// <summary>
    /// Write the entity's active statuses as snapshot entries, in application order. Returns
    /// the number of statuses, which may exceed the buffer. At most
    /// <see cref="StatusSet.DefaultCapacity"/> per entity.
    /// </summary>
    public int CopyStatuses(int key, Span<StatusEffectData> destination)
    {
        SimRecord? rec = _world.Gameplay.Get(key);
        if (rec is not { Kind: SimKind.Actor, Statuses: { } statuses }) return 0;

        statuses.CopyTo(destination);
        return statuses.Count;
    }

    /// <summary>
    /// Item payload of a dropped-item entity (wire type <c>item</c>): content item id,
    /// quantity and despawn tick. False for anything that is not a live dropped item.
    /// </summary>
    public bool TryGetItemDrop(int key, out string itemId, out int quantity, out ulong despawnTick)
    {
        SimRecord? rec = _world.Gameplay.Get(key);
        if (rec is { Kind: SimKind.Item, ItemId: { } id })
        {
            itemId = id;
            quantity = rec.ItemQuantity;
            despawnTick = rec.DespawnTick;
            return true;
        }

        itemId = string.Empty;
        quantity = 0;
        despawnTick = 0;
        return false;
    }
}
