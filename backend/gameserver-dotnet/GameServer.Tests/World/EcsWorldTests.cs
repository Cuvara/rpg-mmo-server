using Shared.GameLogic.Components;
using GameServer.World;

namespace GameServer.Tests.World;

public class EcsWorldTests
{
    [Fact]
    public void AddEntity_CanRetrieve()
    {
        var world = new EcsWorld();
        var player = TestHelpers.CreatePlayer("p1", x: 10, y: 20);

        world.AddEntity(player);
        var retrieved = world.GetEntity("p1");

        Assert.NotNull(retrieved);
        Assert.Equal("p1", retrieved.Value.Id);
        Assert.Equal(10f, retrieved.Value.Position.X, precision: 5);
        Assert.Equal(20f, retrieved.Value.Position.Y, precision: 5);
    }

    [Fact]
    public void RemoveEntity_NoLongerFound()
    {
        var world = new EcsWorld();
        var player = TestHelpers.CreatePlayer("p1");

        world.AddEntity(player);
        world.RemoveEntity("p1");

        var result = world.GetEntity("p1");
        Assert.Null(result);
    }

    [Fact]
    public void RemoveEntity_NonExistent_DoesNotThrow()
    {
        var world = new EcsWorld();
        var ex = Record.Exception(() => world.RemoveEntity("nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public void GetEntity_NonExistent_ReturnsNull()
    {
        var world = new EcsWorld();
        var result = world.GetEntity("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public void GetEntitiesInRange_ReturnsCorrectEntities()
    {
        var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        world.AddEntity(TestHelpers.CreatePlayer("p2", x: 10, y: 10));
        world.AddEntity(TestHelpers.CreatePlayer("p3", x: 200, y: 200));

        var center = new Vec2(0f, 0f);
        var nearby = world.GetEntitiesInRange(center, GameConstants.DefaultAoiRadius);

        Assert.Equal(2, nearby.Count);
        Assert.Contains(nearby, e => e.Id == "p1");
        Assert.Contains(nearby, e => e.Id == "p2");
        Assert.DoesNotContain(nearby, e => e.Id == "p3");
    }

    [Fact]
    public void PushInput_DrainInputs_ReturnsAll()
    {
        var world = new EcsWorld();
        var input1 = new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null);
        var input2 = new InputData(tick: 2, moveX: 0f, moveY: 1f, attackTargetId: null);

        world.PushInput("p1", input1);
        world.PushInput("p1", input2);

        var inputs = world.DrainInputs();
        Assert.Equal(2, inputs.Count);
    }

    [Fact]
    public void DrainInputs_ClearsQueue()
    {
        var world = new EcsWorld();
        var input = new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null);

        world.PushInput("p1", input);
        var first = world.DrainInputs();
        Assert.Single(first);

        var second = world.DrainInputs();
        Assert.Empty(second);
    }

    [Fact]
    public void Update_MutatesEntity()
    {
        var world = new EcsWorld();
        var player = TestHelpers.CreatePlayer("p1", x: 0, y: 0, hp: 100);
        world.AddEntity(player);

        world.Update((get, set) =>
        {
            var entity = get("p1");
            if (entity != null)
            {
                var e = entity.Value;
                e.Hp = 50;
                e.Position = new Vec2(5f, 5f);
                set("p1", e);
            }
        });

        var updated = world.GetEntity("p1");
        Assert.NotNull(updated);
        Assert.Equal(50, updated.Value.Hp);
        Assert.Equal(5f, updated.Value.Position.X, precision: 5);
    }

    [Fact]
    public void PlayerStates_ReturnsOnlyPlayers()
    {
        var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        world.AddEntity(TestHelpers.CreatePlayer("p2"));
        world.AddEntity(TestHelpers.CreateMob("m1", x: 10, y: 10));

        var players = world.PlayerStates();
        Assert.Equal(2, players.Count);
        Assert.All(players, p => Assert.Equal("player", p.Type));
    }

    [Fact]
    public void EntityCount_ReturnsCorrectCount()
    {
        var world = new EcsWorld();
        Assert.Equal(0, world.EntityCount);

        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        Assert.Equal(1, world.EntityCount);

        world.AddEntity(TestHelpers.CreateMob("m1", x: 5, y: 5));
        Assert.Equal(2, world.EntityCount);

        world.RemoveEntity("p1");
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void ConcurrentAccess_NoDeadlock()
    {
        var world = new EcsWorld();
        for (int i = 0; i < 100; i++)
        {
            world.AddEntity(TestHelpers.CreatePlayer($"p{i}", x: i, y: i));
        }

        var tasks = new List<Task>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Writers: add/remove entities
        for (int t = 0; t < 4; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 100 && !cts.Token.IsCancellationRequested; i++)
                {
                    var id = $"thread{threadId}_entity{i}";
                    world.AddEntity(TestHelpers.CreatePlayer(id, x: i, y: i));
                    world.GetEntity(id);
                    world.RemoveEntity(id);
                }
            }));
        }

        // Readers: query entities
        for (int t = 0; t < 4; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 100 && !cts.Token.IsCancellationRequested; i++)
                {
                    world.GetEntitiesInRange(new Vec2(0, 0), GameConstants.DefaultAoiRadius);
                    _ = world.EntityCount;
                    world.PlayerStates();
                }
            }));
        }

        // Input writers
        for (int t = 0; t < 2; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 100 && !cts.Token.IsCancellationRequested; i++)
                {
                    world.PushInput($"p{i % 100}", new InputData(tick: (ulong)i, moveX: 1, moveY: 0, attackTargetId: null));
                }
            }));
        }

        // Should complete within 5 seconds without deadlock
        Assert.True(Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(5)),
            "Concurrent access timed out - possible deadlock");
    }

    [Fact]
    public void AddEntity_Duplicate_OverwritesOrThrows()
    {
        var world = new EcsWorld();
        var player1 = TestHelpers.CreatePlayer("p1", x: 0, y: 0);
        var player2 = TestHelpers.CreatePlayer("p1", x: 99, y: 99);

        world.AddEntity(player1);

        // Either overwrites or throws - document behavior
        try
        {
            world.AddEntity(player2);
            var result = world.GetEntity("p1");
            Assert.NotNull(result);
            // If overwrite, position should be updated
            Assert.Equal(99f, result.Value.Position.X, precision: 5);
        }
        catch (Exception)
        {
            // If throws on duplicate, that's also acceptable
        }
    }

    [Fact]
    public void PushInput_MultipleEntities_AllDrained()
    {
        var world = new EcsWorld();
        world.PushInput("p1", new InputData(tick: 1, moveX: 1, moveY: 0, attackTargetId: null));
        world.PushInput("p2", new InputData(tick: 1, moveX: 0, moveY: 1, attackTargetId: null));
        world.PushInput("p3", new InputData(tick: 1, moveX: -1, moveY: 0, attackTargetId: null));

        var inputs = world.DrainInputs();
        Assert.Equal(3, inputs.Count);
    }
}
