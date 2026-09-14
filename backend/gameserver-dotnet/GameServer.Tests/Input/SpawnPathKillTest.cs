using GameServer.Input;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Input;

public class SpawnPathKillTest
{
    [Fact]
    public void EnemySpawnedLikeTheSpawner_DiesToTwoHits()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0, atk: 10));

        world.Spawn(new EntityState
        {
            Id = "enemy-1",
            Type = "mob",
            Position = new Vec2(1, 0),
            Hp = 16, MaxHp = 16,
            Speed = 2.5f,
            Attack = 5, Defense = 2,
        }, EntityTags.EnemyAi);

        var kills = 0;
        var handler = new InputHandler(world, NullLogger.Instance,
            (v, k) => kills++, GameConstants.DefaultTickRate, MapBounds.Default);

        handler.ProcessInput("p1", new InputData(1, 0f, 0f, "enemy-1"), currentTick: 1);
        ulong next = 1UL + (ulong)GameConstants.AttackCooldownTicks(GameConstants.DefaultTickRate);
        handler.ProcessInput("p1", new InputData(next, 0f, 0f, "enemy-1"), currentTick: next);

        Assert.Equal(2, handler.Attacks.Accepted);
        Assert.Equal(1, handler.Attacks.Kills);
        Assert.Equal(1, kills);
    }
}
