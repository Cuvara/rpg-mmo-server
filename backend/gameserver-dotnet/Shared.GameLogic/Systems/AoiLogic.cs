using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems;

/// <summary>
/// Area of Interest logic. Filters entities by proximity and builds snapshots.
/// Ported from Go AOI package.
/// </summary>
public static class AoiLogic
{
    /// <summary>
    /// Filter entities within <paramref name="radius"/> of <paramref name="center"/>.
    /// Uses squared distance to avoid sqrt in the hot path.
    /// </summary>
    public static List<EntityState> GetNearbyEntities(
        IEnumerable<EntityState> allEntities,
        in Vec2 center,
        float radius)
    {
        float radiusSq = radius * radius;
        var result = new List<EntityState>();

        foreach (EntityState entity in allEntities)
        {
            if (Vec2.DistanceSq(center, entity.Position) <= radiusSq)
            {
                result.Add(entity);
            }
        }

        return result;
    }

    /// <summary>
    /// Build a <see cref="SnapshotData"/> from a tick number and entity list.
    /// </summary>
    public static SnapshotData EncodeSnapshot(ulong tick, IReadOnlyList<EntityState> entities)
    {
        var snapshots = new EntitySnapshotData[entities.Count];

        for (int i = 0; i < entities.Count; i++)
        {
            EntityState e = entities[i];
            snapshots[i] = new EntitySnapshotData(
                id: e.Id,
                type: e.Type,
                x: e.Position.X,
                y: e.Position.Y,
                hp: e.Hp,
                maxHp: e.MaxHp
            );
        }

        return new SnapshotData(tick, snapshots);
    }
}
