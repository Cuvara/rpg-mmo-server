using System;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// Simulation state of one projectile (ADR-29 decision 1). Owner, ability and
    /// <c>spawn_seq</c> are bookkeeping the server keeps beside this; the simulation needs
    /// only the moving sphere and how far it may still go.
    /// </summary>
    public struct ProjectileState
    {
        /// <summary>Centre of the projectile's sphere.</summary>
        public Vec3 Position;

        /// <summary>Velocity in units per second. Constant: skillshots fly straight.</summary>
        public Vec3 Velocity;

        /// <summary>Sphere radius used for world and target hits.</summary>
        public float Radius;

        /// <summary>Distance left before the projectile expires, in units.</summary>
        public float RemainingRange;

        /// <summary>Create a state.</summary>
        public ProjectileState(in Vec3 position, in Vec3 velocity, float radius, float remainingRange)
        {
            Position = position;
            Velocity = velocity;
            Radius = radius;
            RemainingRange = remainingRange;
        }
    }

    /// <summary>
    /// Skillshot projectile simulation and hit geometry (ADR-29 decisions 1, 3 and 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A projectile moves in a straight line at constant velocity. Each tick its path is a
    /// segment; <see cref="Step"/> sweeps that segment against the map
    /// (<see cref="MapGeometry.SweepSphere"/>) and stops it at the first wall or ground
    /// contact. The caller then tests target capsules against the segment it actually
    /// travelled (<c>state.Position → next.Position</c>) with
    /// <see cref="SegmentCapsuleHit"/>. Because a world hit already shortened that segment,
    /// a target behind a wall is never reached — geometry stops a projectile.
    /// </para>
    /// <para>
    /// Same determinism rules as the rest of the library: explicit float rounding of every
    /// intermediate, <c>MathF.Sqrt</c> only, fixed iteration counts, no trigonometry. The
    /// owner's client predicts its own projectile with these functions (ADR-29 decision 2).
    /// </para>
    /// </remarks>
    public static class ProjectileLogic
    {
        /// <summary>
        /// Bisection iterations <see cref="SegmentCapsuleHit"/> uses to locate first contact.
        /// 16 halvings resolve t to ~1.5e-5 of the segment.
        /// </summary>
        public const int ContactRefineIterations = 16;

        /// <summary>
        /// Create a projectile at <paramref name="origin"/> flying toward
        /// <paramref name="aimPoint"/>.
        /// </summary>
        /// <param name="origin">
        /// Launch point. The server derives this from the caster's <b>authoritative</b>
        /// position (ADR-29 decision 5) — never from a client-supplied origin.
        /// </param>
        /// <param name="aimPoint">The point aimed at (InputMessage aim_x/aim_y/aim_z).</param>
        /// <param name="speed">Units per second; must be positive and finite.</param>
        /// <param name="radius">Sphere radius; must be non-negative and finite.</param>
        /// <param name="range">Maximum travel; must be positive and finite.</param>
        /// <param name="state">The new projectile; default when refused.</param>
        /// <returns>
        /// False — refuse to fire — when the aim point coincides with the origin (no
        /// direction exists; inventing one would fire a shot the player did not aim) or when
        /// any input is not a usable number.
        /// </returns>
        public static bool Spawn(in Vec3 origin, in Vec3 aimPoint, float speed, float radius, float range, out ProjectileState state)
        {
            state = default;
            if (!origin.IsFinite || !aimPoint.IsFinite) return false;
            if (!(speed > 0f) || !float.IsFinite(speed)) return false;
            if (!(radius >= 0f) || !float.IsFinite(radius)) return false;
            if (!(range > 0f) || !float.IsFinite(range)) return false;

            Vec3 delta = aimPoint - origin;
            float lenSq = delta.SqrMagnitude;
            if (!(lenSq > GameConstants.InputDeadzoneSq) || !float.IsFinite(lenSq)) return false;

            float len = MathF.Sqrt(lenSq);
            float s = (float)(speed / len);
            var velocity = new Vec3((float)(delta.X * s), (float)(delta.Y * s), (float)(delta.Z * s));
            state = new ProjectileState(origin, velocity, radius, range);
            return true;
        }

        /// <summary>
        /// Advance a projectile by one fixed timestep.
        /// </summary>
        /// <param name="state">Current state.</param>
        /// <param name="dt">Timestep in seconds, clamped to <see cref="GameConstants.MaxDeltaTime"/>.</param>
        /// <param name="geo">Map collision world.</param>
        /// <param name="next">
        /// State after the step. On a world hit, <c>Position</c> is the sphere centre at
        /// contact. When the remaining range runs out mid-tick, the path is shortened to end
        /// exactly at the range limit.
        /// </param>
        /// <param name="hitWorld">True when terrain or a box stopped the projectile this tick.</param>
        /// <returns>
        /// True while the projectile is still flying after this step. False when it hit the
        /// world, used up its range, left the map bounds, or was given an unusable dt. The
        /// caller still tests targets along <c>state.Position → next.Position</c> on the
        /// tick it returns false — the last stretch of flight can hit something.
        /// </returns>
        public static bool Step(in ProjectileState state, float dt, MapGeometry geo, out ProjectileState next, out bool hitWorld)
        {
            if (geo == null) throw new ArgumentNullException(nameof(geo));

            next = state;
            hitWorld = false;
            if (!float.IsFinite(dt) || dt <= 0f) return false;
            if (dt > GameConstants.MaxDeltaTime) dt = GameConstants.MaxDeltaTime;
            if (!(state.RemainingRange > 0f)) return false;

            float speed = state.Velocity.Magnitude;
            if (!(speed > 0f) || !float.IsFinite(speed)) return false;

            float travel = (float)(speed * dt);
            float fraction = 1f;
            bool rangeEnds = false;
            if (travel >= state.RemainingRange)
            {
                fraction = (float)(state.RemainingRange / travel);
                travel = state.RemainingRange;
                rangeEnds = true;
            }

            float scale = (float)(dt * fraction);
            Vec3 to = Vec3.AddScaled(state.Position, state.Velocity, scale);

            if (geo.SweepSphere(state.Position, to, state.Radius, out Vec3 hitPoint, out float hitT))
            {
                hitWorld = true;
                float used = (float)(travel * hitT);
                next = new ProjectileState(hitPoint, state.Velocity, state.Radius, (float)(state.RemainingRange - used));
                return false;
            }

            float remaining = rangeEnds ? 0f : (float)(state.RemainingRange - travel);
            next = new ProjectileState(to, state.Velocity, state.Radius, remaining);

            if (rangeEnds) return false;
            // Leaving the play area ends the projectile; nothing outside the bounds can be hit.
            if (!geo.Bounds.Contains(to.XY)) return false;
            return true;
        }

        /// <summary>
        /// Does a sphere of <paramref name="sphereRadius"/> moving along
        /// <paramref name="segStart"/> → <paramref name="segEnd"/> touch an upright capsule
        /// standing at <paramref name="capsuleBase"/>?
        /// </summary>
        /// <param name="segStart">Sphere centre at t = 0.</param>
        /// <param name="segEnd">Sphere centre at t = 1.</param>
        /// <param name="sphereRadius">Projectile radius.</param>
        /// <param name="capsuleBase">Target's feet (bottom of the capsule).</param>
        /// <param name="capsuleRadius">Target capsule radius.</param>
        /// <param name="capsuleHeight">Target capsule height, feet to top.</param>
        /// <param name="t">
        /// Segment parameter in [0, 1] of <b>first contact</b> — not of closest approach — so
        /// that when several targets are hit, ordering by <c>t</c> picks the one the
        /// projectile reached first. 1 when there is no hit.
        /// </param>
        /// <returns>True on a hit (touching counts).</returns>
        /// <remarks>
        /// <para>
        /// The capsule's axis runs from <c>base + radius</c> to <c>base + height - radius</c>
        /// (a capsule shorter than its diameter degenerates to a sphere at half height). The
        /// test is the closest distance between the projectile's segment and that axis
        /// (Ericson, <i>Real-Time Collision Detection</i> §5.1.9) against the sum of radii.
        /// </para>
        /// <para>
        /// First contact is then found by bisection on [0, t_closest]: the distance from a
        /// point moving along a line to a convex set is convex in t, so it decreases
        /// monotonically up to the closest approach and a fixed number of halvings
        /// (<see cref="ContactRefineIterations"/>) converges deterministically, where the
        /// closed-form quadratic would need a second square root and case analysis per end cap.
        /// </para>
        /// </remarks>
        public static bool SegmentCapsuleHit(
            in Vec3 segStart, in Vec3 segEnd, float sphereRadius,
            in Vec3 capsuleBase, float capsuleRadius, float capsuleHeight,
            out float t)
        {
            t = 1f;
            AxisOf(capsuleBase, capsuleRadius, capsuleHeight, out Vec3 a0, out Vec3 a1);

            float reach = (float)(sphereRadius + capsuleRadius);
            float reachSq = (float)(reach * reach);

            ClosestSegmentSegment(segStart, segEnd, a0, a1, out float sClosest, out Vec3 c1, out Vec3 c2);
            if (Vec3.DistanceSq(c1, c2) > reachSq) return false;

            if (PointAxisDistSq(segStart, a0, a1) <= reachSq)
            {
                t = 0f;
                return true;
            }

            float lo = 0f;
            float hi = sClosest;
            for (int i = 0; i < ContactRefineIterations; i++)
            {
                float mid = (float)((float)(lo + hi) * 0.5f);
                Vec3 pm = Vec3.Lerp(segStart, segEnd, mid);
                if (PointAxisDistSq(pm, a0, a1) <= reachSq) hi = mid;
                else lo = mid;
            }

            t = hi;
            return true;
        }

        /// <summary>
        /// Axis endpoints of an upright capsule; equal (a sphere) when the capsule is no
        /// taller than its diameter.
        /// </summary>
        private static void AxisOf(in Vec3 basePos, float radius, float height, out Vec3 a0, out Vec3 a1)
        {
            float bottom = (float)(basePos.Z + radius);
            float top = (float)((float)(basePos.Z + height) - radius);
            if (top < bottom)
            {
                float mid = (float)(basePos.Z + (float)(height * 0.5f));
                bottom = mid;
                top = mid;
            }

            a0 = new Vec3(basePos.X, basePos.Y, bottom);
            a1 = new Vec3(basePos.X, basePos.Y, top);
        }

        /// <summary>Squared distance from a point to the vertical axis segment a0 → a1.</summary>
        private static float PointAxisDistSq(in Vec3 p, in Vec3 a0, in Vec3 a1)
        {
            float cz = p.Z < a0.Z ? a0.Z : (p.Z > a1.Z ? a1.Z : p.Z);
            float dx = (float)(p.X - a0.X);
            float dy = (float)(p.Y - a0.Y);
            float dz = (float)(p.Z - cz);
            return (float)((float)((float)(dx * dx) + (float)(dy * dy)) + (float)(dz * dz));
        }

        /// <summary>
        /// Closest points between segments p1→q1 and p2→q2 (Ericson §5.1.9), with every
        /// intermediate rounded to float. <paramref name="s"/> is the parameter on the first
        /// segment.
        /// </summary>
        private static void ClosestSegmentSegment(
            in Vec3 p1, in Vec3 q1, in Vec3 p2, in Vec3 q2,
            out float s, out Vec3 c1, out Vec3 c2)
        {
            const float eps = 1e-12f;
            Vec3 d1 = q1 - p1;
            Vec3 d2 = q2 - p2;
            Vec3 r = p1 - p2;
            float a = Vec3.Dot(d1, d1);
            float e = Vec3.Dot(d2, d2);
            float f = Vec3.Dot(d2, r);
            float t;

            if (a <= eps && e <= eps)
            {
                s = 0f;
                t = 0f;
            }
            else if (a <= eps)
            {
                s = 0f;
                t = Clamp01((float)(f / e));
            }
            else
            {
                float c = Vec3.Dot(d1, r);
                if (e <= eps)
                {
                    t = 0f;
                    s = Clamp01((float)(-c / a));
                }
                else
                {
                    float b = Vec3.Dot(d1, d2);
                    float denom = (float)((float)(a * e) - (float)(b * b));
                    if (denom != 0f)
                    {
                        float num = (float)((float)(b * f) - (float)(c * e));
                        s = Clamp01((float)(num / denom));
                    }
                    else
                    {
                        // Parallel: any s works; 0 is the deterministic choice.
                        s = 0f;
                    }

                    t = (float)((float)((float)(b * s) + f) / e);
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Clamp01((float)(-c / a));
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Clamp01((float)((float)(b - c) / a));
                    }
                }
            }

            c1 = Vec3.AddScaled(p1, d1, s);
            c2 = Vec3.AddScaled(p2, d2, t);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
