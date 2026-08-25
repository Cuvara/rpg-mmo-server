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
        /// Longest a held movement direction keeps producing steps after the last input
        /// that refreshed it, in milliseconds — the silence timeout.
        ///
        /// <para><b>The name is historical and the value is not.</b> This used to bound how
        /// much elapsed time one movement step could "bank": a step advanced the entity by
        /// the time since it last moved, so that a burst of inputs coalesced into one tick
        /// was not silently reduced to one tick of travel (#100). Banking is gone. Every
        /// step is one tick now, and the time a burst loses is recovered by stepping on each
        /// tick of the gap instead. The same 250ms budget therefore bounds the length of the
        /// coast rather than the size of a step. Renaming the constant means releasing this
        /// package and bumping the client's manifest and lock, so it is a separate
        /// change.</para>
        ///
        /// <para><b>Why banking went.</b> It restored the correct distance and was wrong by
        /// every other measure. Measured against a live server it produced a 1.36-unit step
        /// where a normal one is 0.083 — read by a player as the avatar jumping — and a
        /// correctly predicting client was snapped back by it, because the client never took
        /// that step itself. Distance was right and the frames were unplayable.</para>
        ///
        /// <para><b>Why 250ms.</b> It has to exceed the longest gap a supported client
        /// leaves between packets, or that client stalls for the remainder of every gap: a
        /// client sending at the world rate (15Hz, 66ms apart) whose packets clump into
        /// fours leaves a 266ms gap between bursts, which <c>MaxBankedMovementTicks(15) = 4</c>
        /// ticks of 66.7ms covers exactly. Measured directly — with the coast one tick
        /// shorter than that, a bursting 15Hz client against a 15Hz server travels 5.00 units
        /// where 6.00 is owed.</para>
        ///
        /// <para>It is deliberately close to, and slightly above, the ~200ms dead-reckoning
        /// limit the netcode model sets for bad mobile networks: both answer "how long may
        /// the server move on information it no longer has". This one is the safer of the
        /// two, because the client demonstrably <i>was</i> sending — the packets were
        /// arriving until they stopped — whereas dead reckoning extrapolates input that was
        /// never sent. That is the whole of the justification for the gap between the two
        /// numbers, and it is why this one is not larger still.</para>
        ///
        /// <para><b>Only lost packets are coasted through.</b> A deadzone input clears the
        /// held direction outright, so a player who releases the stick does not drift for a
        /// quarter second. A client must send its vector on every input tick, including the
        /// zero on release; the server cannot otherwise tell "I stopped" from "my packets
        /// stopped".</para>
        /// </summary>
        public const int MaxBankedMovementMs = 250;

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
