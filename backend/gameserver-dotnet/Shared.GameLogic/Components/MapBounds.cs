using System;

namespace Shared.GameLogic.Components
{
    /// <summary>
    /// Axis-aligned rectangular play area for a map. Positions are clamped to this
    /// rectangle after every movement integration step, so an entity can never leave
    /// the map regardless of input.
    /// </summary>
    /// <remarks>
    /// Pure data + math, no server dependency: the Unity client uses the identical
    /// bounds when predicting local movement, so prediction and the authoritative
    /// server produce the same clamped result at the map edge.
    /// </remarks>
    public readonly struct MapBounds : IEquatable<MapBounds>
    {
        /// <summary>Inclusive minimum X coordinate.</summary>
        public readonly float MinX;

        /// <summary>Inclusive minimum Y coordinate.</summary>
        public readonly float MinY;

        /// <summary>Inclusive maximum X coordinate.</summary>
        public readonly float MaxX;

        /// <summary>Inclusive maximum Y coordinate.</summary>
        public readonly float MaxY;

        /// <summary>
        /// Create bounds from explicit edges. Edges are normalized so that
        /// <see cref="MinX"/> &lt;= <see cref="MaxX"/> and <see cref="MinY"/> &lt;= <see cref="MaxY"/>.
        /// </summary>
        public MapBounds(float minX, float minY, float maxX, float maxY)
        {
            MinX = MathF.Min(minX, maxX);
            MaxX = MathF.Max(minX, maxX);
            MinY = MathF.Min(minY, maxY);
            MaxY = MathF.Max(minY, maxY);
        }

        /// <summary>
        /// Create bounds of <paramref name="width"/> x <paramref name="height"/> world units
        /// centered on the origin. Players spawn at (0,0), so a centered rectangle gives
        /// equal room in every direction.
        /// </summary>
        public static MapBounds FromSize(float width, float height)
        {
            float halfW = MathF.Abs(width) * 0.5f;
            float halfH = MathF.Abs(height) * 0.5f;
            return new MapBounds(-halfW, -halfH, halfW, halfH);
        }

        /// <summary>
        /// Default play area: <see cref="GameConstants.DefaultMapWidth"/> x
        /// <see cref="GameConstants.DefaultMapHeight"/> world units centered on the origin.
        /// </summary>
        public static MapBounds Default =>
            FromSize(GameConstants.DefaultMapWidth, GameConstants.DefaultMapHeight);

        /// <summary>Width of the play area in world units.</summary>
        public float Width => MaxX - MinX;

        /// <summary>Height of the play area in world units.</summary>
        public float Height => MaxY - MinY;

        /// <summary>True when the position lies inside (or exactly on) the bounds.</summary>
        public bool Contains(in Vec2 position) =>
            position.X >= MinX && position.X <= MaxX &&
            position.Y >= MinY && position.Y <= MaxY;

        /// <summary>
        /// Clamp a position into the bounds. Each axis is clamped independently, so an
        /// entity sliding along an edge keeps its tangential movement (no full stop at walls).
        /// </summary>
        public Vec2 Clamp(in Vec2 position)
        {
            float x = position.X < MinX ? MinX : (position.X > MaxX ? MaxX : position.X);
            float y = position.Y < MinY ? MinY : (position.Y > MaxY ? MaxY : position.Y);
            return new Vec2(x, y);
        }

        public bool Equals(MapBounds other) =>
            MinX == other.MinX && MinY == other.MinY && MaxX == other.MaxX && MaxY == other.MaxY;

        public override bool Equals(object? obj) => obj is MapBounds other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(MinX, MinY, MaxX, MaxY);

        public override string ToString() => $"[({MinX:F1}, {MinY:F1}) .. ({MaxX:F1}, {MaxY:F1})]";
    }
}
