using System;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// Kinematic state of one character between motor steps (ADR-28 decision 2).
    /// </summary>
    public struct MotorState
    {
        /// <summary>Feet position: the bottom centre of the capsule.</summary>
        public Vec3 Position;

        /// <summary>Vertical velocity in units per second; positive is up. 0 while grounded.</summary>
        public float VelocityZ;

        /// <summary>True when the character is standing on terrain or a box top.</summary>
        public bool Grounded;

        /// <summary>Create a state.</summary>
        public MotorState(in Vec3 position, float velocityZ, bool grounded)
        {
            Position = position;
            VelocityZ = velocityZ;
            Grounded = grounded;
        }

        /// <summary>
        /// A character standing at <paramref name="feet"/>. Spawns and joins start here; the
        /// first <see cref="CharacterMotor.Step"/> settles it onto the support below if the
        /// spawn was placed slightly in the air.
        /// </summary>
        public static MotorState StandingAt(in Vec3 feet) => new MotorState(feet, 0f, true);
    }

    /// <summary>
    /// Tuning of the character motor. Shared by server and client: a client predicting
    /// with different values diverges on its first jump.
    /// </summary>
    public struct MotorParams
    {
        /// <summary>Downward acceleration in units/s² (positive number).</summary>
        public float Gravity;

        /// <summary>Upward velocity a jump starts with, in units/s.</summary>
        public float JumpSpeed;

        /// <summary>Tallest ledge (box top above the feet) a grounded character walks up without jumping.</summary>
        public float StepHeight;

        /// <summary>
        /// Steepest terrain a character can walk up, as rise per unit of horizontal travel
        /// (1 = 45°). A ratio instead of an angle so the check needs no trigonometry (ADR-10).
        /// </summary>
        public float MaxSlopeRise;

        /// <summary>Capsule radius in units.</summary>
        public float CapsuleRadius;

        /// <summary>Capsule height (feet to top of head) in units.</summary>
        public float CapsuleHeight;

        /// <summary>
        /// The defaults: <see cref="CharacterMotor.DefaultGravity"/> and friends. Jump apex
        /// is JumpSpeed² / (2·Gravity) = 1.6 units, a little under the capsule height, so a
        /// character can jump onto a waist-high crate but not over a wall.
        /// </summary>
        public static MotorParams Default => new MotorParams
        {
            Gravity = CharacterMotor.DefaultGravity,
            JumpSpeed = CharacterMotor.DefaultJumpSpeed,
            StepHeight = CharacterMotor.DefaultStepHeight,
            MaxSlopeRise = CharacterMotor.DefaultMaxSlopeRise,
            CapsuleRadius = CharacterMotor.DefaultCapsuleRadius,
            CapsuleHeight = CharacterMotor.DefaultCapsuleHeight,
        };
    }

    /// <summary>
    /// Kinematic capsule character motor (ADR-28 decision 2): planar input direction times
    /// speed, gravity, a jump honoured only when grounded, step-up onto low boxes, a slope
    /// limit on terrain, and clamping to the map bounds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One step, in order.</b> (1) resolve the input with
    /// <see cref="MovementSystem.ResolveDirection"/>, so deadzone, clamping and rejection
    /// are exactly the protocol 2 rules; (2) a jump sets the vertical velocity if grounded;
    /// (3) horizontal travel is applied one axis at a time, X then Y, each one blocked or
    /// stepped up independently — that is what makes a character slide along a wall
    /// instead of stopping dead against it; (4) a grounded character follows the support
    /// under it up and down, or starts falling when it walked off a ledge; an airborne one
    /// integrates gravity, bumps its head on ceilings and lands on the support below.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> Same discipline as <see cref="MovementSystem.Integrate"/>: every
    /// intermediate is rounded to float explicitly so neither a wide-intermediate JIT nor
    /// FMA contraction can change a result; no trigonometry; boxes are visited in array
    /// order. Prediction and authority agree to the bit wherever their inputs agree.
    /// </para>
    /// <para>
    /// <b>Allocation-free.</b> Structs in, struct out, no collections.
    /// </para>
    /// </remarks>
    public static class CharacterMotor
    {
        /// <summary>Default gravity, units/s².</summary>
        public const float DefaultGravity = 20f;

        /// <summary>Default jump speed, units/s (apex 1.6 units with default gravity).</summary>
        public const float DefaultJumpSpeed = 8f;

        /// <summary>Default step height, units. Stairs and kerbs, not crates.</summary>
        public const float DefaultStepHeight = 0.4f;

        /// <summary>Default slope limit: rise of 1 per unit of travel, i.e. 45°.</summary>
        public const float DefaultMaxSlopeRise = 1f;

        /// <summary>Default capsule radius, units.</summary>
        public const float DefaultCapsuleRadius = 0.4f;

        /// <summary>Default capsule height, units.</summary>
        public const float DefaultCapsuleHeight = 1.8f;

        /// <summary>
        /// Slack added to the slope limit, in units of rise. A slope authored at exactly the
        /// limit would otherwise be walkable or not depending on which way one ULP of the
        /// bilinear sample rounds — still deterministic, but it would feel random to a player
        /// and to a content author testing the limit.
        /// </summary>
        public const float SlopeTolerance = 1e-4f;

        /// <summary>
        /// Advance one character by one fixed timestep.
        /// </summary>
        /// <param name="state">Current state.</param>
        /// <param name="moveX">Raw input X from the wire (a direction, not a displacement).</param>
        /// <param name="moveY">Raw input Y from the wire.</param>
        /// <param name="jump">Jump requested this tick; ignored unless grounded.</param>
        /// <param name="speed">Horizontal speed in units per second.</param>
        /// <param name="dt">Timestep in seconds, clamped to <see cref="GameConstants.MaxDeltaTime"/>.</param>
        /// <param name="geo">Map collision world.</param>
        /// <param name="p">Motor tuning.</param>
        /// <param name="next">Resulting state.</param>
        /// <returns>
        /// The input classification, as <see cref="MovementSystem.TryMove"/> returns it.
        /// <see cref="MoveResult.None"/>, <see cref="MoveResult.Rejected"/> and
        /// <see cref="MoveResult.Blocked"/> (non-positive or non-finite speed) mean no
        /// horizontal travel — but gravity, jumping and landing <b>still run</b>: a body in
        /// the air must keep falling when its owner's packet was garbage. Only an unusable
        /// <paramref name="dt"/> leaves the state untouched (also <see cref="MoveResult.Blocked"/>).
        /// A wall stopping the character does not change the result; compare positions.
        /// </returns>
        public static MoveResult Step(
            in MotorState state,
            float moveX,
            float moveY,
            bool jump,
            float speed,
            float dt,
            MapGeometry geo,
            in MotorParams p,
            out MotorState next)
        {
            if (geo == null) throw new ArgumentNullException(nameof(geo));

            next = state;
            if (!float.IsFinite(dt) || dt <= 0f) return MoveResult.Blocked;
            if (dt > GameConstants.MaxDeltaTime) dt = GameConstants.MaxDeltaTime;

            MoveResult result = MovementSystem.ResolveDirection(moveX, moveY, out Vec2 direction);
            bool canMove = result == MoveResult.Accepted || result == MoveResult.Clamped;
            if (canMove && (!(speed > 0f) || !float.IsFinite(speed)))
            {
                result = MoveResult.Blocked;
                canMove = false;
            }

            float x = state.Position.X;
            float y = state.Position.Y;
            float z = state.Position.Z;
            float vz = state.VelocityZ;
            bool grounded = state.Grounded;

            // (2) Jump: level-triggered, grounded only, so a held button does not fly.
            if (jump && grounded)
            {
                vz = p.JumpSpeed;
                grounded = false;
            }

            // (3) Horizontal, one axis at a time. The step is split into its own float local
            // before the add for the FMA reason given on MovementSystem.Integrate.
            float movedX = 0f;
            float movedY = 0f;
            if (canMove)
            {
                float step = (float)(speed * dt);
                float dx = (float)(direction.X * step);
                float dy = (float)(direction.Y * step);

                if (dx != 0f)
                {
                    float tx = Clamp((float)(x + dx), geo.Bounds.MinX, geo.Bounds.MaxX);
                    if (TryAxisMove(geo, p, x, y, z, tx, y, grounded, out float nz))
                    {
                        movedX = Abs((float)(tx - x));
                        x = tx;
                        z = nz;
                    }
                }

                if (dy != 0f)
                {
                    float ty = Clamp((float)(y + dy), geo.Bounds.MinY, geo.Bounds.MaxY);
                    if (TryAxisMove(geo, p, x, y, z, x, ty, grounded, out float nz))
                    {
                        movedY = Abs((float)(ty - y));
                        y = ty;
                        z = nz;
                    }
                }
            }

            // (4) Vertical.
            if (grounded)
            {
                // Follow the support. Anything up to a step above the feet counts (the
                // horizontal pass already climbed what it was allowed to); anything down to a
                // step plus what the slope limit allows for the distance walked is followed,
                // so walking down stairs or a slope does not turn into a series of tiny falls.
                // |dx| + |dy| bounds the true distance from above without a sqrt.
                float support = geo.SupportHeight(x, y, p.CapsuleRadius, (float)(z + p.StepHeight));
                float slopeDrop = (float)(p.MaxSlopeRise * (float)(movedX + movedY));
                float snapDown = (float)(p.StepHeight + slopeDrop);
                if (support >= (float)(z - snapDown))
                {
                    z = support;
                    vz = 0f;
                }
                else
                {
                    // Walked off a ledge. Fall from rest, starting this tick.
                    grounded = false;
                    vz = 0f;
                }
            }

            if (!grounded)
            {
                float dv = (float)(p.Gravity * dt);
                vz = (float)(vz - dv);
                float dz = (float)(vz * dt);
                float nz = (float)(z + dz);

                if (vz > 0f)
                {
                    // Ceiling: stop at the underside of the lowest box the head would reach.
                    float headNow = (float)(z + p.CapsuleHeight);
                    float headNext = (float)(nz + p.CapsuleHeight);
                    ReadOnlySpan<StaticBox> boxes = geo.Boxes;
                    for (int i = 0; i < boxes.Length; i++)
                    {
                        StaticBox b = boxes[i];
                        if (b.MinZ >= headNow && b.MinZ < headNext && b.FootprintOverlaps(x, y, p.CapsuleRadius))
                        {
                            headNext = b.MinZ;
                            nz = (float)(b.MinZ - p.CapsuleHeight);
                            vz = 0f;
                        }
                    }
                }

                // Landing: supports at or below where the feet were at the start of the fall.
                // A box whose top is above the feet is beside or above the character, not under it.
                float support = geo.SupportHeight(x, y, p.CapsuleRadius, z > nz ? z : nz);
                if (nz <= support)
                {
                    nz = support;
                    vz = 0f;
                    grounded = true;
                }

                z = nz;
            }

            next = new MotorState(new Vec3(x, y, z), vz, grounded);
            return result;
        }

        /// <summary>
        /// Try to move the capsule along one axis from <c>(x, y)</c> to <c>(tx, ty)</c>.
        /// Returns false when blocked; otherwise the feet height after any step-up.
        /// </summary>
        private static bool TryAxisMove(
            MapGeometry geo, in MotorParams p,
            float x, float y, float z, float tx, float ty,
            bool grounded, out float newZ)
        {
            newZ = z;
            float dist = Abs((float)((float)(tx - x) + (float)(ty - y))); // one axis is zero

            // Terrain slope: rise above the feet beyond what the slope limit allows for this
            // distance blocks. Applies airborne too, so a jump cannot carry a character up a
            // cliff face it could not walk.
            float rise = (float)(geo.GroundHeight(tx, ty) - z);
            float allowed = (float)((float)(p.MaxSlopeRise * dist) + SlopeTolerance);
            if (rise > allowed) return false;

            var from = new Vec3(x, y, z);
            var to = new Vec3(tx, ty, z);
            float stepTop = z;

            ReadOnlySpan<StaticBox> boxes = geo.Boxes;
            for (int i = 0; i < boxes.Length; i++)
            {
                StaticBox b = boxes[i];
                if (!b.CapsuleOverlaps(to, p.CapsuleRadius, p.CapsuleHeight)) continue;
                // Already inside it (spawned in, or pushed by a geometry change): ignore it so
                // the character can walk out instead of being stuck forever.
                if (b.CapsuleOverlaps(from, p.CapsuleRadius, p.CapsuleHeight)) continue;

                float climb = (float)(b.MaxZ - z);
                if (grounded && climb <= p.StepHeight)
                {
                    if (b.MaxZ > stepTop) stepTop = b.MaxZ;
                }
                else
                {
                    return false;
                }
            }

            if (stepTop > z)
            {
                // Stepped up: the capsule at its new height must be clear too, or a low box
                // under a ceiling would let a character climb into the ceiling.
                var raised = new Vec3(tx, ty, stepTop);
                for (int i = 0; i < boxes.Length; i++)
                {
                    StaticBox b = boxes[i];
                    if (b.CapsuleOverlaps(raised, p.CapsuleRadius, p.CapsuleHeight) &&
                        !b.CapsuleOverlaps(from, p.CapsuleRadius, p.CapsuleHeight))
                        return false;
                }

                newZ = stepTop;
            }

            // A grounded character walking up a slope rises with it now, not in the vertical
            // pass: the second axis's slope check measures rise from these feet, and on a
            // diagonal slope measuring from the pre-move height would charge the first axis's
            // climb to the second and block a walkable slope.
            if (grounded)
            {
                float ground = geo.GroundHeight(tx, ty);
                if (ground > newZ) newZ = ground;
            }

            return true;
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

        private static float Abs(float v) => v < 0f ? -v : v;
    }
}
