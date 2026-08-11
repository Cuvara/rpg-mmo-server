using System;

namespace Shared.GameLogic.Components
{
    /// <summary>
    /// Lightweight 2D vector. No Unity dependency — uses only System.*.
    /// Matches the server's float32 X/Y coordinate system.
    /// </summary>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public readonly float X;
        public readonly float Y;

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        // --- Static constants ---

        public static readonly Vec2 Zero = new(0f, 0f);
        public static readonly Vec2 One = new(1f, 1f);
        public static readonly Vec2 Up = new(0f, 1f);
        public static readonly Vec2 Down = new(0f, -1f);
        public static readonly Vec2 Left = new(-1f, 0f);
        public static readonly Vec2 Right = new(1f, 0f);

        // --- Properties ---

        /// <summary>
        /// Squared magnitude (avoids sqrt).
        /// </summary>
        /// <remarks>
        /// Each intermediate is cast to <c>float</c> explicitly. C# permits a
        /// float expression to be evaluated at higher precision (ECMA-334
        /// §11.3.7), and the two runtimes this code has to agree on take
        /// different options: .NET 10's RyuJIT evaluates strictly in float32,
        /// while Unity's Editor Mono JIT keeps double-precision intermediates
        /// and rounds once at the end. Written as <c>X * X + Y * Y</c> the two
        /// disagreed by one ULP, which the golden vectors caught.
        /// Do not "simplify" this back to a single expression.
        /// </remarks>
        public float SqrMagnitude => (float)((float)(X * X) + (float)(Y * Y));

        /// <summary>Magnitude (length) of the vector.</summary>
        public float Magnitude => MathF.Sqrt(SqrMagnitude);

        /// <summary>Unit vector in the same direction. Returns Zero if magnitude is near zero.</summary>
        public Vec2 Normalized
        {
            get
            {
                float mag = Magnitude;
                return mag > 1e-6f ? new Vec2(X / mag, Y / mag) : Zero;
            }
        }

        // --- Static distance helpers ---

        /// <summary>Squared Euclidean distance (no sqrt — use for comparisons).</summary>
        public static float DistanceSq(in Vec2 a, in Vec2 b)
        {
            // Per-operation float casts, for the reason given on SqrMagnitude.
            float dx = (float)(a.X - b.X);
            float dy = (float)(a.Y - b.Y);
            return (float)((float)(dx * dx) + (float)(dy * dy));
        }

        /// <summary>Euclidean distance between two points.</summary>
        public static float Distance(in Vec2 a, in Vec2 b) => MathF.Sqrt(DistanceSq(a, b));

        // --- Operators ---

        public static Vec2 operator +(in Vec2 a, in Vec2 b) => new(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(in Vec2 a, in Vec2 b) => new(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(in Vec2 v, float s) => new(v.X * s, v.Y * s);
        public static Vec2 operator *(float s, in Vec2 v) => new(v.X * s, v.Y * s);

        public static bool operator ==(in Vec2 a, in Vec2 b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(in Vec2 a, in Vec2 b) => !(a == b);

        // --- Equality ---

        public bool Equals(Vec2 other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Vec2 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public override string ToString() => $"({X:F2}, {Y:F2})";
    }
}
