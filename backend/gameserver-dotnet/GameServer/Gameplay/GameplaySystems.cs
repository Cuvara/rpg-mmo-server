using System;
using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;
using Shared.GameLogic.Systems;
using Shared.GameLogic.World;

namespace GameServer.Gameplay;

/// <summary>
/// The per-base-tick gameplay systems of the Core v3 simulation (ADR-28..31): the damageable
/// roster and lag-compensation history, projectiles, status ticking and item despawn. Run by
/// the tick loop inside the CRITICAL write scope (every base tick), around input processing:
/// <see cref="BeginTick"/> before inputs, <see cref="Step"/> after inputs and held movement.
/// </summary>
/// <remarks>
/// <para><b>Why the critical group.</b> A projectile crosses up to <c>speed / Hz</c> units per
/// step; at the 15 Hz world rate a 30 u/s bolt jumps 2 units — more than a capsule's width —
/// between tests, which a swept test survives but a client watching the impact does not.
/// Status durations and periodic intervals are counted in base ticks (like cooldowns), so they
/// are stepped on the timeline that counts them. History must be recorded every base tick
/// because <c>render_tick</c> is a base tick. All three are a few comparisons per active
/// projectile/status/target; none is per-viewer.</para>
/// <para><b>When history is recorded.</b> At the START of base tick T, labelled T-1: nothing
/// moves between the end of one tick and the start of the next except joins and leaves, so this
/// is the end-of-tick position of every damageable entity — including enemies, which the world
/// group moves AFTER the critical scope — without taking the write lock a second time per tick.
/// A rewind to T-1 therefore sees exactly what the snapshot for T-1 showed.</para>
/// <para>State lives in the world (<see cref="GameplayState"/>); this class holds only
/// scratch buffers.</para>
/// </remarks>
public sealed class GameplaySystems
{
    private readonly CombatResolver _combat;
    private readonly float _dt;

    [SimulationScratch]
    private EntityHandle[] _handles = Array.Empty<EntityHandle>();

    [SimulationScratch]
    private readonly StatusTickResult[] _statusResults = new StatusTickResult[StatusSet.DefaultCapacity * 2];

    /// <summary>Builds the systems.</summary>
    /// <param name="combat">Effect/damage/death resolver shared with the input path.</param>
    /// <param name="dt">Base-tick timestep in seconds (1 / critical Hz).</param>
    public GameplaySystems(CombatResolver combat, float dt)
    {
        _combat = combat;
        _dt = dt;
    }

    /// <summary>
    /// Start of a base tick: rebuild the damageable roster and record every roster entry's
    /// position into the hitbox history as tick <c>T-1</c>.
    /// </summary>
    public void BeginTick(WorldWriter writer, ulong tick) => Rebuild(writer, tick, recordHistory: true);

    /// <summary>
    /// Rebuild the roster for <paramref name="tick"/> without recording history: for callers
    /// that drive inputs or <see cref="Step"/> outside the tick loop.
    /// </summary>
    public void BuildRoster(WorldWriter writer, ulong tick) => Rebuild(writer, tick, recordHistory: false);

    private void Rebuild(WorldWriter writer, ulong tick, bool recordHistory)
    {
        GameplayState g = writer.Gameplay;
        g.CurrentTick = tick;
        g.ClearRoster();
        g.RosterTick = tick;

        int count = writer.QueryAll(_handles);
        if (count > _handles.Length)
        {
            _handles = new EntityHandle[count + (count >> 2) + 16];
            count = writer.QueryAll(_handles);
        }

        HitboxHistory? history = recordHistory && tick > 1 ? g.History : null;
        ulong recordTick = tick - 1;
        history?.BeginTick(recordTick);

        int n = Math.Min(count, _handles.Length);
        for (int i = 0; i < n; i++)
        {
            ref readonly EntityHandle h = ref _handles[i];
            SimRecord? rec = writer.RecordOf(h);
            if (rec is not { Kind: SimKind.Actor }) continue;

            ref Health health = ref writer.HealthOf(h);
            if (health.Dead) continue;

            ref EntityIdRef id = ref writer.IdRefOf(h);
            bool linkdead = rec.Type == "player" && writer.PlayerTagOf(h).Linkdead;
            g.AddToRoster(new RosterEntry(h, id.Stable, rec.Type ?? string.Empty, linkdead));

            if (history != null && !linkdead)
            {
                ref Position p = ref writer.PositionOf(h);
                history.Record(recordTick, id.Stable, new Vec3(p.Value.X, p.Value.Y, p.Z));
            }
        }
    }

    /// <summary>
    /// After inputs and held movement: step projectiles, tick statuses, despawn expired items.
    /// </summary>
    public void Step(WorldWriter writer, ulong tick, MapGeometry geometry)
    {
        writer.Gameplay.CurrentTick = tick;
        StepProjectiles(writer, tick, geometry);
        StepStatuses(writer, tick);
        StepItems(writer, tick);
    }

    // ── Projectiles ──────────────────────────────────────────────────────────

    private void StepProjectiles(WorldWriter writer, ulong tick, MapGeometry geometry)
    {
        GameplayState g = writer.Gameplay;
        var keys = g.ProjectileKeys;
        int write = 0;
        for (int read = 0; read < keys.Count; read++)
        {
            int key = keys[read];
            SimRecord? rec = g.Get(key);
            if (rec is not { Kind: SimKind.Projectile }) continue;
            if (!rec.Handle.IsValid)
            {
                // Spawn still queued (deferred structural op): step it once it exists.
                keys[write++] = key;
                continue;
            }

            if (!writer.IsAlive(rec.Handle)) continue;

            // A projectile spawned THIS tick takes its first step now; one spawned earlier in
            // the same tick by an input is exactly the "first tick" ADR-29 rewinds.
            bool keep = StepOne(writer, g, rec, tick, geometry);
            if (keep) keys[write++] = key;
            else writer.Despawn(rec.Handle);
        }

        keys.RemoveRange(write, keys.Count - write);
    }

    /// <summary>Advance one projectile; returns false when it is spent (hit, wall, range, bounds).</summary>
    private bool StepOne(WorldWriter writer, GameplayState g, SimRecord rec, ulong tick, MapGeometry geometry)
    {
        ProjectileState before = rec.Projectile;
        bool flying = ProjectileLogic.Step(in before, _dt, geometry, out ProjectileState next, out _);

        // First step: targets rewound to the firing input's render instant (ADR-29 decision 4),
        // already clamped to MaxRewindMs at cast time. Later steps: present-time targets.
        bool rewind = rec.StepsTaken == 0 && rec.RewindTick != 0 && rec.RewindTick < tick;
        rec.StepsTaken++;

        if (TryFirstHit(writer, g, rec, in before.Position, in next.Position, before.Radius,
                rewind, rec.RewindTick, rec.RewindAlpha, out RosterEntry hit))
        {
            string? ownerId = rec.OwnerId;
            int ownerKey = rec.OwnerKey == 0 ? PendingGameEvent.NoKey : rec.OwnerKey;
            _combat.Emit(GameEventData.ProjectileHit(ownerId, writer.IdRefOf(hit.Handle).Value, rec.Ability?.Id ?? 0u),
                ownerKey, hit.Key);
            if (rec.Ability != null)
            {
                _combat.ApplyEffects(writer, rec.Ability, in rec.OwnerSnapshot, ownerKey, hit.Handle, tick);
            }

            return false;
        }

        if (!flying) return false;

        rec.Projectile = next;
        ref Position p = ref writer.PositionOf(rec.Handle);
        p.Value = new Vec2(next.Position.X, next.Position.Y);
        p.Z = next.Position.Z;
        return true;
    }

    /// <summary>
    /// The first damageable entity the swept sphere touches along <paramref name="from"/> →
    /// <paramref name="to"/>, by contact parameter t (ties: roster order). Skips the owner,
    /// non-hostile types, the dead and held players.
    /// </summary>
    internal static bool TryFirstHit(
        WorldWriter writer, GameplayState g, SimRecord projectile, in Vec3 from, in Vec3 to, float radius,
        bool rewind, ulong rewindTick, float rewindAlpha, out RosterEntry hit)
    {
        hit = default;
        float bestT = 2f;
        bool found = false;
        MotorParams motor = g.Motor;
        float reach = radius + motor.CapsuleRadius + motor.CapsuleHeight;
        Vec3 mid = Vec3.Lerp(from, to, 0.5f);
        float half = Vec3.Distance(from, to) * 0.5f;
        float broad = (half + reach) * (half + reach);
        HitboxHistory? history = rewind ? g.History : null;

        ReadOnlySpan<RosterEntry> roster = g.Roster;
        for (int i = 0; i < roster.Length; i++)
        {
            RosterEntry e = roster[i];
            if (e.Linkdead || e.Key == projectile.OwnerKey) continue;
            if (!CombatResolver.IsHostile(projectile.OwnerType, e.Type)) continue;
            if (!writer.IsAlive(e.Handle) || writer.HealthOf(e.Handle).Dead) continue;

            Vec3 feet = TargetFeet(writer, history, in e, rewindTick, rewindAlpha);
            if (Vec3.DistanceSq(mid, feet) > broad) continue;

            if (ProjectileLogic.SegmentCapsuleHit(from, to, radius, feet, motor.CapsuleRadius, motor.CapsuleHeight, out float t)
                && t < bestT)
            {
                bestT = t;
                hit = e;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Where a roster entry's feet are for a hit test: rewound through the history when
    /// <paramref name="history"/> is given and has the entity at that instant, present
    /// otherwise (spawned since, or the history overflowed).
    /// </summary>
    internal static Vec3 TargetFeet(WorldWriter writer, HitboxHistory? history, in RosterEntry e, ulong tick, float alpha)
    {
        if (history != null && history.TryGetAt(e.Key, tick, alpha, out Vec3 past)) return past;
        ref Position p = ref writer.PositionOf(e.Handle);
        return new Vec3(p.Value.X, p.Value.Y, p.Z);
    }

    // ── Statuses ─────────────────────────────────────────────────────────────

    private void StepStatuses(WorldWriter writer, ulong tick)
    {
        GameplayState g = writer.Gameplay;
        ReadOnlySpan<RosterEntry> roster = g.Roster;
        for (int i = 0; i < roster.Length; i++)
        {
            RosterEntry e = roster[i];
            if (!writer.IsAlive(e.Handle)) continue;
            SimRecord? rec = writer.RecordOf(e.Handle);
            if (rec?.Statuses is not { Count: > 0 } set) continue;

            // A full buffer cannot happen when ticked every tick (MaxResultsPerTick), but the
            // set supports resuming, so loop rather than assume.
            while (true)
            {
                int n = set.Tick(tick, _statusResults);
                for (int r = 0; r < n; r++)
                {
                    ApplyStatusResult(writer, in e, rec, in _statusResults[r], tick);
                    // A periodic tick that killed the entity also cleared its statuses (with
                    // their removal events); the rest of this batch describes nothing.
                    if (writer.HealthOf(e.Handle).Dead) break;
                }

                if (n < _statusResults.Length || writer.HealthOf(e.Handle).Dead) break;
            }
        }
    }

    private void ApplyStatusResult(WorldWriter writer, in RosterEntry e, SimRecord rec, in StatusTickResult r, ulong tick)
    {
        switch (r.Kind)
        {
            case StatusTickKind.PeriodicDamage:
            {
                // A held player is out of reach (CORE-BASELINE-V1 §3): a DoT that was running
                // when the connection dropped keeps its rhythm but deals nothing until reattach.
                if (e.Linkdead || writer.HealthOf(e.Handle).Dead) return;
                ResolveSource(writer, r.SourceId, out int sourceKey, out EntityState killer);
                _combat.ApplyDamage(writer, r.SourceId, sourceKey, killer, e.Handle, e.Key, r.Amount, 0u,
                    GameEventFlags.Periodic, r.StatusId, tick);
                return;
            }
            case StatusTickKind.PeriodicHeal:
            {
                if (writer.HealthOf(e.Handle).Dead) return;
                EntityState t = CombatResolver.Effective(writer, e.Handle);
                int healed = AbilityLogic.ClampHeal(r.Amount, in t);
                ResolveSource(writer, r.SourceId, out int sourceKey, out _);
                _combat.ApplyHeal(writer, r.SourceId, sourceKey, e.Handle, e.Key, healed, 0u,
                    GameEventFlags.Periodic, r.StatusId);
                return;
            }
            case StatusTickKind.Expired:
            {
                GameplayState.NoteStatusChange(rec, null);
                rec.StatsVersion++;
                _combat.Emit(GameEventData.StatusRemoved(writer.IdRefOf(e.Handle).Value, r.StatusId),
                    PendingGameEvent.NoKey, e.Key);
                CombatResolver.ClampHpToEffectiveMax(writer, e.Handle);
                return;
            }
        }
    }

    private static void ResolveSource(WorldWriter writer, string? sourceId, out int key, out EntityState state)
    {
        key = PendingGameEvent.NoKey;
        state = default;
        if (sourceId == null) return;
        EntityHandle h = writer.Resolve(sourceId);
        if (!h.IsValid) return;
        key = writer.IdRefOf(h).Stable;
        state = writer.Compose(h);
    }

    // ── Items ────────────────────────────────────────────────────────────────

    private static void StepItems(WorldWriter writer, ulong tick)
    {
        GameplayState g = writer.Gameplay;
        var keys = g.ItemKeys;
        int write = 0;
        for (int read = 0; read < keys.Count; read++)
        {
            int key = keys[read];
            SimRecord? rec = g.Get(key);
            if (rec is not { Kind: SimKind.Item }) continue;      // taken or removed
            if (!writer.IsAlive(rec.Handle))
            {
                // Spawn still queued (deferred structural op): keep it for the next tick.
                if (rec.Handle.IsValid) continue;
                keys[write++] = key;
                continue;
            }

            if (tick >= rec.DespawnTick)
            {
                writer.Despawn(rec.Handle);
                continue;
            }

            keys[write++] = key;
        }

        keys.RemoveRange(write, keys.Count - write);
    }
}
