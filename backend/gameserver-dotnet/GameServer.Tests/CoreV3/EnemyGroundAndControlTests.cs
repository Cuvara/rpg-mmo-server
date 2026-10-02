using GameServer.Scaffolding;
using GameServer.World;
using GameServer.World.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;
using Shared.GameLogic.World;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// Enemies stay planar movers (ADR-28 leaves them on the protocol 2 path) but stand on the
/// ground, and honour the crowd control a player's abilities put on them (ADR-30).
/// </summary>
public class EnemyGroundAndControlTests
{
    private static EnemyAiSettings Peaceful(MapBounds bounds)
    {
        var lookup = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvAttacksEnabled] = "off",
        };
        Assert.True(EnemyAiSettings.TryCreate(n => lookup.GetValueOrDefault(n), bounds, out var s, out var err), err);
        return s!;
    }

    private static Vec3 Pos(EcsWorld world, string id)
    {
        Vec3 at = default;
        world.UpdateComponents(w =>
        {
            ref Position p = ref w.PositionOf(w.Resolve(id));
            at = new Vec3(p.Value.X, p.Value.Y, p.Z);
        });
        return at;
    }

    [Fact]
    public void AnEnemyWalkingOverTerrain_KeepsItsFeetOnTheGround_AndARootedOneStaysPut()
    {
        // Ramp: height = 0.5 * x over [-20, 20].
        var hf = new HeightField(-20f, -20f, 20f, 3, 3, new[] { -10f, 0f, 10f, -10f, 0f, 10f, -10f, 0f, 10f });
        var geo = new MapGeometry(MapBounds.FromSize(40, 40), hf, null, null, null);
        var content = GameServer.Content.ContentLoader.LoadFromBytes(
            System.Text.Encoding.UTF8.GetBytes(SimFixture.ContentJson), "t").Database;

        using var world = new EcsWorld();
        world.Gameplay.Configure(geometry: geo, content: content);
        world.AddEntity(TestHelpers.CreatePlayer("p1", -15f, 0f));
        world.Spawn(new EntityState { Id = "walker", Type = "mob", Position = new Vec2(15f, 0f), Hp = 50, MaxHp = 50, Speed = 3f }, EntityTags.EnemyAi);
        world.Spawn(new EntityState { Id = "rooted", Type = "mob", Position = new Vec2(10f, 5f), Hp = 50, MaxHp = 50, Speed = 3f }, EntityTags.EnemyAi);

        Assert.Equal(7.5f, Pos(world, "walker").Z, 4);

        world.UpdateComponents(w =>
        {
            SimRecord rec = w.RecordOf(w.Resolve("rooted"))!;
            rec.Statuses!.Apply(content.GetStatus(SimFixture.Rooted), 1, null);
        });

        var spawner = new EnemySpawner(world, 15, Peaceful(geo.Bounds), NullLogger.Instance);
        for (ulong t = 1; t <= 4; t++) spawner.Tick(t); // statuses do not tick here: the root holds

        Vec3 walker = Pos(world, "walker");
        Assert.True(walker.X < 15f, "the walker should have moved toward the player");
        Assert.Equal(walker.X * 0.5f, walker.Z, 3);

        Vec3 rooted = Pos(world, "rooted");
        Assert.Equal(10f, rooted.X);
        Assert.Equal(5f, rooted.Y);
    }
}
