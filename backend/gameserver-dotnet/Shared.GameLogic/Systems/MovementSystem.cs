using System;
using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// Outcome of resolving/applying a movement input. Returned instead of throwing —
    /// the tick loop must never allocate an exception for a hostile client packet.
    /// </summary>
    public enum MoveResult
    {
        /// <summary>No movement requested (input vector inside the deadzone).</summary>
        None = 0,

        /// <summary>Input accepted as-is (magnitude &lt;= 1, analog partial speed preserved).</summary>
        Accepted = 1,

        /// <summary>
        /// Input magnitude was above 1 but still plausible; normalized to unit length
        /// before integration. Raw diagonal key input (1,1) lands here.
        /// </summary>
        Clamped = 2,

        /// <summary>
        /// Input is grossly invalid (NaN, infinity, or magnitude beyond
        /// <see cref="GameConstants.MaxInputMagnitude"/>). Caller should log and drop.
        /// </summary>
        Rejected = 3,

        /// <summary>
        /// The entity cannot move right now (dead, non-positive speed, or non-positive dt).
        /// </summary>
        Blocked = 4,
    }

    /// <summary>
    /// Server-authoritative movement model.
    ///
    /// <para>
    /// The wire field <c>move_x</c>/<c>move_y</c> carries a <b>direction</b>, not a
    /// displacement. Position advances by <c>direction * speed * dt</c> where
    /// <c>dt = 1 / tickRate</c>, so travel distance depends only on wall-clock time and
    /// the entity's speed stat — never on how many input packets a client sends.
    /// </para>
    ///
    /// <para>
    /// Everything here is deterministic and allocation-free: no randomness, no
    /// <see cref="DateTime"/>, no collections. The Unity DOTS client links the same
    /// library and runs the exact same functions for client-side prediction, so a
    /// predicted position and the authoritative position agree bit-for-bit given the
    /// same input, speed and dt.
    /// </para>
    /// </summary>
    public static class MovementSystem
    {
        /// <summary>
        /// Fixed timestep for a given simulation tick rate, in seconds.
        /// Returns 0 for a non-positive tick rate (caller treats that as "cannot move").
        /// </summary>
        public static float DeltaTimeForTickRate(int tickRate) =>
            tickRate > 0 ? 1f / tickRate : 0f;

        /// <summary>
        /// Turn a raw client input vector into a movement direction with magnitude &lt;= 1.
        /// </summary>
        /// <param name="moveX">Raw X component from the wire.</param>
        /// <param name="moveY">Raw Y component from the wire.</param>
        /// <param name="direction">
        /// Resolved direction. Zero unless the result is
        /// <see cref="MoveResult.Accepted"/> or <see cref="MoveResult.Clamped"/>.
        /// </param>
        /// <remarks>
        /// Normalization is what makes diagonal movement the same speed as cardinal
        /// movement: (1,1) has magnitude ~1.414 and becomes (0.707, 0.707).
        /// </remarks>
        public static MoveResult ResolveDirection(float moveX, float moveY, out Vec2 direction)
        {
            direction = Vec2.Zero;

            if (!float.IsFinite(moveX) || !float.IsFinite(moveY))
                return MoveResult.Rejected;

            // Explicit per-operation casts: C# allows float arithmetic to be
            // evaluated at higher precision (ECMA-334 §11.3.7), and .NET 10
            // (strict float32) and Unity's Mono JIT (double intermediates)
            // choose differently. Without these the two runtimes disagree by one
            // ULP here, which changes which branch the magnitude comparisons
            // below take. See Vec2.SqrMagnitude for the same fix.
            float magSq = (float)((float)(moveX * moveX) + (float)(moveY * moveY));

            if (magSq <= GameConstants.InputDeadzoneSq)
                return MoveResult.None;

            const float maxSq = GameConstants.MaxInputMagnitude * GameConstants.MaxInputMagnitude;
            if (magSq > maxSq)
                return MoveResult.Rejected;

            if (magSq > 1f)
            {
                float mag = MathF.Sqrt(magSq);
                direction = new Vec2(moveX / mag, moveY / mag);
                return MoveResult.Clamped;
            }

            direction = new Vec2(moveX, moveY);
            return MoveResult.Accepted;
        }

        /// <summary>
        /// Advance a position by one fixed timestep and clamp it to the map bounds.
        /// </summary>
        /// <param name="position">Current position.</param>
        /// <param name="direction">Direction with magnitude &lt;= 1 (see <see cref="ResolveDirection"/>).</param>
        /// <param name="speed">Movement speed in world units per second.</param>
        /// <param name="dt">Timestep in seconds; clamped to <see cref="GameConstants.MaxDeltaTime"/>.</param>
        /// <param name="bounds">Play area the resulting position is clamped into.</param>
        public static Vec2 Integrate(in Vec2 position, in Vec2 direction, float speed, float dt, in MapBounds bounds)
        {
            float step = (float)(speed * ClampDeltaTime(dt));

            // `a + b * c` is the shape most at risk here: besides the wider
            // intermediates that break SqrMagnitude, a JIT is free to contract a
            // multiply-add into a single FMA instruction, which rounds ONCE
            // instead of twice and so gives a different float. Splitting the
            // multiply into its own float local denies the contraction.
            float dx = (float)(direction.X * step);
            float dy = (float)(direction.Y * step);
            var moved = new Vec2(
                (float)(position.X + dx),
                (float)(position.Y + dy));
            return bounds.Clamp(moved);
        }

        /// <summary>
        /// Full server-side movement step for one entity and one input: validate the
        /// input vector, integrate, clamp to bounds.
        /// </summary>
        /// <param name="entity">Entity being moved (read-only; caller writes the position back).</param>
        /// <param name="moveX">Raw X component from the wire.</param>
        /// <param name="moveY">Raw Y component from the wire.</param>
        /// <param name="dt">Fixed timestep in seconds.</param>
        /// <param name="bounds">Play area.</param>
        /// <param name="newPosition">
        /// Resulting position. Equal to the entity's current position unless the result is
        /// <see cref="MoveResult.Accepted"/> or <see cref="MoveResult.Clamped"/>.
        /// </param>
        public static MoveResult TryMove(
            in EntityState entity,
            float moveX,
            float moveY,
            float dt,
            in MapBounds bounds,
            out Vec2 newPosition)
        {
            newPosition = entity.Position;

            if (entity.Dead)
                return MoveResult.Blocked;

            MoveResult result = ResolveDirection(moveX, moveY, out Vec2 direction);
            if (result is MoveResult.None or MoveResult.Rejected)
                return result;

            if (entity.Speed <= 0f || !float.IsFinite(entity.Speed) || dt <= 0f || !float.IsFinite(dt))
                return MoveResult.Blocked;

            newPosition = Integrate(in entity.Position, in direction, entity.Speed, dt, in bounds);
            return result;
        }

        /// <summary>
        /// Theoretical maximum displacement for one tick: <c>speed * dt</c>, with the
        /// jitter tolerance applied. Anti-cheat audit bound.
        /// </summary>
        public static float MaxDisplacementPerTick(float speed, float dt) =>
            speed * ClampDeltaTime(dt) * GameConstants.DisplacementTolerance;

        /// <summary>
        /// Audit an observed position change against what the movement model allows in
        /// one tick. Used to validate client-reported positions (reconciliation) and as a
        /// defence-in-depth check on any code path that writes a position directly.
        /// </summary>
        public static bool IsDisplacementLegal(in Vec2 from, in Vec2 to, float speed, float dt)
        {
            float limit = MaxDisplacementPerTick(speed, dt);
            return Vec2.DistanceSq(from, to) <= limit * limit;
        }

        private static float ClampDeltaTime(float dt)
        {
            if (!float.IsFinite(dt) || dt <= 0f) return 0f;
            return dt > GameConstants.MaxDeltaTime ? GameConstants.MaxDeltaTime : dt;
        }
    }
}
