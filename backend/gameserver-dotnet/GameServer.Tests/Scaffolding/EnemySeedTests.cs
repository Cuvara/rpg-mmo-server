using System.Text.Json;
using GameServer.Observability;
using GameServer.Scaffolding;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Scaffolding;

/// <summary>
/// <c>GAMESERVER_ENEMY_SEED</c>: parse rules, and the property it exists for - two servers
/// with the same seed and the same players place the same enemies at the same points.
/// <c>/debug/entities</c> is used to read the placements, so its document is covered here too.
/// </summary>
public class EnemySeedTests
{
    private static bool Parse(string? seed, out EnemyAiSettings? settings, out string? error)
    {
        var env = new Dictionary<string, string?> { [EnemyAiSettings.EnvSeed] = seed };
        return EnemyAiSettings.TryCreate(n => env.GetValueOrDefault(n), MapBounds.Default, out settings, out error);
    }

    [Fact]
    public void Unset_LeavesSeedNull_AndTheBannerUnchanged()
    {
        Assert.True(Parse(null, out var s, out var err), err);
        Assert.Null(s!.Seed);
        Assert.DoesNotContain("seed=", s.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData(" 2147483647 ", int.MaxValue)]
    public void AWholeNumber_IsTheSeed_AndIsShownOnTheBanner(string raw, int expected)
    {
        Assert.True(Parse(raw, out var s, out var err), err);
        Assert.Equal(expected, s!.Seed);
        Assert.Contains($"seed={expected}", s.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4O")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    public void AnythingElse_IsRefused_NamingTheVariable(string raw)
    {
        Assert.False(Parse(raw, out var s, out var err));
        Assert.Null(s);
        Assert.Contains(EnemyAiSettings.EnvSeed, err, StringComparison.Ordinal);
    }

    [Fact]
    public void SameSeed_SamePlayers_PlacesEnemiesAtTheSamePoints()
    {
        List<(string Id, float X, float Y)> a = SpawnWave(seed: 7);
        List<(string Id, float X, float Y)> b = SpawnWave(seed: 7);
        List<(string Id, float X, float Y)> c = SpawnWave(seed: 8);

        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DebugDocument_ListsEveryEntity_WithItsAuthoritativePosition()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", 3f, -4f));
        world.Spawn(new EntityState { Id = "m1", Type = "mob", Position = new Vec2(10f, 2.5f), Hp = 7, MaxHp = 16, Speed = 2.5f }, EntityTags.EnemyAi);

        byte[] body = DebugEntitiesDocument.Write(world, tick: 123, monotonicSeconds: 4.5);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        Assert.Equal(DebugEntitiesDocument.Schema, root.GetProperty("schema").GetString());
        Assert.Equal(123UL, root.GetProperty("tick").GetUInt64());
        Assert.Equal(2, root.GetProperty("count").GetInt32());

        var byId = root.GetProperty("entities").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e);
        Assert.Equal(3f, byId["p1"].GetProperty("x").GetSingle());
        Assert.Equal(-4f, byId["p1"].GetProperty("y").GetSingle());
        Assert.Equal("mob", byId["m1"].GetProperty("type").GetString());
        Assert.Equal(10f, byId["m1"].GetProperty("x").GetSingle());
        Assert.Equal(7, byId["m1"].GetProperty("hp").GetInt32());
        Assert.False(byId["m1"].GetProperty("dead").GetBoolean());
    }

    /// <summary>Two players, one wave interval of world ticks, then every enemy's position.</summary>
    private static List<(string Id, float X, float Y)> SpawnWave(int seed)
    {
        var env = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvSeed] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [EnemyAiSettings.EnvAttacksEnabled] = "off",
        };
        Assert.True(EnemyAiSettings.TryCreate(n => env.GetValueOrDefault(n), MapBounds.Default, out var settings, out var err), err);

        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", 0f, 0f));
        world.AddEntity(TestHelpers.CreatePlayer("p2", 40f, 0f));
        var spawner = new EnemySpawner(world, 15, settings!, NullLogger.Instance);

        // 1.5 s wave interval at 15 Hz: 23 ticks crosses it once.
        for (ulong t = 1; t <= 23; t++) spawner.Tick(t);

        using JsonDocument doc = JsonDocument.Parse(DebugEntitiesDocument.Write(world, 0, 0));
        return doc.RootElement.GetProperty("entities").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() != "player")
            .Select(e => (e.GetProperty("id").GetString()!, e.GetProperty("x").GetSingle(), e.GetProperty("y").GetSingle()))
            .OrderBy(e => e.Item1, StringComparer.Ordinal)
            .ToList();
    }
}
