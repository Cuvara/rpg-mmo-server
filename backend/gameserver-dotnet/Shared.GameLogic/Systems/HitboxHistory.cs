using System;
using Shared.GameLogic.Components;

namespace Shared.GameLogic.Systems
{
    /// <summary>
    /// Ring buffer of past hitbox positions for lag compensation (ADR-29 decision 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every tick the server records the capsule base (feet) of every damageable entity,
    /// keyed by an int the caller chooses (an entity handle). A hit caused by an input is
    /// then tested against targets rewound to the instant that client was rendering —
    /// <c>render_tick</c> + <c>render_alpha</c> — interpolated between the two recorded ticks
    /// that bracket it, which is exactly how the client drew them.
    /// </para>
    /// <para>
    /// Only positions are stored. Capsule radius and height are per-entity constants the
    /// caller already has; storing them per tick would multiply the memory for no gain.
    /// </para>
    /// <para>
    /// <b>Allocation-free after construction.</b> All storage is allocated once, sized from
    /// the tick rate, the rewind cap and the most entities recorded per tick. Recording
    /// overwrites the oldest tick in place. Lookups are a linear scan of one tick's entries,
    /// cheap for the handful of hit tests an input causes; a caller that rewinds many
    /// targets for one shot pays O(entities) per target.
    /// </para>
    /// <para>
    /// Not thread-safe: written and read by the simulation thread only.
    /// </para>
    /// </remarks>
    public sealed class HitboxHistory
    {
        /// <summary>
        /// Furthest lag compensation rewinds, in milliseconds (ADR-29 decision 4). Bounds
        /// "shot around the corner" at the cost of very high-latency players leading shots.
        /// </summary>
        public const int MaxRewindMs = 200;

        /// <summary>
        /// Ticks kept beyond the rewind window: one so the tick at the far edge still has its
        /// <c>tick + 1</c> partner to interpolate toward, one so the tick being written does
        /// not evict a tick still in the window.
        /// </summary>
        public const int CapacityMargin = 2;

        private readonly ulong[] _slotTick;   // tick stored in each slot; 0 = empty
        private readonly int[] _slotCount;
        private readonly int[] _keys;         // slot * _maxEntities + i
        private readonly Vec3[] _positions;   // parallel to _keys
        private readonly int _maxEntities;

        /// <summary>Number of ticks the buffer holds.</summary>
        public int CapacityTicks { get; }

        /// <summary>Most entities one tick can record; extra records are refused.</summary>
        public int MaxEntitiesPerTick => _maxEntities;

        /// <summary>
        /// Create a history sized for <paramref name="tickRate"/> Hz and a rewind cap of
        /// <paramref name="maxRewindMs"/>.
        /// </summary>
        /// <param name="tickRate">Rate the history is recorded at (the critical rate, ADR-13).</param>
        /// <param name="maxEntitiesPerTick">Most damageable entities recorded per tick.</param>
        /// <param name="maxRewindMs">Rewind cap; defaults to <see cref="MaxRewindMs"/>.</param>
        public HitboxHistory(int tickRate, int maxEntitiesPerTick, int maxRewindMs = MaxRewindMs)
        {
            if (tickRate <= 0) throw new ArgumentOutOfRangeException(nameof(tickRate), "tick rate must be positive");
            if (maxEntitiesPerTick <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntitiesPerTick), "must be positive");
            if (maxRewindMs < 0) throw new ArgumentOutOfRangeException(nameof(maxRewindMs), "must not be negative");

            CapacityTicks = CapacityFor(tickRate, maxRewindMs);
            _maxEntities = maxEntitiesPerTick;
            _slotTick = new ulong[CapacityTicks];
            _slotCount = new int[CapacityTicks];
            _keys = new int[CapacityTicks * maxEntitiesPerTick];
            _positions = new Vec3[CapacityTicks * maxEntitiesPerTick];
        }

        /// <summary>
        /// Ticks needed to cover <paramref name="maxRewindMs"/> at <paramref name="tickRate"/>:
        /// the window rounded up, plus <see cref="CapacityMargin"/>.
        /// </summary>
        public static int CapacityFor(int tickRate, int maxRewindMs)
        {
            if (tickRate <= 0) tickRate = GameConstants.DefaultTickRate;
            if (maxRewindMs < 0) maxRewindMs = 0;
            int window = (int)(((long)maxRewindMs * tickRate + 999) / 1000); // ceil
            return window + CapacityMargin;
        }

        /// <summary>
        /// Start recording <paramref name="tick"/>, discarding whatever its slot held. Optional:
        /// <see cref="Record"/> begins a tick implicitly the first time it sees it. Call it
        /// explicitly when a tick may legitimately record nobody, so a stale tick from one
        /// lap ago is not left answering for it.
        /// </summary>
        public void BeginTick(ulong tick)
        {
            if (tick == 0) return; // 0 means "empty slot" and "no rewind"; never recorded.
            int slot = SlotOf(tick);
            _slotTick[slot] = tick;
            _slotCount[slot] = 0;
        }

        /// <summary>
        /// Record the capsule base of entity <paramref name="key"/> at <paramref name="tick"/>.
        /// Recording the same key twice in one tick overwrites the first.
        /// </summary>
        /// <returns>False when the tick is 0 or already holds <see cref="MaxEntitiesPerTick"/> entries.</returns>
        public bool Record(ulong tick, int key, in Vec3 basePos)
        {
            if (tick == 0) return false;
            int slot = SlotOf(tick);
            if (_slotTick[slot] != tick)
            {
                _slotTick[slot] = tick;
                _slotCount[slot] = 0;
            }

            int start = slot * _maxEntities;
            int count = _slotCount[slot];
            for (int i = 0; i < count; i++)
            {
                if (_keys[start + i] == key)
                {
                    _positions[start + i] = basePos;
                    return true;
                }
            }

            if (count >= _maxEntities) return false;
            _keys[start + count] = key;
            _positions[start + count] = basePos;
            _slotCount[slot] = count + 1;
            return true;
        }

        /// <summary>Forget everything (map change, server reset).</summary>
        public void Clear()
        {
            Array.Clear(_slotTick, 0, _slotTick.Length);
            Array.Clear(_slotCount, 0, _slotCount.Length);
        }

        /// <summary>True when <paramref name="tick"/> is still held by the buffer.</summary>
        public bool HasTick(ulong tick) => tick != 0 && _slotTick[SlotOf(tick)] == tick;

        /// <summary>
        /// Position of entity <paramref name="key"/> at <paramref name="tick"/> +
        /// <paramref name="alpha"/>, interpolated between the records at <c>tick</c> and
        /// <c>tick + 1</c>.
        /// </summary>
        /// <param name="key">Entity key used when recording.</param>
        /// <param name="tick">Base tick (normally from <see cref="ClampRewind"/>).</param>
        /// <param name="alpha">Fraction toward <c>tick + 1</c>, clamped to [0, 1]; NaN reads as 0.</param>
        /// <param name="pos">The interpolated position; default when not found.</param>
        /// <returns>
        /// False when the entity has no record at <paramref name="tick"/> (not yet spawned,
        /// or the tick has fallen out of the buffer). When <c>tick + 1</c> is missing — the
        /// entity despawned, or <c>tick</c> is the newest — the position at <c>tick</c> is
        /// returned unchanged rather than extrapolated.
        /// </returns>
        public bool TryGetAt(int key, ulong tick, float alpha, out Vec3 pos)
        {
            pos = default;
            if (!TryGetExact(key, tick, out Vec3 a)) return false;

            if (!(alpha > 0f) || tick == ulong.MaxValue || !TryGetExact(key, tick + 1, out Vec3 b))
            {
                pos = a;
                return true;
            }

            if (alpha > 1f) alpha = 1f;
            pos = Vec3.Lerp(a, b, alpha);
            return true;
        }

        /// <summary>Position recorded for <paramref name="key"/> at exactly <paramref name="tick"/>.</summary>
        public bool TryGetExact(int key, ulong tick, out Vec3 pos)
        {
            pos = default;
            if (tick == 0) return false;
            int slot = SlotOf(tick);
            if (_slotTick[slot] != tick) return false;

            int start = slot * _maxEntities;
            int count = _slotCount[slot];
            for (int i = 0; i < count; i++)
            {
                if (_keys[start + i] == key)
                {
                    pos = _positions[start + i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The tick a hit test rewinds to, per ADR-29 decision 4.
        /// </summary>
        /// <param name="currentTick">The server's current tick.</param>
        /// <param name="renderTick">InputMessage.render_tick.</param>
        /// <param name="tickRate">Tick rate the history is recorded at.</param>
        /// <param name="maxRewindMs">Rewind cap, normally <see cref="MaxRewindMs"/>.</param>
        /// <returns>
        /// <paramref name="currentTick"/> when <paramref name="renderTick"/> is 0 ("not sent":
        /// no rewind) or in the future (a client cannot have rendered a tick the server has
        /// not simulated, so the claim is ignored rather than trusted). Otherwise the render
        /// tick, but no further back than the whole number of ticks that fit in
        /// <paramref name="maxRewindMs"/> — rounded <b>down</b>, so the cap is never exceeded.
        /// </returns>
        public static ulong ClampRewindTick(ulong currentTick, ulong renderTick, int tickRate, int maxRewindMs)
        {
            if (renderTick == 0 || renderTick > currentTick || tickRate <= 0) return currentTick;
            if (maxRewindMs < 0) maxRewindMs = 0;
            ulong maxTicks = (ulong)((long)maxRewindMs * tickRate / 1000);
            ulong back = currentTick - renderTick;
            return back > maxTicks ? currentTick - maxTicks : renderTick;
        }

        /// <summary>
        /// <see cref="ClampRewindTick"/> plus the matching alpha: the full instant to pass to
        /// <see cref="TryGetAt"/>.
        /// </summary>
        /// <remarks>
        /// The alpha is zeroed whenever the tick was clamped or is the current tick: at the
        /// cap a positive alpha would only move <i>forward</i> (fine, but not what the client
        /// asked for, since it asked for something older), and at the current tick there is
        /// no <c>tick + 1</c> yet. A NaN or out-of-range alpha is clamped to [0, 1).
        /// </remarks>
        public static void ClampRewind(
            ulong currentTick, ulong renderTick, float renderAlpha, int tickRate, int maxRewindMs,
            out ulong tick, out float alpha)
        {
            tick = ClampRewindTick(currentTick, renderTick, tickRate, maxRewindMs);
            if (tick != renderTick || tick == currentTick || !(renderAlpha > 0f))
            {
                alpha = 0f;
                return;
            }

            alpha = renderAlpha < 1f ? renderAlpha : 0.99999994f; // largest float below 1
        }

        private int SlotOf(ulong tick) => (int)(tick % (ulong)CapacityTicks);
    }
}
