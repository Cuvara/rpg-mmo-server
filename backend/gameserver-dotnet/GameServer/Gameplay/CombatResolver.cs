using System;
using GameServer.Content;
using GameServer.Snapshot;
using GameServer.World;
using GameServer.World.Components;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;
using Shared.GameLogic.Systems;
using SimAction = Shared.GameLogic.Components.EntityAction;

namespace GameServer.Gameplay;

/// <summary>
/// Applies ability effects, damage, healing, statuses and death to entities inside a world
/// write scope, and emits the matching <see cref="GameEventData"/> (ADR-30). One place for
/// "what happens when something is hit", shared by instant casts, area casts, projectile hits
/// and periodic ticks, so the four cannot drift apart.
/// </summary>
/// <remarks>
/// <para><b>Effective stats.</b> Every calculation reads attack, defense and max HP through
/// the entity's <see cref="StatusSet"/> (<see cref="Effective"/>); with no status active the
/// numbers are the component values exactly, so protocol 2 combat is unchanged.</para>
/// <para><b>Allocation.</b> Nothing here allocates on the steady path: events go into the
/// pre-sized <see cref="TickEventBuffer"/>, statuses into the entity's fixed-capacity set.
/// Spawning a loot drop allocates (an entity is created), which is an event, not a tick.</para>
/// <para>Holds no simulation state: everything it changes lives in the world.</para>
/// </remarks>
public sealed class CombatResolver
{
    private readonly TickEventBuffer? _events;
    private readonly Input.InputHandler.DeathHandler? _onDeath;
    private readonly int _oneShotHoldTicks;
    private readonly ContentDatabase _content;

    /// <summary>Builds a resolver.</summary>
    /// <param name="content">Content abilities and status ids resolve against (the set clients downloaded).</param>
    public CombatResolver(
        TickEventBuffer? events, Input.InputHandler.DeathHandler? onDeath, int oneShotHoldTicks, ContentDatabase content)
    {
        _content = content ?? ContentDatabase.Empty;
        _events = events;
        _onDeath = onDeath;
        _oneShotHoldTicks = oneShotHoldTicks;
    }

    /// <summary>
    /// Placeholder targeting rule for area and projectile deliveries, pending a faction
    /// design: an effect delivered by area or projectile affects entities whose TYPE differs
    /// from the caster's (players hit mobs, mobs hit players), never the caster itself.
    /// Entity delivery names its target explicitly and is not filtered by this.
    /// </summary>
    public static bool IsHostile(string? sourceType, string? targetType) =>
        !string.Equals(sourceType, targetType, StringComparison.Ordinal);

    /// <summary>
    /// The entity's state with attack, defense, max HP and speed read through its statuses.
    /// Equal to <see cref="WorldWriter.Compose"/> when no status is active.
    /// </summary>
    public static EntityState Effective(WorldWriter writer, in EntityHandle handle)
    {
        EntityState s = writer.Compose(handle);
        SimRecord? rec = writer.RecordOf(handle);
        if (rec?.Statuses is { Count: > 0 } st)
        {
            s.Attack = st.EffectiveAttack(s.Attack);
            s.Defense = st.EffectiveDefense(s.Defense);
            s.MaxHp = st.EffectiveMaxHp(s.MaxHp);
            s.Speed = st.EffectiveSpeed(s.Speed);
        }

        return s;
    }

    /// <summary>The entity's status set, or null when it has none (projectiles, items, tests' bare entities).</summary>
    public static StatusSet? StatusesOf(WorldWriter writer, in EntityHandle handle) =>
        writer.RecordOf(handle)?.Statuses;

    /// <summary>
    /// Apply every effect of <paramref name="ability"/>, in order, from
    /// <paramref name="caster"/> to <paramref name="target"/>. Stops early when the target
    /// dies: later effects have nothing to land on.
    /// </summary>
    /// <param name="caster">The caster's EFFECTIVE state (at cast time for a projectile).</param>
    public void ApplyEffects(
        WorldWriter writer, AbilityDefinition ability, in EntityState caster, int casterKey,
        in EntityHandle target, ulong tick)
    {
        int targetKey = writer.IdRefOf(target).Stable;
        for (int i = 0; i < ability.EffectCount; i++)
        {
            if (!writer.IsAlive(target) || writer.HealthOf(target).Dead) return;

            EffectSpec effect = ability.GetEffect(i);
            switch (effect.Kind)
            {
                case EffectKind.Damage:
                {
                    EntityState t = Effective(writer, target);
                    int damage = AbilityLogic.CalculateEffectDamage(in caster, in effect, in t);
                    ApplyDamage(writer, caster.Id, casterKey, caster, target, targetKey, damage, ability.Id,
                        GameEventFlags.None, 0u, tick);
                    break;
                }
                case EffectKind.Heal:
                {
                    EntityState t = Effective(writer, target);
                    int healed = AbilityLogic.CalculateEffectHeal(in effect, in t);
                    ApplyHeal(writer, caster.Id, casterKey, target, targetKey, healed, ability.Id,
                        GameEventFlags.None, 0u);
                    break;
                }
                case EffectKind.ApplyStatus:
                    ApplyStatus(writer, caster.Id, casterKey, target, targetKey, effect.StatusId, ability.Id, tick);
                    break;
            }
        }
    }

    /// <summary>
    /// Subtract <paramref name="amount"/> HP, emit the Damage event, and resolve a death.
    /// Returns the amount applied.
    /// </summary>
    /// <param name="killerState">State reported to the death callback as the killer; may be default when unknown.</param>
    public int ApplyDamage(
        WorldWriter writer, string? sourceId, int sourceKey, in EntityState killerState,
        in EntityHandle target, int targetKey, int amount, uint abilityId, GameEventFlags flags, uint effectId,
        ulong tick)
    {
        ref Health health = ref writer.HealthOf(target);
        if (health.Dead) return 0;
        if (amount < 0) amount = 0;

        health.Hp -= amount;
        string targetId = writer.IdRefOf(target).Value;
        Emit(new GameEventData(GameEventType.Damage, sourceId, targetId, amount, abilityId, flags, effectId),
            sourceKey, targetKey);

        if (health.Hp <= 0)
        {
            Kill(writer, target, targetKey, sourceId, sourceKey, killerState, tick);
        }

        return amount;
    }

    /// <summary>
    /// Restore up to <paramref name="amount"/> HP (already clamped by the caller to missing
    /// EFFECTIVE health) and emit the Heal event. A zero heal is still reported, flagged
    /// <see cref="GameEventFlags.Immune"/> — "it happened and did nothing".
    /// </summary>
    public void ApplyHeal(
        WorldWriter writer, string? sourceId, int sourceKey, in EntityHandle target, int targetKey,
        int amount, uint abilityId, GameEventFlags flags, uint effectId)
    {
        ref Health health = ref writer.HealthOf(target);
        if (health.Dead) return;
        string targetId = writer.IdRefOf(target).Value;

        if (amount <= 0)
        {
            Emit(new GameEventData(GameEventType.Heal, sourceId, targetId, 0, abilityId,
                flags | GameEventFlags.Immune, effectId), sourceKey, targetKey);
            return;
        }

        health.Hp += amount;
        Emit(new GameEventData(GameEventType.Heal, sourceId, targetId, amount, abilityId, flags, effectId),
            sourceKey, targetKey);
    }

    /// <summary>
    /// Apply one stack of a status and emit StatusApplied (amount = stacks after applying).
    /// Silently nothing when the target has no status set, the status id is unknown, or the
    /// set is full of other statuses.
    /// </summary>
    public StatusApplyResult ApplyStatus(
        WorldWriter writer, string? sourceId, int sourceKey, in EntityHandle target, int targetKey,
        uint statusId, uint abilityId, ulong tick)
    {
        SimRecord? rec = writer.RecordOf(target);
        StatusSet? set = rec?.Statuses;
        if (set == null) return StatusApplyResult.Rejected;
        if (!_content.TryGetStatus(statusId, out StatusDefinition? def) || def == null)
        {
            return StatusApplyResult.Rejected;
        }

        StatusApplyResult result = set.Apply(def, tick, sourceId);
        if (result == StatusApplyResult.Rejected) return result;

        GameplayState.NoteStatusChange(rec!, def);
        set.TryGet(statusId, out StatusInstance inst);
        Emit(GameEventData.StatusApplied(sourceId, writer.IdRefOf(target).Value, statusId, inst.Stacks, abilityId),
            sourceKey, targetKey);
        ClampHpToEffectiveMax(writer, target);
        return result;
    }

    /// <summary>Remove a status (cleanse, expiry handled elsewhere) and emit StatusRemoved.</summary>
    public bool RemoveStatus(WorldWriter writer, in EntityHandle target, uint statusId)
    {
        SimRecord? rec = writer.RecordOf(target);
        if (rec?.Statuses == null) return false;
        rec.Statuses.TryGet(statusId, out StatusInstance inst);
        if (!rec.Statuses.Remove(statusId)) return false;
        GameplayState.NoteStatusChange(rec, inst.Definition);
        Emit(GameEventData.StatusRemoved(writer.IdRefOf(target).Value, statusId),
            PendingGameEvent.NoKey, writer.IdRefOf(target).Stable);
        ClampHpToEffectiveMax(writer, target);
        return true;
    }

    /// <summary>
    /// Kill <paramref name="target"/>: Dead flag and HP 0, the Death event, the terminal Dead
    /// action, the death callback, then <see cref="AfterDeath"/> (statuses cleared, loot).
    /// </summary>
    public void Kill(
        WorldWriter writer, in EntityHandle target, int targetKey, string? killerId, int killerKey,
        in EntityState killerState, ulong tick)
    {
        EntityState victim = writer.Compose(target);
        if (!CombatLogic.HandleDeath(ref victim)) return;

        ref Health health = ref writer.HealthOf(target);
        health.Hp = victim.Hp;
        health.Dead = true;

        Emit(GameEventData.Death(killerId, victim.Id), killerKey, targetKey);
        ActionTransitions.Enter(ref writer.LocomotionOf(target), SimAction.Dead, tick, _oneShotHoldTicks);

        EntityState killer = killerState;
        if (killer.Id == null) killer.Id = killerId!;
        _onDeath?.Invoke(victim, killer);

        AfterDeath(writer, target, tick);
    }

    /// <summary>
    /// What follows any death, whoever caused it: every status ends (with StatusRemoved
    /// events, so a client clears its icons on the edge rather than waiting for the next
    /// keyframe), and the victim's loot table is rolled. Call exactly once per death — the
    /// basic-attack path, which resolves its own death, calls it directly.
    /// </summary>
    public void AfterDeath(WorldWriter writer, in EntityHandle target, ulong tick)
    {
        SimRecord? rec = writer.RecordOf(target);
        if (rec == null) return;

        int targetKey = writer.IdRefOf(target).Stable;
        if (rec.Statuses is { Count: > 0 } set)
        {
            string targetId = writer.IdRefOf(target).Value;
            for (int i = 0; i < set.Count; i++)
            {
                Emit(GameEventData.StatusRemoved(targetId, set.GetAt(i).StatusId), PendingGameEvent.NoKey, targetKey);
            }

            set.Clear();
            rec.StatusesVersion++;
            rec.StatsVersion++;
        }

        RollLoot(writer, target, rec, tick);
    }

    /// <summary>
    /// Roll the victim type's loot table and drop each winning entry as an item entity at the
    /// victim's feet. Deterministic: every roll is a pure function of the server's loot seed,
    /// the tick, the victim's stable key and the entry index — no wall clock, no shared RNG
    /// stream whose position would depend on what else died first.
    /// </summary>
    internal void RollLoot(WorldWriter writer, in EntityHandle victim, SimRecord rec, ulong tick)
    {
        GameplayState g = writer.Gameplay;
        if (!g.Loot.TryGetForType(rec.Type, out LootTable? table) || table == null) return;

        g.LootRolls++;
        int victimKey = writer.IdRefOf(victim).Stable;
        ref Position at = ref writer.PositionOf(victim);
        Shared.GameLogic.Components.Vec2 origin = at.Value;
        int dropped = 0;

        for (int i = 0; i < table.EntryCount; i++)
        {
            LootEntry entry = table.GetEntry(i);
            ulong r = LootRandom(g.LootSeed, tick, victimKey, i);
            if ((int)(r % 1000UL) >= entry.ChancePermille) continue;

            int span = entry.MaxQuantity - entry.MinQuantity + 1;
            int quantity = entry.MinQuantity + (span <= 1 ? 0 : (int)((r >> 20) % (ulong)span));
            SpawnItem(writer, entry.ItemId, quantity, DropOffset(origin, dropped, g), tick + (ulong)table.DespawnTicks);
            dropped++;
        }
    }

    /// <summary>
    /// Spawn a dropped-item entity (wire type <c>item</c>) carrying an item id, a quantity
    /// and a despawn tick. Exposed for the command layer (e.g. a future "drop item" opcode).
    /// </summary>
    public static string SpawnItem(WorldWriter writer, string itemId, int quantity, Shared.GameLogic.Components.Vec2 position, ulong despawnTick)
    {
        GameplayState g = writer.Gameplay;
        string id = g.RentItemId();
        int key = writer.StableKey(id);
        SimRecord rec = g.GetOrCreate(key);
        rec.ResetPayload();
        rec.Kind = SimKind.Item;
        rec.ItemId = itemId;
        rec.ItemQuantity = quantity;
        rec.DespawnTick = despawnTick;
        g.ItemKeys.Add(key);
        g.ItemsDropped++;

        writer.Spawn(new EntityState
        {
            Id = id,
            Type = ItemType,
            Position = g.Geometry.Bounds.Clamp(position),
            Hp = 0,
            MaxHp = 0,
            Speed = 0f,
        }, EntityTags.None);
        return id;
    }

    /// <summary>Entity type of a dropped item.</summary>
    public const string ItemType = "item";

    /// <summary>Entity type of a projectile.</summary>
    public const string ProjectileType = "projectile";

    // Drops after the first fan out on a small fixed ring so they do not stack exactly; the
    // offsets are constants, so placement is as deterministic as the roll.
    private static readonly float[] RingX = { 0f, 0.6f, -0.6f, 0f, 0f, 0.42f, -0.42f, 0.42f, -0.42f };
    private static readonly float[] RingY = { 0f, 0f, 0f, 0.6f, -0.6f, 0.42f, 0.42f, -0.42f, -0.42f };

    private static Shared.GameLogic.Components.Vec2 DropOffset(Shared.GameLogic.Components.Vec2 origin, int index, GameplayState g)
    {
        int i = index % RingX.Length;
        return new Shared.GameLogic.Components.Vec2(origin.X + RingX[i], origin.Y + RingY[i]);
    }

    /// <summary>SplitMix64 over (seed, tick, key, index): a stateless, platform-independent roll.</summary>
    internal static ulong LootRandom(ulong seed, ulong tick, int key, int index)
    {
        ulong z = seed ^ (tick * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)key << 32) ^ (uint)index;
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Clamp HP down to the effective max after a max-HP modifier changed or ended.</summary>
    public static void ClampHpToEffectiveMax(WorldWriter writer, in EntityHandle target)
    {
        ref Health health = ref writer.HealthOf(target);
        if (health.Dead) return;
        SimRecord? rec = writer.RecordOf(target);
        int max = rec?.Statuses is { Count: > 0 } st ? st.EffectiveMaxHp(health.MaxHp) : health.MaxHp;
        if (max > 0 && health.Hp > max) health.Hp = max;
    }

    internal void Emit(in GameEventData data, int sourceKey, int targetKey) =>
        _events?.Add(in data, sourceKey, targetKey);
}
