using System;
using GameServer.World.Components;
using Shared.GameLogic.Components;

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
}
