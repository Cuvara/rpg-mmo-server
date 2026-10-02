using System.Text;
using GameServer.Content;
using GameServer.Input;
using GameServer.Snapshot;
using GameServer.World;
using GameServer.World.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;
using Shared.GameLogic.World;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// A world, an input handler and a tick driver that runs the critical scope the way
/// <c>TickLoop</c> does (BeginTick → inputs → held movement → gameplay step), plus an
/// optional "world phase" callback that runs AFTER the critical scope, where the enemy
/// systems move entities in the real loop. Test content only; every number is arbitrary.
/// </summary>
internal sealed class SimFixture : IDisposable
{
    public const uint Bolt = 1;
    public const uint Snare = 2;
    public const uint Mend = 3;
    public const uint Jab = 4;

    public const uint Burning = 1;
    public const uint Rooted = 2;
    public const uint Chilled = 3;
    public const uint Mending = 4;
    public const uint Stunned = 5;
    public const uint Silenced = 6;
    public const uint Empowered = 7;

    public const string ContentJson = """
    {
      "items": [
        { "id": "wolf_pelt", "name": "Wolf Pelt", "slot": "none", "rarity": "common",
          "stackMax": 99, "attack": 0, "defense": 0, "levelRequirement": 0 }
      ],
      "stats": [
        { "id": 1, "key": "level", "default": 1 },
        { "id": 2, "key": "mana", "default": 100 }
      ],
      "statuses": [
        { "id": 1, "key": "burning", "durationTicks": 10, "maxStacks": 3,
          "periodic": { "kind": "damage", "intervalTicks": 2, "amount": 3 } },
        { "id": 2, "key": "rooted", "durationTicks": 5, "crowdControl": [ "root" ] },
        { "id": 3, "key": "chilled", "durationTicks": 20, "crowdControl": [ "slow" ], "slowPermille": 500 },
        { "id": 4, "key": "mending", "durationTicks": 6,
          "periodic": { "kind": "heal", "intervalTicks": 3, "amount": 7 } },
        { "id": 5, "key": "stunned", "durationTicks": 5, "crowdControl": [ "stun" ] },
        { "id": 6, "key": "silenced", "durationTicks": 5, "crowdControl": [ "silence" ] },
        { "id": 7, "key": "empowered", "durationTicks": 50,
          "modifiers": [ { "target": "attack", "add": 10 } ] }
      ],
      "abilities": [
        { "id": 1, "name": "Bolt", "delivery": "projectile",
          "effects": [ { "kind": "damage", "power": 20 }, { "kind": "apply_status", "statusId": 1 } ],
          "projectile": { "speed": 600, "radius": 0.3, "range": 12 }, "cooldownTicks": 1 },
        { "id": 2, "name": "Snare", "delivery": "ground", "range": 20, "radius": 3,
          "effects": [ { "kind": "damage", "power": 5 }, { "kind": "apply_status", "statusId": 2 } ],
          "cooldownTicks": 1 },
        { "id": 3, "name": "Mend", "delivery": "self",
          "effects": [ { "kind": "apply_status", "statusId": 4 } ], "cooldownTicks": 1 },
        { "id": 4, "name": "Jab", "delivery": "entity", "range": 5,
          "effects": [ { "kind": "damage", "power": 1 } ], "cooldownTicks": 1 }
      ],
      "loot": [
        { "id": "mobs", "entityType": "mob", "despawnTicks": 100,
          "entries": [ { "itemId": "wolf_pelt", "chancePermille": 1000, "min": 2, "max": 2 } ] }
      ]
    }
    """;

    public EcsWorld World { get; }
    public TickEventBuffer Events { get; } = new();
    public InputHandler Handler { get; }
    public LoadedContent Content { get; }
    public ulong Tick { get; private set; }
    public List<(EntityState Victim, EntityState Killer)> Deaths { get; } = new();

    private readonly List<(string User, InputData Input, InputExtras Extras)> _queued = new();

    public SimFixture(MapGeometry? geometry = null, int hz = 60, ulong lootSeed = 42, bool withLoot = true)
    {
        Content = ContentLoader.LoadFromBytes(Encoding.UTF8.GetBytes(ContentJson), "test-content.json");
        World = new EcsWorld();
        MapBounds bounds = geometry?.Bounds ?? MapBounds.FromSize(200, 200);
        World.Gameplay.Configure(
            geometry: geometry ?? MapGeometry.Flat(bounds),
            content: Content.Database,
            loot: withLoot ? Content.Loot : LootTables.Empty,
            baseHz: hz,
            lootSeed: lootSeed);
        Handler = new InputHandler(
            World, NullLogger.Instance, (v, k) => Deaths.Add((v, k)), hz, bounds,
            onRejected: null, onAttackAccepted: null, events: Events, content: Content.Database);
    }

    public void AddPlayer(string id, float x, float y, int hp = 100, int attack = 10, int defense = 5)
    {
        World.AddEntity(new EntityState
        {
            Id = id, Type = "player", Position = new Vec2(x, y),
            Hp = hp, MaxHp = 100, Speed = 5f, Attack = attack, Defense = defense,
        });
    }

    public void AddMob(string id, float x, float y, int hp = 100, int defense = 5)
    {
        World.AddEntity(new EntityState
        {
            Id = id, Type = "mob", Position = new Vec2(x, y),
            Hp = hp, MaxHp = 100, Speed = 2.5f, Attack = 5, Defense = defense,
        });
    }

    /// <summary>Queue an input for the next <see cref="Step"/>.</summary>
    public void Input(string user, InputData input, InputExtras extras = default) =>
        _queued.Add((user, input, extras));

    /// <summary>
    /// One base tick: the critical scope as TickLoop runs it, then <paramref name="worldPhase"/>
    /// (enemy movement in the real loop) in a scope of its own.
    /// </summary>
    public void Step(Action<WorldWriter, ulong>? worldPhase = null)
    {
        Tick++;
        ulong tick = Tick;
        World.UpdateComponents(w =>
        {
            Handler.BeginTick(w, tick);
            foreach (var (user, input, extras) in _queued)
            {
                Handler.ProcessInput(w, user, input, in extras, tick);
            }

            Handler.ApplyHeldMovement(w, tick);
            Handler.StepGameplay(w, tick);
        });
        _queued.Clear();
        if (worldPhase != null) World.UpdateComponents(w => worldPhase(w, tick));
    }

    public void StepUntil(ulong tick, Action<WorldWriter, ulong>? worldPhase = null)
    {
        while (Tick < tick) Step(worldPhase);
    }

    public EntityState State(string id) => World.GetEntity(id) ?? throw new InvalidOperationException($"no entity {id}");

    public bool Exists(string id) => World.GetEntity(id) != null;

    public Vec3 Pos(string id)
    {
        Vec3 result = default;
        World.UpdateComponents(w =>
        {
            EntityHandle h = w.Resolve(id);
            ref Position p = ref w.PositionOf(h);
            result = new Vec3(p.Value.X, p.Value.Y, p.Z);
        });
        return result;
    }

    public void SetPos(WorldWriter w, string id, float x, float y)
    {
        EntityHandle h = w.Resolve(id);
        if (!h.IsValid) return;
        w.PositionOf(h).Value = new Vec2(x, y);
    }

    public void ApplyStatus(string id, uint statusId, string? source = null)
    {
        ulong tick = Tick;
        World.UpdateComponents(w =>
        {
            EntityHandle h = w.Resolve(id);
            Handler.Combat.ApplyStatus(w, source, PendingGameEvent.NoKey, h, w.IdRefOf(h).Stable, statusId, 0, tick);
        });
    }

    public SimRecord Record(string id)
    {
        SimRecord? rec = null;
        World.UpdateComponents(w => rec = w.RecordOf(w.Resolve(id)));
        return rec ?? throw new InvalidOperationException($"no record for {id}");
    }

    public List<EntityView> Views()
    {
        var list = new List<EntityView>();
        World.ReadAll(r =>
        {
            var buffer = new EntityView[256];
            int n = r.GetEntitiesInRange(Vec2.Zero, 100000f, buffer);
            for (int i = 0; i < Math.Min(n, buffer.Length); i++) list.Add(buffer[i]);
        });
        return list;
    }

    public IEnumerable<GameEventData> EventsOf(GameEventType type) =>
        Events.Events.Where(e => e.Data.Type == type).Select(e => e.Data);

    public static InputData Move(ulong tick, float x, float y) => new(tick, x, y, null);

    public static InputData Cast(ulong tick, uint ability, string? target = null, float aimX = 0, float aimY = 0) =>
        new(tick, 0f, 0f, null, ability, target, new Vec2(aimX, aimY));

    public void Dispose() => World.Dispose();
}
