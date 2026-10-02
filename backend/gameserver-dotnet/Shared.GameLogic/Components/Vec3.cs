using System;

namespace Shared.GameLogic.Components
{
    /// <summary>
    /// Lightweight 3D vector. No Unity dependency — uses only System.*.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Axis convention (ADR-28): <see cref="X"/>/<see cref="Y"/> are the ground plane with
    /// exactly the meaning they had in <see cref="Vec2"/> and on the protocol 2 wire, and
    /// <see cref="Z"/> is height. A renderer maps <c>(x, y, z)</c> to Unity's
    /// <c>(x, z, y)</c>. Keeping x/y is what makes the third axis additive: anything planar
    /// (AOI, the 2D movement path) keeps working on <see cref="XY"/>.
    /// </para>
    /// <para>
    /// Every arithmetic intermediate is cast to <c>float</c> explicitly, for the reason
    /// given on <see cref="Vec2.SqrMagnitude"/>: .NET 10 evaluates float expressions
    /// strictly in float32 while Unity's Mono JIT keeps wider intermediates, and a JIT may
    /// contract <c>a + b * c</c> into a single-rounding FMA. Either one moves a result by an
    /// ULP and breaks bit-exact prediction. Do not "simplify" the casts away.
    /// </para>
    /// </remarks>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        /// <summary>Ground-plane X (same meaning as <see cref="Vec2.X"/>).</summary>
        public readonly float X;

        /// <summary>Ground-plane Y (same meaning as <see cref="Vec2.Y"/>).</summary>
        public readonly float Y;

        /// <summary>Height above the ground plane's zero level.</summary>
        public readonly float Z;

        /// <summary>Create a vector from its three components.</summary>
        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>
        /// Lift a planar position to 3D at height <paramref name="z"/>. The protocol 2
        /// world is <c>FromXY(p, 0)</c> everywhere.
        /// </summary>
        public static Vec3 FromXY(in Vec2 xy, float z) => new Vec3(xy.X, xy.Y, z);

        // --- Static constants ---

        /// <summary>The zero vector.</summary>
        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);

        /// <summary>Unit vector pointing up (+Z).</summary>
        public static readonly Vec3 Up = new Vec3(0f, 0f, 1f);

        // --- Properties ---

        /// <summary>
        /// Ground-plane projection. AOI and anything else that ADR-28 keeps planar reads this.
        /// </summary>
        public Vec2 XY => new Vec2(X, Y);

        /// <summary>Squared magnitude (avoids sqrt). Per-operation casts; see type remarks.</summary>
        public float SqrMagnitude =>
            (float)((float)((float)(X * X) + (float)(Y * Y)) + (float)(Z * Z));

        /// <summary>Magnitude (length) of the vector.</summary>
        public float Magnitude => MathF.Sqrt(SqrMagnitude);

        /// <summary>
        /// Unit vector in the same direction. Returns <see cref="Zero"/> when the magnitude
        /// is at or below 1e-6, the same threshold <see cref="Vec2.Normalized"/> uses, so a
        /// caller never divides by a denormal and gets an infinity back.
        /// </summary>
        public Vec3 Normalized
        {
            get
            {
                float mag = Magnitude;
                return mag > 1e-6f ? new Vec3(X / mag, Y / mag, Z / mag) : Zero;
            }
        }

        // --- Static helpers ---

        /// <summary>Dot product, with each product rounded to float before the sum.</summary>
        public static float Dot(in Vec3 a, in Vec3 b) =>
            (float)((float)((float)(a.X * b.X) + (float)(a.Y * b.Y)) + (float)(a.Z * b.Z));

        /// <summary>Squared Euclidean distance (no sqrt — use for comparisons).</summary>
        public static float DistanceSq(in Vec3 a, in Vec3 b)
        {
            float dx = (float)(a.X - b.X);
            float dy = (float)(a.Y - b.Y);
            float dz = (float)(a.Z - b.Z);
            return (float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz));
        }

        /// <summary>Euclidean distance between two points.</summary>
        public static float Distance(in Vec3 a, in Vec3 b) => MathF.Sqrt(DistanceSq(a, b));

        /// <summary>
        /// Point at parameter <paramref name="t"/> along <c>a → b</c>:
        /// <c>a + (b - a) * t</c>. Not clamped; <c>t</c> outside [0, 1] extrapolates.
        /// </summary>
        /// <remarks>
        /// Written as difference, multiply, add — each rounded — rather than
        /// <c>a * (1 - t) + b * t</c>: the form here returns exactly <c>a</c> at t = 0,
        /// which is what lets a rewind at alpha 0 reproduce the recorded tick bit for bit.
        /// </remarks>
        public static Vec3 Lerp(in Vec3 a, in Vec3 b, float t)
        {
            float dx = (float)((float)(b.X - a.X) * t);
            float dy = (float)((float)(b.Y - a.Y) * t);
            float dz = (float)((float)(b.Z - a.Z) * t);
            return new Vec3((float)(a.X + dx), (float)(a.Y + dy), (float)(a.Z + dz));
        }

        /// <summary>
        /// <c>origin + direction * scale</c> with the multiply rounded before the add, so
        /// the JIT cannot contract it into an FMA (see <c>MovementSystem.Integrate</c>).
        /// </summary>
        public static Vec3 AddScaled(in Vec3 origin, in Vec3 direction, float scale)
        {
            float dx = (float)(direction.X * scale);
            float dy = (float)(direction.Y * scale);
            float dz = (float)(direction.Z * scale);
            return new Vec3((float)(origin.X + dx), (float)(origin.Y + dy), (float)(origin.Z + dz));
        }

        /// <summary>True when every component is a finite number (no NaN, no infinity).</summary>
        public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);

        // --- Operators ---

        /// <summary>Component-wise sum.</summary>
        public static Vec3 operator +(in Vec3 a, in Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        /// <summary>Component-wise difference.</summary>
        public static Vec3 operator -(in Vec3 a, in Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        /// <summary>Scale by a scalar.</summary>
        public static Vec3 operator *(in Vec3 v, float s) => new Vec3(v.X * s, v.Y * s, v.Z * s);

        /// <summary>Scale by a scalar.</summary>
        public static Vec3 operator *(float s, in Vec3 v) => new Vec3(v.X * s, v.Y * s, v.Z * s);

        /// <summary>Exact component equality (no tolerance — determinism is bit-exact).</summary>
        public static bool operator ==(in Vec3 a, in Vec3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

        /// <summary>Negation of <c>==</c>.</summary>
        public static bool operator !=(in Vec3 a, in Vec3 b) => !(a == b);

        // --- Equality ---

        /// <inheritdoc />
        public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);

        /// <inheritdoc />
        public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
    }
}
