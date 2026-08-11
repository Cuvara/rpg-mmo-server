using Shared.GameLogic.Components;
using GameServer.Net;
using GameServer.World;
using RpgMmo.Wire.V1;

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
        var msg = new SnapshotMessage { Tick = tick };

        foreach (var e in entities)
        {
            var ent = new EntitySnapshot
            {
                Id = e.Id,
                X = e.Position.X,
                Y = e.Position.Y,
                Hp = e.Hp,
                MaxHp = e.MaxHp
            };
            EntityTypes.SetType(ent, e.Type);
            msg.Entities.Add(ent);
        }

        return msg;
    }

    /// <summary>Get entities within AOI radius of a center point.</summary>
    public static List<EntityState> GetNearbyEntities(EcsWorld world, Vec2 center, float radius)
    {
        return world.GetEntitiesInRange(center, radius);
    }
}
