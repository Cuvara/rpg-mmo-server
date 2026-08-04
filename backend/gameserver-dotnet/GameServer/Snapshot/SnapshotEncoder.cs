using Shared.GameLogic.Components;
using GameServer.Net;
using GameServer.World;

namespace GameServer.Snapshot;

/// <summary>
/// Encodes world state into snapshot messages for clients.
/// Port of Go snapshot/encoder.go + snapshot/aoi.go.
/// </summary>
public static class SnapshotEncoder
{
    /// <summary>Encode a list of entities into a wire snapshot message.</summary>
    public static SnapshotMessage Encode(ulong tick, List<EntityState> entities)
    {
        var msg = new SnapshotMessage
        {
            Tick = tick,
            Entities = new List<EntitySnapshotMsg>(entities.Count)
        };

        foreach (var e in entities)
        {
            msg.Entities.Add(new EntitySnapshotMsg
            {
                Id = e.Id,
                Type = e.Type,
                X = e.Position.X,
                Y = e.Position.Y,
                Hp = e.Hp,
                MaxHp = e.MaxHp
            });
        }

        return msg;
    }

    /// <summary>Get entities within AOI radius of a center point.</summary>
    public static List<EntityState> GetNearbyEntities(GameWorld world, Vec2 center, float radius)
    {
        return world.GetEntitiesInRange(center, radius);
    }
}
