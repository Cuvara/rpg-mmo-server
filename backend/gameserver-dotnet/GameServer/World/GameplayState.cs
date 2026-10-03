using System;
using System.Collections.Generic;
using GameServer.Content;
using GameServer.World.Components;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;
using Shared.GameLogic.Systems;
using Shared.GameLogic.World;

namespace GameServer.World;

/// <summary>What a <see cref="SimRecord"/> describes.</summary>
public enum SimKind : byte
{
    /// <summary>No gameplay data (slot unused, or the entity was removed).</summary>
    None = 0,

    /// <summary>A character: player, mob, npc or boss. Damageable, has statuses and a stat block.</summary>
    Actor = 1,

    /// <summary>A skillshot projectile (ADR-29), wire type <c>projectile</c>.</summary>
    Projectile = 2,

    /// <summary>A dropped item lying in the world, wire type <c>item</c>.</summary>
    Item = 3,
}

/// <summary>
/// Protocol 3 input fields that <see cref="InputData"/> (a frozen <c>Shared.GameLogic</c>
/// type) does not carry: jump, the aim height and the lag-compensation instant (ADR-28,
/// ADR-29). Rides beside the input in <see cref="PendingInput"/>.
/// </summary>
/// <remarks>
/// The network decoder fills this from <c>InputMessage</c> fields 9-13; a protocol 2 peer
/// sends none of them and gets <c>default</c>: no jump, aim height 0, no rewind, no spawn
/// sequence — exactly the protocol 2 behaviour.
/// </remarks>
public readonly struct InputExtras
{
    /// <summary>Builds the extras.</summary>
    public InputExtras(bool jump, float aimZ, ulong renderTick, float renderAlpha, uint spawnSeq)
    {
        Jump = jump;
        AimZ = aimZ;
        RenderTick = renderTick;
        RenderAlpha = renderAlpha;
        SpawnSeq = spawnSeq;
    }

    /// <summary>InputMessage.jump: level-triggered for the tick it is sent on.</summary>
    public bool Jump { get; }

    /// <summary>InputMessage.aim_z: height of the surface point aimed at.</summary>
    public float AimZ { get; }

    /// <summary>InputMessage.render_tick: base tick the client was rendering remote entities at; 0 = no rewind.</summary>
    public ulong RenderTick { get; }

    /// <summary>InputMessage.render_alpha: fraction toward render_tick + 1.</summary>
    public float RenderAlpha { get; }

    /// <summary>InputMessage.spawn_seq: echoed on the projectile this input fires; 0 = none.</summary>
    public uint SpawnSeq { get; }

    /// <summary>True when any field differs from the protocol 2 default.</summary>
    public bool IsDefault => !Jump && AimZ == 0f && RenderTick == 0 && RenderAlpha == 0f && SpawnSeq == 0;
}

/// <summary>
/// Per-entity gameplay data that does not fit the seven standard components: statuses, the
/// replicated stat block, and the projectile/item payload (ADR-29, ADR-30). One instance per
/// world-stable key (<see cref="EntityIdRef.Stable"/>), reused when the key respawns.
/// </summary>
/// <remarks>
/// <para><b>Why a side table and not components.</b> Projectile and item payloads would each
/// need a new archetype, a new AOT hint and a structural op that carries a payload the
/// deferred queue does not have; statuses and stats are managed objects either way. Keying a
/// table on the stable key keeps every archetype exactly as it was (ADR-12 "the archetype
/// count is a thing to watch") and is still world state: it is owned by
/// <see cref="EcsWorld"/>, written only under its write lock and read only under its read
/// lock, and cleared when the entity is removed.</para>
/// <para>Allocated when an entity is created (an event, not a tick); nothing here allocates
/// per tick.</para>
/// </remarks>
public sealed class SimRecord
{
    /// <summary>What this record describes; <see cref="SimKind.None"/> once removed.</summary>
    public SimKind Kind { get; internal set; }

    /// <summary>The entity, valid while <see cref="Kind"/> is not None.</summary>
    internal EntityHandle Handle;

    /// <summary>Entity id string.</summary>
    public string? Id { get; internal set; }

    /// <summary>Entity type string.</summary>
    public string? Type { get; internal set; }

    // ── Actor ──

    /// <summary>Active statuses (actors only).</summary>
    internal StatusSet? Statuses;

    /// <summary>Bumped on every status change; the encoder's cheap dirty test.</summary>
    public uint StatusesVersion { get; internal set; }

    /// <summary>Base stat values, parallel to <see cref="GameplayState.StatIds"/> (actors only).</summary>
    internal int[]? StatValues;

    /// <summary>Bumped when a base stat or a status modifying a content stat changes.</summary>
    public uint StatsVersion { get; internal set; }

    /// <summary>Death bookkeeping (statuses cleared, loot rolled) already ran for this life.</summary>
    internal bool DeathHandled;

    // ── Projectile ──

    /// <summary>Simulation state of the projectile.</summary>
    internal ProjectileState Projectile;

    /// <summary>Ability whose effects the projectile applies on hit.</summary>
    internal AbilityDefinition? Ability;

    /// <summary>Owner (caster) id of a projectile, or null.</summary>
    public string? OwnerId { get; internal set; }

    /// <summary>Owner's stable key, or 0 when there is none.</summary>
    public int OwnerKey { get; internal set; }

    /// <summary>Owner's entity type, for the hostility rule.</summary>
    internal string? OwnerType;

    /// <summary>Caster state captured at cast time (effective attack); damage math reads it.</summary>
    internal EntityState OwnerSnapshot;

    /// <summary>InputMessage.spawn_seq of the input that fired the projectile.</summary>
    public uint SpawnSeq { get; internal set; }

    /// <summary>Base tick the projectile was spawned on.</summary>
    internal ulong SpawnTick;

    /// <summary>Clamped lag-compensation instant of the firing input; used on the first step only.</summary>
    internal ulong RewindTick;

    /// <inheritdoc cref="RewindTick"/>
    internal float RewindAlpha;

    /// <summary>Steps already taken; 0 means the next step is the rewound first one.</summary>
    internal int StepsTaken;

    // ── Item ──

    /// <summary>Item content id of a dropped item.</summary>
    public string? ItemId { get; internal set; }

    /// <summary>Stack size of a dropped item.</summary>
    public int ItemQuantity { get; internal set; }

    /// <summary>Base tick at which a dropped item despawns.</summary>
    public ulong DespawnTick { get; internal set; }

    internal void ResetPayload()
    {
        Kind = SimKind.None;
        Handle = default;
        Id = null;
        Type = null;
        Statuses?.Clear();
        DeathHandled = false;
        Projectile = default;
        Ability = null;
        OwnerId = null;
        OwnerKey = 0;
        OwnerType = null;
        OwnerSnapshot = default;
        SpawnSeq = 0;
        SpawnTick = 0;
        RewindTick = 0;
        RewindAlpha = 0f;
        StepsTaken = 0;
        ItemId = null;
        ItemQuantity = 0;
        DespawnTick = 0;
        // Versions are NOT reset: a key reused by a respawn must never present the same
        // version for different contents to an encoder that still remembers the old one.
        StatusesVersion++;
        StatsVersion++;
    }
}

/// <summary>
/// One damageable entity as the hit tests see it this tick: built once per base tick by
/// <see cref="GameplayState.RebuildRoster"/>, the same set the hitbox history records.
/// </summary>
public readonly struct RosterEntry
{
    internal RosterEntry(EntityHandle handle, int key, string type, bool linkdead)
    {
        Handle = handle;
        Key = key;
        Type = type;
        Linkdead = linkdead;
    }

    /// <summary>The entity.</summary>
    public EntityHandle Handle { get; }

    /// <summary>World-stable key (hitbox history key).</summary>
    public int Key { get; }

    /// <summary>Entity type string.</summary>
    public string Type { get; }

    /// <summary>A held player (<see cref="PlayerTag.Linkdead"/>): never a target.</summary>
    public bool Linkdead { get; }
}

/// <summary>
/// Reusable entity id strings for short-lived entities (projectiles, dropped items).
/// </summary>
/// <remarks>
/// Every distinct id string gets a world-stable key that is never released
/// (<see cref="EntityIdRef.Stable"/>), so minting a fresh id per projectile would grow that
/// map — and every per-connection delta map keyed on it — without bound. Ids are therefore
/// recycled, but only after a quarantine long enough that every connection has long since
/// been sent the removal of the previous holder; a key that came back sooner could be read by
/// a delta encoder as the same entity teleporting.
/// </remarks>
internal sealed class EntityIdPool
{
    private readonly string _prefix;
    private readonly Queue<(string Id, ulong ReleasedTick)> _free = new();
    private int _next;

    internal EntityIdPool(string prefix) => _prefix = prefix;

    /// <summary>Ids ever minted (pool size).</summary>
    internal int Minted => _next;

    internal string Rent(ulong tick, ulong quarantineTicks)
    {
        if (_free.Count > 0)
        {
            var head = _free.Peek();
            if (tick >= head.ReleasedTick + quarantineTicks)
            {
                _free.Dequeue();
                return head.Id;
            }
        }

        _next++;
        return _prefix + _next.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal void Release(string id, ulong tick) => _free.Enqueue((id, tick));
}

/// <summary>
/// The gameplay side of the world (ADR-28..31): map geometry, motor tuning, content, the
/// per-entity <see cref="SimRecord"/> table, the projectile and item lists, the damageable
/// roster, the lag-compensation history and the loot RNG seed. Owned by
/// <see cref="EcsWorld"/>; every member is touched only under the world's lock (write lock
/// for writes, read lock for reads), like component storage.
/// </summary>
public sealed class GameplayState
{
    /// <summary>
    /// Height above the caster's feet that projectiles launch from, and the height above the
    /// aim point they fly toward (see <c>docs/DESIGN.md</c>, "Core v3 simulation"). The aim
    /// point is a point on a surface; launching from and aiming at the same height above
    /// both makes a flat-ground shot fly level. Placeholder, pending design.
    /// </summary>
    public const float ProjectileLaunchHeight = 1.0f;

    /// <summary>Seconds a recycled projectile/item id stays quarantined (see <see cref="EntityIdPool"/>).</summary>
    public const int IdQuarantineSeconds = 10;

    /// <summary>Default <see cref="MaxHitboxEntities"/>.</summary>
    public const int DefaultMaxHitboxEntities = 2048;

    private SimRecord?[] _records = new SimRecord?[64];
    private readonly List<int> _projectiles = new();
    private readonly List<int> _items = new();
    private RosterEntry[] _roster = Array.Empty<RosterEntry>();
    private int _rosterCount;
    private HitboxHistory? _history;
    private readonly EntityIdPool _projectileIds = new("proj-");
    private readonly EntityIdPool _itemIds = new("item-");

    /// <summary>Map collision world. Flat default bounds until configured.</summary>
    public MapGeometry Geometry { get; private set; } = MapGeometry.Flat(MapBounds.Default);

    /// <summary>True once <see cref="Configure"/> supplied a geometry (from a map file or explicitly).</summary>
    public bool HasConfiguredGeometry { get; private set; }

    /// <summary>Motor tuning shared with the client (ADR-28 decision 2).</summary>
    public MotorParams Motor { get; private set; } = MotorParams.Default;

    /// <summary>Content the stat defaults are read from.</summary>
    public ContentDatabase Content { get; private set; } = ContentDatabase.Empty;

    /// <summary>Loot tables rolled on death.</summary>
    public LootTables Loot { get; private set; } = LootTables.Empty;

    /// <summary>Base (critical) tick rate: the rate hitbox history, statuses and projectiles step at.</summary>
    public int BaseHz { get; private set; } = SimulationDefaults.CriticalHz;

    /// <summary>Lag-compensation cap (ADR-29 decision 4).</summary>
    public int MaxRewindMs { get; private set; } = HitboxHistory.MaxRewindMs;

    /// <summary>Most damageable entities the hitbox history records per tick.</summary>
    public int MaxHitboxEntities { get; private set; } = DefaultMaxHitboxEntities;

    /// <summary>Seed of the loot RNG. Deterministic per server; never the wall clock.</summary>
    public ulong LootSeed { get; private set; } = 0x9E3779B97F4A7C15UL;

    /// <summary>Content stat ids in ascending order; the stat block's layout.</summary>
    public uint[] StatIds { get; private set; } = Array.Empty<uint>();

    private int[] _statDefaults = Array.Empty<int>();

    /// <summary>Base tick the gameplay step last ran on (or is running on).</summary>
    public ulong CurrentTick { get; internal set; }

    /// <summary>Loot rolls performed (diagnostics).</summary>
    public long LootRolls { get; internal set; }

    /// <summary>Items dropped by loot (diagnostics).</summary>
    public long ItemsDropped { get; internal set; }

    /// <summary>Projectiles spawned (diagnostics).</summary>
    public long ProjectilesSpawned { get; internal set; }

    /// <summary>
    /// Configure the gameplay side. Call before the first tick; anything not supplied keeps
    /// its current value.
    /// </summary>
    public void Configure(
        MapGeometry? geometry = null,
        ContentDatabase? content = null,
        LootTables? loot = null,
        int? baseHz = null,
        ulong? lootSeed = null,
        MotorParams? motor = null,
        int? maxHitboxEntities = null,
        int? maxRewindMs = null)
    {
        if (geometry != null)
        {
            Geometry = geometry;
            HasConfiguredGeometry = true;
        }

        if (content != null)
        {
            Content = content;
            var ids = new List<uint>();
            foreach (StatDefinition s in content.Stats) ids.Add(s.Id);
            ids.Sort();
            StatIds = ids.ToArray();
            _statDefaults = new int[StatIds.Length];
            for (int i = 0; i < StatIds.Length; i++) _statDefaults[i] = content.GetStat(StatIds[i]).DefaultValue;
        }

        if (loot != null) Loot = loot;
        if (baseHz is > 0) { BaseHz = baseHz.Value; _history = null; }
        if (lootSeed.HasValue) LootSeed = lootSeed.Value;
        if (motor.HasValue) Motor = motor.Value;
        if (maxHitboxEntities is > 0) { MaxHitboxEntities = maxHitboxEntities.Value; _history = null; }
        if (maxRewindMs is >= 0) { MaxRewindMs = maxRewindMs.Value; _history = null; }
    }

    /// <summary>Lag-compensation history, created on first use and sized from the configuration.</summary>
    public HitboxHistory History => _history ??= new HitboxHistory(BaseHz, MaxHitboxEntities, MaxRewindMs);

    /// <summary>The default spawn (map spawn named <c>default</c>), if the map has one.</summary>
    public bool TryGetDefaultSpawn(out Vec3 position)
    {
        if (Geometry.FindSpawn("default", out SpawnPoint spawn))
        {
            position = spawn.Position;
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>
    /// The default player spawn: the map's <c>default</c> spawn point when it has one, else
    /// the protocol 2 spawn (origin, clamped into bounds) standing on the ground.
    /// </summary>
    public Vec3 DefaultSpawn()
    {
        if (TryGetDefaultSpawn(out Vec3 p)) return p;
        Vec2 xy = Geometry.Bounds.Clamp(Vec2.Zero);
        return new Vec3(xy.X, xy.Y, GroundAt(xy.X, xy.Y));
    }

    /// <summary>Height a character standing at (x, y) rests on.</summary>
    public float GroundAt(float x, float y) =>
        Geometry.SupportHeight(x, y, Motor.CapsuleRadius, float.MaxValue);

    // ── Records ──────────────────────────────────────────────────────────────

    /// <summary>The record for a stable key, or null.</summary>
    public SimRecord? Get(int key) =>
        (uint)key < (uint)_records.Length ? _records[key] : null;

    internal SimRecord GetOrCreate(int key)
    {
        if (key < 0) throw new ArgumentOutOfRangeException(nameof(key));
        if (key >= _records.Length)
        {
            int size = _records.Length;
            while (size <= key) size *= 2;
            Array.Resize(ref _records, size);
        }

        return _records[key] ??= new SimRecord();
    }

    /// <summary>True for entity types that are characters.</summary>
    public static bool IsActorType(string? type) =>
        type is "player" or "mob" or "npc" or "boss";

    /// <summary>
    /// Called by <see cref="EcsWorld"/> when it CREATES an entity (not when it overwrites one).
    /// Sets the spawn height and grounded flag, and fills the record.
    /// </summary>
    internal void OnEntityCreated(int key, EntityHandle handle, string id, string type, ref Position position, ref Locomotion locomotion)
    {
        SimRecord? existing = Get(key);

        if (existing is { Kind: SimKind.Projectile } proj)
        {
            proj.Handle = handle;
            proj.Id = id;
            proj.Type = type;
            position.Z = proj.Projectile.Position.Z;
            locomotion.VelocityX = proj.Projectile.Velocity.X;
            locomotion.VelocityY = proj.Projectile.Velocity.Y;
            locomotion.VelocityZ = proj.Projectile.Velocity.Z;
            locomotion.Grounded = false;
            return;
        }

        position.Z = GroundAt(position.Value.X, position.Value.Y);
        locomotion.Grounded = true;

        if (existing is { Kind: SimKind.Item } item)
        {
            item.Handle = handle;
            item.Id = id;
            item.Type = type;
            return;
        }

        if (!IsActorType(type))
        {
            if (existing != null) existing.ResetPayload();
            return;
        }

        SimRecord rec = GetOrCreate(key);
        rec.ResetPayload();
        rec.Kind = SimKind.Actor;
        rec.Handle = handle;
        rec.Id = id;
        rec.Type = type;
        rec.Statuses ??= new StatusSet();
        if (rec.StatValues == null || rec.StatValues.Length != StatIds.Length)
            rec.StatValues = new int[StatIds.Length];
        Array.Copy(_statDefaults, rec.StatValues, StatIds.Length);
    }

    /// <summary>Called by <see cref="EcsWorld"/> when it destroys an entity.</summary>
    internal void OnEntityRemoved(int key)
    {
        SimRecord? rec = Get(key);
        if (rec == null) return;

        if (rec.Id != null)
        {
            if (rec.Kind == SimKind.Projectile) _projectileIds.Release(rec.Id, CurrentTick);
            else if (rec.Kind == SimKind.Item) _itemIds.Release(rec.Id, CurrentTick);
        }

        rec.ResetPayload();
    }

    internal ulong IdQuarantineTicks => (ulong)IdQuarantineSeconds * (ulong)Math.Max(1, BaseHz);

    internal string RentProjectileId() => _projectileIds.Rent(CurrentTick, IdQuarantineTicks);

    internal string RentItemId() => _itemIds.Rent(CurrentTick, IdQuarantineTicks);

    internal int ProjectileIdsMinted => _projectileIds.Minted;

    internal List<int> ProjectileKeys => _projectiles;

    internal List<int> ItemKeys => _items;

    // ── Stats and statuses ───────────────────────────────────────────────────

    /// <summary>Index of a stat id in <see cref="StatIds"/>, or -1.</summary>
    public int StatIndex(uint statId)
    {
        uint[] ids = StatIds;
        for (int i = 0; i < ids.Length; i++) if (ids[i] == statId) return i;
        return -1;
    }

    /// <summary>Content stat id for a key (e.g. <c>"level"</c>), or 0.</summary>
    public uint StatIdFor(string key)
    {
        foreach (StatDefinition s in Content.Stats)
        {
            if (string.Equals(s.Key, key, StringComparison.Ordinal)) return s.Id;
        }

        return 0;
    }

    internal bool TrySetBaseStat(SimRecord rec, uint statId, int value)
    {
        int i = StatIndex(statId);
        if (i < 0 || rec.StatValues == null || i >= rec.StatValues.Length) return false;
        if (rec.StatValues[i] == value) return true;
        rec.StatValues[i] = value;
        rec.StatsVersion++;
        return true;
    }

    internal static bool TryGetBaseStat(GameplayState g, SimRecord rec, uint statId, out int value)
    {
        int i = g.StatIndex(statId);
        if (i < 0 || rec.StatValues == null || i >= rec.StatValues.Length)
        {
            value = 0;
            return false;
        }

        value = rec.StatValues[i];
        return true;
    }

    /// <summary>Bump the status version, and the stat version when the status touches a content stat.</summary>
    internal static void NoteStatusChange(SimRecord rec, StatusDefinition? def)
    {
        rec.StatusesVersion++;
        if (def == null) return;
        for (int m = 0; m < def.ModifierCount; m++)
        {
            if (def.GetModifier(m).Target == StatModifierTarget.ContentStat)
            {
                rec.StatsVersion++;
                return;
            }
        }
    }

    // ── Roster ───────────────────────────────────────────────────────────────

    /// <summary>Damageable entities as of the start of this tick.</summary>
    public ReadOnlySpan<RosterEntry> Roster => new(_roster, 0, _rosterCount);

    internal void ClearRoster() => _rosterCount = 0;

    /// <summary>Base tick the roster was last built for; 0 before the first build.</summary>
    public ulong RosterTick { get; internal set; }

    internal void AddToRoster(in RosterEntry entry)
    {
        if (_rosterCount == _roster.Length)
        {
            Array.Resize(ref _roster, Math.Max(16, _roster.Length * 2));
        }

        _roster[_rosterCount++] = entry;
    }
}

/// <summary>Compile-time defaults GameplayState falls back to before configuration.</summary>
internal static class SimulationDefaults
{
    /// <summary>The critical (base) rate's default, mirrored from <c>SimulationRates.DefaultCriticalHz</c>.</summary>
    public const int CriticalHz = 60;
}
