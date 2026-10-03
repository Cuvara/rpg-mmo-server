using System;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;

namespace Shared.GameLogic.Systems
{
    /// <summary>Outcome of <see cref="StatusSet.Apply"/>.</summary>
    public enum StatusApplyResult
    {
        /// <summary>
        /// Nothing changed: the set was full and did not already hold this status, or the
        /// definition was null.
        /// </summary>
        Rejected = 0,

        /// <summary>The status was not active and now is, with one stack.</summary>
        Added = 1,

        /// <summary>The status was active below its stack cap; one stack was added and the duration refreshed.</summary>
        Stacked = 2,

        /// <summary>The status was active at its stack cap; only the duration was refreshed.</summary>
        Refreshed = 3,
    }

    /// <summary>What a <see cref="StatusTickResult"/> reports.</summary>
    public enum StatusTickKind
    {
        /// <summary>Never produced; the default of an unwritten buffer slot.</summary>
        None = 0,

        /// <summary>A damage-over-time application of <see cref="StatusTickResult.Amount"/>, before mitigation.</summary>
        PeriodicDamage = 1,

        /// <summary>A heal-over-time application of <see cref="StatusTickResult.Amount"/>, before clamping to missing health.</summary>
        PeriodicHeal = 2,

        /// <summary>The status reached its expiry tick and was removed from the set.</summary>
        Expired = 3,
    }

    /// <summary>One thing that happened to a <see cref="StatusSet"/> during <see cref="StatusSet.Tick"/>.</summary>
    /// <remarks>
    /// The set reports, it does not act: it has no access to the entity's health and must
    /// not, because the same tick also resolves direct damage and the caller owns the order
    /// the two are applied in. A caller turns these into HP changes and into
    /// <see cref="GameEventData"/> (periodic damage/heal with <see cref="GameEventFlags.Periodic"/>
    /// and <see cref="GameEventData.EffectId"/>, or <see cref="GameEventType.StatusRemoved"/>).
    /// </remarks>
    public readonly struct StatusTickResult
    {
        /// <summary>Builds a result.</summary>
        public StatusTickResult(StatusTickKind kind, uint statusId, int amount, int stacks, string? sourceId)
        {
            Kind = kind;
            StatusId = statusId;
            Amount = amount;
            Stacks = stacks;
            SourceId = sourceId;
        }

        /// <summary>What happened.</summary>
        public StatusTickKind Kind { get; }

        /// <summary>Content id of the status involved.</summary>
        public uint StatusId { get; }

        /// <summary>
        /// For periodic results, the per-stack amount times <see cref="Stacks"/>, before
        /// mitigation; 0 for <see cref="StatusTickKind.Expired"/>.
        /// </summary>
        public int Amount { get; }

        /// <summary>Stacks the status held when this was produced.</summary>
        public int Stacks { get; }

        /// <summary>Id of the entity that last applied the status, or null.</summary>
        public string? SourceId { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Kind}(status={StatusId}, amount={Amount}, stacks={Stacks})";
    }

    /// <summary>One active status on an entity, as <see cref="StatusSet"/> holds it.</summary>
    public readonly struct StatusInstance
    {
        /// <summary>Builds an instance. Normally only <see cref="StatusSet"/> does this.</summary>
        public StatusInstance(
            StatusDefinition definition, int stacks, ulong appliedTick, ulong expiresTick,
            ulong nextPeriodicTick, string? sourceId)
        {
            Definition = definition;
            Stacks = stacks;
            AppliedTick = appliedTick;
            ExpiresTick = expiresTick;
            NextPeriodicTick = nextPeriodicTick;
            SourceId = sourceId;
        }

        /// <summary>The content definition.</summary>
        public StatusDefinition Definition { get; }

        /// <summary>Content id, the wire's <c>StatusEffect.effect_id</c>.</summary>
        public uint StatusId => Definition.Id;

        /// <summary>Current stack count, 1 to <see cref="StatusDefinition.MaxStacks"/>.</summary>
        public int Stacks { get; }

        /// <summary>Tick of the FIRST application; re-applying does not move it.</summary>
        public ulong AppliedTick { get; }

        /// <summary>Tick the status ends on; 0 means "until removed". Matches the wire's <c>expires_tick</c>.</summary>
        public ulong ExpiresTick { get; }

        /// <summary>Tick of the next periodic application; 0 for a non-periodic status.</summary>
        public ulong NextPeriodicTick { get; }

        /// <summary>Id of the entity that last applied the status, or null for "none / the world".</summary>
        public string? SourceId { get; }
    }

    /// <summary>
    /// The active status effects of ONE entity: a fixed-capacity, allocation-free container
    /// with deterministic stacking, ticking, expiry, stat modification and crowd-control
    /// queries (ADR-30).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Allocation.</b> The slot array is allocated once, in the constructor. Every other
    /// member — <see cref="Apply"/>, <see cref="Tick"/>, <see cref="Remove"/>, the effective-stat
    /// and crowd-control queries, <see cref="CopyTo"/> — allocates nothing, so a server can
    /// keep one per entity in a side table and call it on the tick thread.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> Time is the simulation tick passed in by the caller, never a clock.
    /// Slots are kept in first-application order (removal compacts without reordering), so
    /// <see cref="Tick"/> reports results in the same order on every run and on both sides.
    /// Stat math is 64-bit integer fixed point (permille); the only float operation is the
    /// final speed scale, done with the explicit per-operation casts
    /// <see cref="MovementSystem"/> uses.
    /// </para>
    /// <para>
    /// <b>One instance per status id.</b> Re-applying an active status adds a stack (up to
    /// <see cref="StatusDefinition.MaxStacks"/>) and refreshes the duration; it never creates
    /// a second instance, and the most recent applier becomes <see cref="StatusInstance.SourceId"/>.
    /// Re-applying does not reset the periodic rhythm, so a DoT cannot be delayed forever by
    /// being refreshed just before it ticks.
    /// </para>
    /// <para>Not thread-safe; owned by the tick thread.</para>
    /// </remarks>
    public sealed class StatusSet
    {
        /// <summary>Slots per entity when no capacity is given.</summary>
        public const int DefaultCapacity = 16;

        private readonly StatusInstance[] _slots;
        private int _count;

        /// <summary>Creates an empty set with <see cref="DefaultCapacity"/> slots.</summary>
        public StatusSet() : this(DefaultCapacity)
        {
        }

        /// <summary>Creates an empty set with <paramref name="capacity"/> slots.</summary>
        public StatusSet(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
            _slots = new StatusInstance[capacity];
        }

        /// <summary>Maximum number of distinct statuses this set can hold.</summary>
        public int Capacity => _slots.Length;

        /// <summary>Number of active statuses.</summary>
        public int Count => _count;

        /// <summary>
        /// Smallest result buffer that <see cref="Tick"/> can never fill when it is called
        /// every tick: one periodic result and one expiry per slot.
        /// </summary>
        public int MaxResultsPerTick => _slots.Length * 2;

        /// <summary>The active status at <paramref name="index"/> (0 to <see cref="Count"/> - 1), in application order.</summary>
        public StatusInstance GetAt(int index)
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _slots[index];
        }

        /// <summary>True when status <paramref name="statusId"/> is active.</summary>
        public bool Contains(uint statusId) => IndexOf(statusId) >= 0;

        /// <summary>Reads the active instance of <paramref name="statusId"/>.</summary>
        public bool TryGet(uint statusId, out StatusInstance instance)
        {
            int i = IndexOf(statusId);
            if (i < 0)
            {
                instance = default;
                return false;
            }

            instance = _slots[i];
            return true;
        }

        /// <summary>
        /// Applies one stack of <paramref name="definition"/> at <paramref name="currentTick"/>.
        /// </summary>
        /// <param name="definition">The status to apply.</param>
        /// <param name="currentTick">Current simulation tick.</param>
        /// <param name="sourceId">Entity applying it, or null for "none / the world".</param>
        /// <returns>What changed. <see cref="StatusApplyResult.Rejected"/> leaves the set untouched.</returns>
        public StatusApplyResult Apply(StatusDefinition definition, ulong currentTick, string? sourceId)
        {
            if (definition == null) return StatusApplyResult.Rejected;

            ulong expires = StatusLogic.ExpiryTick(definition, currentTick);
            int i = IndexOf(definition.Id);
            if (i >= 0)
            {
                StatusInstance cur = _slots[i];
                int max = definition.MaxStacks < 1 ? 1 : definition.MaxStacks;
                bool stacked = cur.Stacks < max;
                _slots[i] = new StatusInstance(
                    definition,
                    stacked ? cur.Stacks + 1 : cur.Stacks,
                    cur.AppliedTick,
                    expires,
                    cur.NextPeriodicTick,
                    sourceId);
                return stacked ? StatusApplyResult.Stacked : StatusApplyResult.Refreshed;
            }

            if (_count == _slots.Length) return StatusApplyResult.Rejected;

            _slots[_count++] = new StatusInstance(
                definition,
                1,
                currentTick,
                expires,
                StatusLogic.FirstPeriodicTick(definition, currentTick),
                sourceId);
            return StatusApplyResult.Added;
        }

        /// <summary>
        /// Removes status <paramref name="statusId"/> (cleanse, dispel, death). Returns false
        /// when it was not active.
        /// </summary>
        public bool Remove(uint statusId)
        {
            int i = IndexOf(statusId);
            if (i < 0) return false;
            RemoveAt(i);
            return true;
        }

        /// <summary>Removes every status (death, map transfer).</summary>
        public void Clear()
        {
            Array.Clear(_slots, 0, _count);
            _count = 0;
        }

        /// <summary>
        /// Advances the set to <paramref name="currentTick"/>: emits every periodic application
        /// that has come due and every expiry, removing expired statuses.
        /// </summary>
        /// <param name="currentTick">Current simulation tick. Call once per tick, in increasing order.</param>
        /// <param name="results">
        /// Caller-owned buffer. Sized <see cref="MaxResultsPerTick"/> it can never fill when
        /// the set is ticked every tick.
        /// </param>
        /// <returns>Number of results written, in slot (first-application) order.</returns>
        /// <remarks>
        /// <para>
        /// <b>Order within a status:</b> periodic applications first, then the expiry. A
        /// periodic application due ON the expiry tick still happens, so a 10-tick DoT with a
        /// 2-tick interval applies five times (ticks +2, +4, +6, +8, +10).
        /// </para>
        /// <para>
        /// <b>Catch-up.</b> If the caller skipped ticks, every missed application is emitted,
        /// in order. <b>A full buffer</b> stops processing without losing anything: state is
        /// advanced only for results actually written, so calling again with the same tick
        /// and a fresh buffer continues where this call stopped.
        /// </para>
        /// </remarks>
        public int Tick(ulong currentTick, Span<StatusTickResult> results)
        {
            int written = 0;
            int i = 0;
            while (i < _count)
            {
                StatusInstance s = _slots[i];
                StatusDefinition def = s.Definition;
                PeriodicSpec p = def.Periodic;

                if (p.IsPeriodic && p.IntervalTicks > 0)
                {
                    ulong next = s.NextPeriodicTick;
                    while (next <= currentTick && (s.ExpiresTick == 0 || next <= s.ExpiresTick))
                    {
                        if (written == results.Length)
                        {
                            _slots[i] = WithNextPeriodic(s, next);
                            return written;
                        }

                        results[written++] = new StatusTickResult(
                            p.Kind == PeriodicKind.Heal ? StatusTickKind.PeriodicHeal : StatusTickKind.PeriodicDamage,
                            def.Id,
                            StatusLogic.Scale(p.Amount, s.Stacks),
                            s.Stacks,
                            s.SourceId);
                        next += (ulong)p.IntervalTicks;
                    }

                    if (next != s.NextPeriodicTick)
                    {
                        s = WithNextPeriodic(s, next);
                        _slots[i] = s;
                    }
                }

                if (s.ExpiresTick != 0 && currentTick >= s.ExpiresTick)
                {
                    if (written == results.Length) return written;

                    results[written++] = new StatusTickResult(StatusTickKind.Expired, def.Id, 0, s.Stacks, s.SourceId);
                    RemoveAt(i);
                    continue; // the next status has moved into slot i
                }

                i++;
            }

            return written;
        }

        /// <summary>Every crowd-control flag imposed by an active status.</summary>
        public CrowdControl ActiveCrowdControl
        {
            get
            {
                CrowdControl cc = CrowdControl.None;
                for (int i = 0; i < _count; i++) cc |= _slots[i].Definition.CrowdControl;
                return cc;
            }
        }

        /// <summary>False while stunned or rooted. Gate movement input (and the motor) on this.</summary>
        public bool CanMove => (ActiveCrowdControl & (CrowdControl.Stun | CrowdControl.Root)) == 0;

        /// <summary>False while stunned or silenced. Gate ability casts on this.</summary>
        public bool CanCast => (ActiveCrowdControl & (CrowdControl.Stun | CrowdControl.Silence)) == 0;

        /// <summary>False while stunned. Gate basic attacks and other actions on this.</summary>
        public bool CanAct => (ActiveCrowdControl & CrowdControl.Stun) == 0;

        /// <summary>
        /// Strongest active slow in permille (0 when not slowed). Slows do not add up and do
        /// not scale with stacks; the strongest one wins.
        /// </summary>
        public int SlowPermille
        {
            get
            {
                int slow = 0;
                for (int i = 0; i < _count; i++)
                {
                    StatusDefinition d = _slots[i].Definition;
                    if ((d.CrowdControl & CrowdControl.Slow) != 0 && d.SlowPermille > slow) slow = d.SlowPermille;
                }

                return slow > 1000 ? 1000 : slow;
            }
        }

        /// <summary>
        /// Total speed factor in permille: speed modifiers and the strongest slow combined
        /// (1000 = unchanged). Does NOT include root or stun, which <see cref="CanMove"/>
        /// reports separately — an immobile entity still has a speed to replicate.
        /// </summary>
        public int SpeedMultiplierPermille
        {
            get
            {
                SumModifiers(StatModifierTarget.Speed, 0u, out _, out long mul);
                long factor = StatusLogic.ClampFactor(1000L + mul);
                long slowed = factor * (1000L - SlowPermille) / 1000L;
                return (int)slowed;
            }
        }

        /// <summary>Attack after modifiers; never below 0.</summary>
        public int EffectiveAttack(int baseAttack) => Effective(StatModifierTarget.Attack, 0u, baseAttack, 0);

        /// <summary>Defense after modifiers; never below 0.</summary>
        public int EffectiveDefense(int baseDefense) => Effective(StatModifierTarget.Defense, 0u, baseDefense, 0);

        /// <summary>Max HP after modifiers; never below 1.</summary>
        public int EffectiveMaxHp(int baseMaxHp) => Effective(StatModifierTarget.MaxHp, 0u, baseMaxHp, 1);

        /// <summary>
        /// Content stat <paramref name="statId"/> after modifiers. Not floored: a content stat
        /// (a resistance, say) may legitimately go negative.
        /// </summary>
        public int EffectiveStat(uint statId, int baseValue) =>
            Effective(StatModifierTarget.ContentStat, statId, baseValue, int.MinValue);

        /// <summary>
        /// Speed in world units per second after speed modifiers and slows
        /// (<see cref="SpeedMultiplierPermille"/>). Returns <paramref name="baseSpeed"/>
        /// bit-for-bit when the factor is exactly 1000.
        /// </summary>
        public float EffectiveSpeed(float baseSpeed)
        {
            int pm = SpeedMultiplierPermille;
            if (pm == 1000) return baseSpeed;

            // Per-operation casts: see MovementSystem. pm <= 1_000_000, so (float)pm is exact.
            return (float)((float)(baseSpeed * (float)pm) / 1000f);
        }

        /// <summary>
        /// Writes the active statuses as snapshot entries, in application order. Returns the
        /// number written, at most <c>min(Count, destination.Length)</c>.
        /// </summary>
        public int CopyTo(Span<StatusEffectData> destination)
        {
            int n = _count < destination.Length ? _count : destination.Length;
            for (int i = 0; i < n; i++)
            {
                StatusInstance s = _slots[i];
                destination[i] = new StatusEffectData(s.StatusId, (uint)s.Stacks, s.ExpiresTick, s.SourceId);
            }

            return n;
        }

        private int Effective(StatModifierTarget target, uint statId, int baseValue, int floor)
        {
            SumModifiers(target, statId, out long add, out long mul);
            if (add == 0 && mul == 0) return baseValue < floor ? floor : baseValue;

            long sum = StatusLogic.ClampInt((long)baseValue + add);
            long v = sum * StatusLogic.ClampFactor(1000L + mul) / 1000L;
            v = StatusLogic.ClampInt(v);
            return v < floor ? floor : (int)v;
        }

        private void SumModifiers(StatModifierTarget target, uint statId, out long add, out long mul)
        {
            add = 0;
            mul = 0;
            for (int i = 0; i < _count; i++)
            {
                StatusInstance s = _slots[i];
                StatusDefinition d = s.Definition;
                for (int m = 0; m < d.ModifierCount; m++)
                {
                    StatModifier mod = d.GetModifier(m);
                    if (!mod.Affects(target, statId)) continue;
                    add += (long)mod.Add * s.Stacks;
                    mul += (long)mod.MultiplierPermille * s.Stacks;
                }
            }
        }

        private int IndexOf(uint statusId)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_slots[i].Definition.Id == statusId) return i;
            }

            return -1;
        }

        private void RemoveAt(int index)
        {
            // Shift down rather than swap-with-last: order is part of the determinism
            // contract (Tick reports in slot order).
            for (int j = index + 1; j < _count; j++) _slots[j - 1] = _slots[j];
            _count--;
            _slots[_count] = default;
        }

        private static StatusInstance WithNextPeriodic(in StatusInstance s, ulong next) =>
            new StatusInstance(s.Definition, s.Stacks, s.AppliedTick, s.ExpiresTick, next, s.SourceId);
    }

    /// <summary>
    /// Stateless status rules shared by <see cref="StatusSet"/> and its callers.
    /// </summary>
    public static class StatusLogic
    {
        /// <summary>
        /// Looks up <paramref name="statusId"/> in <paramref name="content"/> and applies it to
        /// <paramref name="target"/>. <see cref="StatusApplyResult.Rejected"/> when the id is
        /// unknown — validated content never references one, but an id from elsewhere might.
        /// </summary>
        public static StatusApplyResult Apply(
            ContentDatabase content, uint statusId, StatusSet target, ulong currentTick, string? sourceId)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (!content.TryGetStatus(statusId, out var def) || def == null) return StatusApplyResult.Rejected;
            return target.Apply(def, currentTick, sourceId);
        }

        /// <summary>
        /// Tick a status applied at <paramref name="currentTick"/> ends on; 0 for a status that
        /// lasts until removed (<see cref="StatusDefinition.DurationTicks"/> of 0 or less).
        /// </summary>
        public static ulong ExpiryTick(StatusDefinition definition, ulong currentTick) =>
            definition.DurationTicks <= 0 ? 0UL : currentTick + (ulong)definition.DurationTicks;

        /// <summary>
        /// Tick of the first periodic application for a status applied at
        /// <paramref name="currentTick"/>: one interval later. 0 when not periodic.
        /// </summary>
        public static ulong FirstPeriodicTick(StatusDefinition definition, ulong currentTick)
        {
            PeriodicSpec p = definition.Periodic;
            return p.IsPeriodic && p.IntervalTicks > 0 ? currentTick + (ulong)p.IntervalTicks : 0UL;
        }

        internal static int Scale(int amount, int stacks) => (int)ClampInt((long)amount * stacks);

        internal static long ClampInt(long v) =>
            v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : v;

        // A factor below zero would invert a stat; above 1000x it would only overflow.
        internal static long ClampFactor(long permille) =>
            permille < 0 ? 0 : permille > 1_000_000 ? 1_000_000 : permille;
    }
}
