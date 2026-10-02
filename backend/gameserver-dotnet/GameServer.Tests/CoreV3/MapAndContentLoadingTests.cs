using System.Text;
using System.Text.Json;
using GameServer.Content;
using GameServer.Gameplay;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// Boot-time loading for Core v3: map files (ADR-28 decision 3) and the per-type content
/// files (ADR-30) — what loads, what is refused, and what is served.
/// </summary>
public class MapAndContentLoadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "corev3-" + Guid.NewGuid().ToString("N"));

    public MapAndContentLoadingTests() => Directory.CreateDirectory(Path.Combine(_dir, MapLoader.MapsDirectoryName));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteMap(string mapId, string json) =>
        File.WriteAllText(Path.Combine(_dir, MapLoader.MapsDirectoryName, mapId + ".json"), json);

    private static string RepoContentDir() =>
        ContentLoader.ResolveDefaultDirectory() ?? throw new InvalidOperationException("repo content not found");

    // ── Maps ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NoMapFile_IsTheFlatProtocol2World()
    {
        var bounds = MapBounds.FromSize(300, 300);
        MapLoadResult result = MapLoader.Load(_dir, "map_without_file", bounds);

        Assert.False(result.FromFile);
        Assert.True(result.Geometry.IsFlat);
        Assert.Equal(bounds, result.Geometry.Bounds);
    }

    [Fact]
    public void TheRepoExampleMap_LoadsAndValidates()
    {
        MapLoadResult result = MapLoader.Load(RepoContentDir(), "dev_arena", MapBounds.Default);

        Assert.True(result.FromFile);
        Assert.Equal(new MapBounds(-20, -20, 20, 20), result.Geometry.Bounds);
        Assert.Equal(2, result.Geometry.Boxes.Length);
        Assert.NotNull(result.Geometry.HeightField);
        Assert.True(result.Geometry.FindSpawn("default", out var spawn));
        Assert.Equal(-10f, spawn.Position.Y);
        Assert.Single(result.Geometry.Portals.ToArray());
    }

    [Theory]
    [InlineData("""{ "boxes": [] }""", "bounds")]
    [InlineData("""{ "bounds": { "minX": 0, "minY": 0, "maxX": 0, "maxY": 10 } }""", "zero width")]
    [InlineData("""{ "bounds": { "minX": -5, "minY": -5, "maxX": 5, "maxY": 5 }, "heightfield": { "originX": 0, "originY": 0, "cellSize": 1, "columns": 3, "rows": 3, "heights": [0, 0] } }""", "samples")]
    [InlineData("""{ "bounds": { "minX": -5, "minY": -5, "maxX": 5, "maxY": 5 }, "spawns": [ { "name": "default", "x": 50, "y": 0 } ] }""", "outside the map bounds")]
    [InlineData("""{ "bounds": { "minX": -5, "minY": -5, "maxX": 5, "maxY": 5 }, "boxes": [ { "minX": 0, "minY": 0, "minZ": 0, "maxX": 1, "maxY": 1 } ] }""", "boxes[0]")]
    [InlineData("""{ "bounds": { "minX": -5, """, "not valid JSON")]
    public void AnInvalidMapFile_RefusesTheBoot(string json, string expectedFragment)
    {
        WriteMap("broken", json);

        // MapLoadException is what Program.cs turns into "log critical, exit 1".
        var ex = Assert.Throws<MapLoadException>(() => MapLoader.Load(_dir, "broken", MapBounds.Default));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void AnUnsafeMapId_IsNeverJoinedIntoAPath(string mapId)
    {
        Assert.False(MapLoader.IsSafeMapId(mapId));
        Assert.False(MapLoader.Load(_dir, mapId, MapBounds.Default).FromFile);
    }

    // ── Content ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheRepoContentSet_LoadsEveryV3TypeFromItsOwnFile()
    {
        LoadedContent content = ContentLoader.Load(RepoContentDir());
        ContentDatabase db = content.Database;

        Assert.True(db.ItemCount > 0);
        Assert.True(db.StatCount >= 2);
        Assert.Contains(db.Stats, s => s.Key == "level");
        Assert.Contains(db.Stats, s => s.Key == "mana");
        Assert.True(db.StatusCount >= 2);

        // The vertical-slice set the placeholder content promises.
        Assert.Contains(db.Abilities, a => a.Delivery == AbilityDelivery.Projectile
            && a.Effects.Any(e => e.Kind == EffectKind.Damage)
            && a.Effects.Any(e => e.Kind == EffectKind.ApplyStatus && db.GetStatus(e.StatusId).Periodic.Kind == PeriodicKind.Damage));
        Assert.Contains(db.Abilities, a => a.Delivery == AbilityDelivery.Ground
            && a.Effects.Any(e => e.Kind == EffectKind.ApplyStatus
                && (db.GetStatus(e.StatusId).CrowdControl & (CrowdControl.Root | CrowdControl.Slow)) != 0));
        Assert.Contains(db.Abilities, a => a.Delivery == AbilityDelivery.Self
            && a.Effects.Any(e => e.Kind == EffectKind.ApplyStatus && db.GetStatus(e.StatusId).Periodic.Kind == PeriodicKind.Heal));

        Assert.True(content.Loot.TryGetForType("mob", out LootTable? mobLoot));
        Assert.All(mobLoot!.Entries, e => Assert.True(db.TryGetItem(e.ItemId, out _)));
    }

    [Fact]
    public void TheServedDocument_ComposesTheClientKeys_AndNeverTheLoot()
    {
        LoadedContent content = ContentLoader.Load(RepoContentDir());

        using var doc = JsonDocument.Parse(content.CanonicalBytes);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "items", "abilities", "stats", "statuses" }, keys);
        Assert.Equal(ContentLoader.ComputeHash(content.CanonicalBytes), content.Hash);

        // And the served document is itself loadable as a single-document set — what a
        // client that parses it with the same schema gets.
        LoadedContent again = ContentLoader.LoadFromBytes(content.CanonicalBytes, "served");
        Assert.Equal(content.Database.AbilityCount, again.Database.AbilityCount);
        Assert.Equal(content.Database.StatusCount, again.Database.StatusCount);
        Assert.Equal(0, again.Loot.Count);
    }

    private static ContentLoader.ContentFile Doc(string name, string json) =>
        new(name, Encoding.UTF8.GetBytes(json));

    private const string Items = """{ "items": [ { "id": "pelt", "name": "Pelt", "slot": "none", "rarity": "common", "stackMax": 5 } ] }""";

    [Fact]
    public void AKeyDefinedInTwoFiles_IsRefused()
    {
        var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadFromFiles(new[]
        {
            Doc("items.json", """{ "items": [], "stats": [ { "id": 1, "key": "level", "default": 1 } ] }"""),
            Doc("stats.json", """{ "stats": [ { "id": 2, "key": "mana", "default": 1 } ] }"""),
        }));
        Assert.Contains("'stats' is defined in both", ex.Message);
    }

    [Theory]
    [InlineData("""{ "abilities": [ { "id": 1, "name": "X", "delivery": "laser", "effects": [], "cooldownTicks": 0 } ] }""", "delivery 'laser'")]
    [InlineData("""{ "abilities": [ { "id": 1, "name": "X", "delivery": "self", "effect": "heal", "effects": [ { "kind": "heal", "power": 1 } ], "cooldownTicks": 0 } ] }""", "legacy")]
    [InlineData("""{ "abilities": [ { "id": 1, "name": "X", "delivery": "projectile", "effects": [ { "kind": "damage", "power": 1 } ], "cooldownTicks": 0 } ] }""", "no 'projectile' block")]
    [InlineData("""{ "abilities": [ { "id": 1, "name": "X", "delivery": "self", "effects": [ { "kind": "apply_status", "statusId": 9 } ], "cooldownTicks": 0 } ] }""", "does not exist")]
    [InlineData("""{ "abilities": [ { "id": 1, "name": "X", "delivery": "self", "effects": [ { "kind": "zap", "power": 1 } ], "cooldownTicks": 0 } ] }""", "kind 'zap'")]
    [InlineData("""{ "statuses": [ { "id": 1, "key": "x", "durationTicks": 5, "crowdControl": [ "freeze" ] } ] }""", "crowdControl 'freeze'")]
    [InlineData("""{ "statuses": [ { "id": 1, "key": "x" } ] }""", "durationTicks")]
    [InlineData("""{ "statuses": [ { "id": 1, "key": "x", "durationTicks": 5, "periodic": { "kind": "damage", "intervalTicks": 0, "amount": 1 } } ] }""", "intervalTicks")]
    [InlineData("""{ "stats": [ { "id": 1, "key": "level" } ] }""", "'default' is missing")]
    [InlineData("""{ "loot": [ { "id": "t", "entityType": "mob", "despawnTicks": 10, "entries": [ { "itemId": "nope", "chancePermille": 10 } ] } ] }""", "does not exist in items.json")]
    [InlineData("""{ "loot": [ { "id": "t", "entityType": "mob", "despawnTicks": 10, "entries": [ { "itemId": "pelt", "chancePermille": 1001 } ] } ] }""", "chancePermille")]
    [InlineData("""{ "loot": [ { "id": "t", "entityType": "mob", "despawnTicks": 10, "entries": [ { "itemId": "pelt", "chancePermille": 10, "min": 1, "max": 9 } ] } ] }""", "stackMax")]
    [InlineData("""{ "loot": [ { "id": "t", "entityType": "mob", "entries": [ { "itemId": "pelt", "chancePermille": 10 } ] } ] }""", "despawnTicks")]
    public void InvalidV3Content_IsRefusedWithADiagnosis(string second, string expectedFragment)
    {
        var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadFromFiles(new[]
        {
            Doc("items.json", Items),
            Doc("extra.json", second),
        }));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void ALegacyAbilityStillLoads_AsASingleEffect()
    {
        LoadedContent c = ContentLoader.LoadFromFiles(new[]
        {
            Doc("items.json", Items),
            Doc("abilities.json", """{ "abilities": [ { "id": 5, "name": "Old", "targeting": "entity", "effect": "damage", "range": 3, "power": 7, "cooldownTicks": 2 } ] }"""),
        });

        AbilityDefinition a = c.Database.GetAbility(5);
        Assert.Equal(AbilityDelivery.Entity, a.Delivery);
        Assert.Equal(EffectSpec.Damage(7), Assert.Single(a.Effects));
    }

    [Fact]
    public void StatusModifiersAndCrowdControl_ParseIntoTheSharedDefinition()
    {
        LoadedContent c = ContentLoader.LoadFromFiles(new[]
        {
            Doc("items.json", Items),
            Doc("x.json", """
            {
              "stats": [ { "id": 3, "key": "fire_resist", "default": 0 } ],
              "statuses": [ { "id": 9, "key": "warded", "durationTicks": 0, "maxStacks": 2,
                "crowdControl": [ "silence", "slow" ], "slowPermille": 250,
                "modifiers": [ { "target": "defense", "multiplierPermille": 200 },
                               { "target": "stat", "statId": 3, "add": 15 } ] } ]
            }
            """),
        });

        StatusDefinition s = c.Database.GetStatus(9);
        Assert.True(s.IsPermanent);
        Assert.Equal(2, s.MaxStacks);
        Assert.Equal(CrowdControl.Silence | CrowdControl.Slow, s.CrowdControl);
        Assert.Equal(250, s.SlowPermille);
        Assert.Equal(StatModifier.For(StatModifierTarget.Defense, 0, 200).MultiplierPermille, s.GetModifier(0).MultiplierPermille);
        Assert.True(s.GetModifier(1).Affects(StatModifierTarget.ContentStat, 3));
        Assert.Equal(15, s.GetModifier(1).Add);
    }
}
