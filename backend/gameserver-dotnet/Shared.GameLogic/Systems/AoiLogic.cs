using System;
using System.Collections.Generic;
using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// Area of Interest logic. Filters entities by proximity and builds snapshots.
    /// Ported from Go AOI package.
    /// </summary>
    public static class AoiLogic
    {
        /// <summary>
        /// Copy every entity within <paramref name="radius"/> of <paramref name="center"/>
        /// into <paramref name="destination"/>. Uses squared distance — no sqrt in the
        /// hot path — and allocates nothing: this runs once per entity per tick.
        /// </summary>
        /// <param name="allEntities">Candidate entities, scanned in order.</param>
        /// <param name="center">AOI centre.</param>
        /// <param name="radius">AOI radius in world units. Inclusive at the boundary.</param>
        /// <param name="destination">
        /// Caller-owned buffer that receives the matches, in source order.
        /// </param>
        /// <returns>
        /// The <b>total number of matches</b>, which may exceed
        /// <paramref name="destination"/>'s length.
        /// <para>
        /// <b>Overflow contract — count, do not saturate.</b> When the buffer is too
        /// small, the first <c>destination.Length</c> matches are written and the scan
        /// continues, so the return value is the size the buffer needed to be. The
        /// caller detects truncation with <c>count &gt; destination.Length</c> and can
        /// resize and call again, once, with a correct size. A saturating variant would
        /// return <c>destination.Length</c> and make "full" indistinguishable from
        /// "exactly full", which is silent AOI truncation — entities missing from a
        /// keyframe with no error anywhere.
        /// </para>
        /// </returns>
        public static int GetNearbyEntities(
            ReadOnlySpan<EntityState> allEntities,
            in Vec2 center,
            float radius,
            Span<EntityState> destination)
        {
            float radiusSq = radius * radius;
            int count = 0;

            for (int i = 0; i < allEntities.Length; i++)
            {
                if (Vec2.DistanceSq(center, allEntities[i].Position) <= radiusSq)
                {
                    if (count < destination.Length)
                    {
                        destination[count] = allEntities[i];
                    }
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// <see cref="GetNearbyEntities(ReadOnlySpan{EntityState}, in Vec2, float, Span{EntityState})"/>
        /// over an indexable collection, for callers whose storage is not contiguous.
        /// Same overflow contract: the return value is the total match count, which may
        /// exceed <paramref name="destination"/>'s length.
        /// </summary>
        public static int GetNearbyEntities(
            IReadOnlyList<EntityState> allEntities,
            in Vec2 center,
            float radius,
            Span<EntityState> destination)
        {
            float radiusSq = radius * radius;
            int count = 0;

            for (int i = 0; i < allEntities.Count; i++)
            {
                EntityState entity = allEntities[i];
                if (Vec2.DistanceSq(center, entity.Position) <= radiusSq)
                {
                    if (count < destination.Length)
                    {
                        destination[count] = entity;
                    }
                    count++;
                }
            }

            return count;
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
}
