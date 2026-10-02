using System;
using Shared.GameLogic.Components;

namespace Shared.GameLogic.World
{
    /// <summary>
    /// Static axis-aligned box collider: walls, crates, ledges, platforms (ADR-28 decision 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Axis-aligned only. Rotated and moving colliders are out of scope until content needs
    /// them; an AABB keeps every overlap and sweep test to comparisons and a few divisions,
    /// which is what makes server and client agree on a hit to the bit.
    /// </para>
    /// <para>
    /// Overlap tests are <b>strict</b> (touching is not overlapping). That is load-bearing:
    /// a character standing on a box has its feet exactly at <see cref="MaxZ"/>, and a
    /// non-strict test would call that a collision and block every step it takes.
    /// </para>
    /// </remarks>
    public readonly struct StaticBox : IEquatable<StaticBox>
    {
        /// <summary>Minimum X edge.</summary>
        public readonly float MinX;

        /// <summary>Minimum Y edge.</summary>
        public readonly float MinY;

        /// <summary>Bottom of the box.</summary>
        public readonly float MinZ;

        /// <summary>Maximum X edge.</summary>
        public readonly float MaxX;

        /// <summary>Maximum Y edge.</summary>
        public readonly float MaxY;

        /// <summary>Top of the box — the surface a character stands on.</summary>
        public readonly float MaxZ;

        /// <summary>
        /// Create a box from two opposite corners. Edges are normalized per axis so
        /// <c>Min &lt;= Max</c>, as <see cref="MapBounds"/> does, so an exporter that wrote
        /// the corners in the other order still produces the intended box.
        /// </summary>
        public StaticBox(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            MinX = MathF.Min(minX, maxX);
            MaxX = MathF.Max(minX, maxX);
            MinY = MathF.Min(minY, maxY);
            MaxY = MathF.Max(minY, maxY);
            MinZ = MathF.Min(minZ, maxZ);
            MaxZ = MathF.Max(minZ, maxZ);
        }

        /// <summary>True when every edge is a finite number.</summary>
        public bool IsFinite =>
            float.IsFinite(MinX) && float.IsFinite(MinY) && float.IsFinite(MinZ) &&
            float.IsFinite(MaxX) && float.IsFinite(MaxY) && float.IsFinite(MaxZ);

        /// <summary>True when the point lies strictly inside the box.</summary>
        public bool ContainsStrict(in Vec3 p) =>
            p.X > MinX && p.X < MaxX && p.Y > MinY && p.Y < MaxY && p.Z > MinZ && p.Z < MaxZ;

        /// <summary>
        /// True when a ground-plane circle of <paramref name="radius"/> at <c>(x, y)</c>
        /// strictly overlaps the box's footprint.
        /// </summary>
        public bool FootprintOverlaps(float x, float y, float radius)
        {
            float cx = x < MinX ? MinX : (x > MaxX ? MaxX : x);
            float cy = y < MinY ? MinY : (y > MaxY ? MaxY : y);
            float dx = (float)(x - cx);
            float dy = (float)(y - cy);
            float distSq = (float)((float)(dx * dx) + (float)(dy * dy));
            return distSq < (float)(radius * radius);
        }

        /// <summary>
        /// True when an upright character capsule whose feet are at
        /// <paramref name="basePos"/> strictly overlaps the box.
        /// </summary>
        /// <remarks>
        /// The capsule is tested as its bounding vertical cylinder (radius, height). The
        /// rounded ends only matter at a box's top and bottom edges, where the motor's
        /// step-up and ceiling rules already decide the outcome, and the cylinder test is
        /// comparisons only — cheaper and with fewer roundings to keep in agreement.
        /// </remarks>
        public bool CapsuleOverlaps(in Vec3 basePos, float radius, float height)
        {
            float top = (float)(basePos.Z + height);
            if (!(MinZ < top && MaxZ > basePos.Z)) return false;
            return FootprintOverlaps(basePos.X, basePos.Y, radius);
        }

        /// <inheritdoc />
        public bool Equals(StaticBox other) =>
            MinX == other.MinX && MinY == other.MinY && MinZ == other.MinZ &&
            MaxX == other.MaxX && MaxY == other.MaxY && MaxZ == other.MaxZ;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is StaticBox other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(MinX, MinY, MinZ, MaxX, MaxY, MaxZ);

        /// <inheritdoc />
        public override string ToString() =>
            $"[({MinX:F1}, {MinY:F1}, {MinZ:F1}) .. ({MaxX:F1}, {MaxY:F1}, {MaxZ:F1})]";
    }
}
