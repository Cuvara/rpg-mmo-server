namespace Shared.GameLogic.Components
{
    /// <summary>
    /// Shared game constants. Used by both server validation and client prediction —
    /// the Unity client must compile against the exact same values or prediction
    /// diverges from the authoritative simulation.
    /// </summary>
    public static class GameConstants
    {
        // ── Movement ──

        /// <summary>
        /// Maximum accepted magnitude of a raw client input vector before it is treated
        /// as garbage and dropped. Anything in (1, this] is normalized to unit length —
        /// a raw diagonal key input of (1,1) has magnitude ~1.414 and is clamped, not dropped.
        /// </summary>
        public const float MaxInputMagnitude = 1.5f;

        /// <summary>
        /// Squared magnitude below which an input vector counts as "no movement".
        /// </summary>
        public const float InputDeadzoneSq = 1e-8f;

        /// <summary>
        /// Upper bound for a single integration step in seconds. Guards against a
        /// pathological dt (paused process, debugger break) teleporting an entity.
        /// </summary>
        public const float MaxDeltaTime = 0.5f;

        /// <summary>
        /// Tolerance factor applied when auditing an observed displacement against the
        /// theoretical maximum (<c>speed * dt</c>). Absorbs float rounding and one frame
        /// of jitter without opening a speed-hack window.
        /// </summary>
        public const float DisplacementTolerance = 1.05f;

        /// <summary>
        /// Longest span of simulated time a single movement step may cover, in
        /// milliseconds — the cap on how much elapsed time an input can "bank".
        ///
        /// <para>A movement step advances the entity by the time since it last moved, not
        /// by one fixed tick, so that a burst of inputs arriving in one tick is not silently
        /// reduced to a single tick's travel (#100). This constant bounds that: a client
        /// that goes quiet and then sends can never claim more than this much movement in
        /// one step, however long it was silent.</para>
        ///
        /// <para><b>Why 250ms.</b> It has to be at least one send interval of the slowest
        /// client we support, or that client can never be made whole: a client sending at
        /// the world rate (15Hz, so 66ms apart) whose packets clump into fours leaves a
        /// 266ms gap between bursts, and a cap below that leaves part of every gap
        /// permanently unpaid. Measured directly — at a 200ms cap a bursting 15Hz client
        /// against a 15Hz server recovers to 72% of its intended distance rather than to
        /// 100%.</para>
        ///
        /// <para>It is deliberately close to, and slightly above, the ~200ms dead-reckoning
        /// limit the netcode model sets for bad mobile networks: both answer "how long may
        /// the server move on information it no longer has". Banking is the safer of the
        /// two, because the client demonstrably <i>did</i> send input covering the period —
        /// the packets arrived, coalescing is what discarded them — whereas dead reckoning
        /// extrapolates input that was never sent. That is the whole of the justification
        /// for the gap between the two numbers, and it is why this one is not larger
        /// still.</para>
        ///
        /// <para><b>A predicting client must apply the same cap</b>, because it is part of
        /// the movement model rather than a server-side safety valve: a client that banks
        /// unbounded time reconciles against a server that does not, on exactly the frames
        /// where the network was worst.</para>
        /// </summary>
        public const int MaxBankedMovementMs = 250;

        /// <summary>
        /// The most a repaying step may add on top of its own movement, as a fraction of a
        /// normal step.
        /// </summary>
        /// <remarks>
        /// What turns owed time from a jump into a catch-up. At 0.5 a repaying player runs
        /// at 1.5x their own speed until square, so the largest debt
        /// <see cref="MaxBankedMovementMs"/> permits clears in half a second rather than in
        /// one frame. Higher clears sooner and is more visible; lower is invisible but
        /// leaves the predicted and authoritative positions disagreeing for longer.
        /// </remarks>
        public const float MaxCatchUpFraction = 0.5f;

        /// <summary>
        /// <see cref="MaxBankedMovementMs"/> expressed in ticks at the given simulation
        /// rate, rounded up and never below 1.
        /// </summary>
        public static int MaxBankedMovementTicks(int tickRate)
        {
            if (tickRate <= 0) tickRate = DefaultTickRate;
            int ticks = (MaxBankedMovementMs * tickRate + 999) / 1000; // ceil
            return ticks < 1 ? 1 : ticks;
        }

        /// <summary>Default map width in world units.</summary>
        public const float DefaultMapWidth = 1000f;

        /// <summary>Default map height in world units.</summary>
        public const float DefaultMapHeight = 1000f;

        // ── Combat ──

        /// <summary>Maximum attack range in world units.</summary>
        public const float AttackRange = 3.0f;

        /// <summary>Attack cooldown duration in milliseconds.</summary>
        public const int AttackCooldownMs = 500;

        /// <summary>
        /// Attack cooldown expressed in simulation ticks for the given tick rate.
        /// <para>
        /// Cooldowns are counted in ticks, not wall-clock time: the simulation must be
        /// deterministic and replayable (client prediction rewind/replay, server-side
        /// replay of a disputed sequence). A <c>DateTime.UtcNow</c> comparison makes the
        /// same input sequence produce different outcomes on two runs.
        /// </para>
        /// <para>
        /// Rounded UP so the tick-based cooldown is never shorter than the wall-clock one
        /// it replaces. At the default 15Hz: ceil(500ms / 66.67ms) = 8 ticks = 533ms.
        /// </para>
        /// </summary>
        public static int AttackCooldownTicks(int tickRate)
        {
            if (tickRate <= 0) tickRate = DefaultTickRate;
            int ticks = (AttackCooldownMs * tickRate + 999) / 1000; // ceil
            return ticks < 1 ? 1 : ticks;
        }

        /// <summary>Minimum damage dealt per attack (floor).</summary>
        public const int MinDamage = 1;

        // ── Simulation ──

        /// <summary>Default Area of Interest radius for snapshot filtering.</summary>
        public const float DefaultAoiRadius = 50.0f;

        /// <summary>Default simulation tick rate (Hz).</summary>
        public const int DefaultTickRate = 15;

        /// <summary>
        /// Default number of delta snapshots between full keyframes. At 15Hz a keyframe
        /// lands every 2 seconds, bounding how long a client can stay desynced if it ever
        /// misses a delta. Set to 0 or less to disable delta encoding entirely.
        /// </summary>
        public const int DefaultKeyframeInterval = 30;
    }
}
