using System;

namespace Shared.GameLogic.World
{
    /// <summary>
    /// Terrain as a regular grid of height samples, read with bilinear interpolation
    /// (ADR-28 decision 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sample <c>(col, row)</c> sits at world <c>(OriginX + col * CellSize,
    /// OriginY + row * CellSize)</c> and is stored at <c>Heights[row * Columns + col]</c>
    /// (row-major, rows along +Y). A grid is used rather than a triangle mesh because a
    /// grid lookup is O(1) with no search structure, and bilinear height is a handful of
    /// multiply-adds that two runtimes can be made to agree on to the bit.
    /// </para>
    /// <para>
    /// Outside the grid the height is 0, which is the protocol 2 world. A map that only
    /// covers part of its bounds with terrain is therefore flat everywhere else rather than
    /// extended by its edge samples — the exporter is expected to cover the whole playable
    /// area when it means to.
    /// </para>
    /// <para>
    /// Immutable: the constructor copies the samples, so a loader cannot change terrain
    /// under a running simulation by keeping a reference to its array.
    /// </para>
    /// </remarks>
    public sealed class HeightField
    {
        private readonly float[] _heights;

        /// <summary>World X of sample column 0.</summary>
        public float OriginX { get; }

        /// <summary>World Y of sample row 0.</summary>
        public float OriginY { get; }

        /// <summary>Distance between adjacent samples on both axes, in world units.</summary>
        public float CellSize { get; }

        /// <summary>Number of samples along X (at least 2 for a valid field).</summary>
        public int Columns { get; }

        /// <summary>Number of samples along Y (at least 2 for a valid field).</summary>
        public int Rows { get; }

        /// <summary>Row-major samples; see the type remarks for the layout.</summary>
        public ReadOnlySpan<float> Heights => _heights;

        /// <summary>
        /// Create a height field. The array is copied. Structural problems (too few
        /// samples, a non-positive cell size, a non-finite height) are not thrown here —
        /// they are reported by <see cref="MapGeometryValidation"/> together with every
        /// other problem in the map, so one restart fixes all of them.
        /// </summary>
        public HeightField(float originX, float originY, float cellSize, int columns, int rows, float[] heights)
        {
            if (heights == null) throw new ArgumentNullException(nameof(heights));
            OriginX = originX;
            OriginY = originY;
            CellSize = cellSize;
            Columns = columns;
            Rows = rows;
            _heights = (float[])heights.Clone();
        }

        /// <summary>World X of the last sample column.</summary>
        public float MaxX => (float)(OriginX + (float)((Columns - 1) * CellSize));

        /// <summary>World Y of the last sample row.</summary>
        public float MaxY => (float)(OriginY + (float)((Rows - 1) * CellSize));

        /// <summary>
        /// True when the structure is usable for <see cref="Sample"/>. A field that fails
        /// this always samples 0; validation reports why.
        /// </summary>
        public bool IsUsable =>
            Columns >= 2 && Rows >= 2 && CellSize > 0f && float.IsFinite(CellSize) &&
            (long)Columns * Rows == _heights.Length;

        /// <summary>
        /// Bilinear height at world <c>(x, y)</c>; 0 outside the grid or when the field is
        /// not usable.
        /// </summary>
        /// <remarks>
        /// Every intermediate is rounded to float explicitly (see <c>Vec2.SqrMagnitude</c>).
        /// The cell index comes from truncating a non-negative float, which is floor — no
        /// <c>MathF.Floor</c> call, nothing a runtime could implement differently.
        /// </remarks>
        public float Sample(float x, float y)
        {
            if (!IsUsable || !float.IsFinite(x) || !float.IsFinite(y)) return 0f;
            if (x < OriginX || y < OriginY || x > MaxX || y > MaxY) return 0f;

            float fx = (float)((float)(x - OriginX) / CellSize);
            float fy = (float)((float)(y - OriginY) / CellSize);

            int col = (int)fx;
            int row = (int)fy;
            // The far edge lands exactly on the last sample; use the last cell with t = 1
            // there instead of reading one past the end.
            if (col > Columns - 2) col = Columns - 2;
            if (row > Rows - 2) row = Rows - 2;

            float tx = (float)(fx - col);
            float ty = (float)(fy - row);

            int i00 = row * Columns + col;
            float h00 = _heights[i00];
            float h10 = _heights[i00 + 1];
            float h01 = _heights[i00 + Columns];
            float h11 = _heights[i00 + Columns + 1];

            float a = Lerp(h00, h10, tx);
            float b = Lerp(h01, h11, tx);
            return Lerp(a, b, ty);
        }

        private static float Lerp(float a, float b, float t)
        {
            float d = (float)((float)(b - a) * t);
            return (float)(a + d);
        }
    }
}
