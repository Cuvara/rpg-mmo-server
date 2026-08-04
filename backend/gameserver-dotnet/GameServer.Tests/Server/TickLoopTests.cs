using Shared.GameLogic.Components;
using GameServer.World;
using GameServer.Server;
using GameServer.Net;
using GameServer.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Server;

public class TickLoopTests
{
    private static (TickLoop loop, GameWorld world, ConnectionManager connMgr) CreateTickLoop()
    {
        var world = new GameWorld();
        var connMgr = new ConnectionManager();
        var logger = NullLogger.Instance;
        var handler = new InputHandler(world, logger);
        var loop = new TickLoop(world, handler, connMgr, GameConstants.DefaultTickRate, GameConstants.DefaultAoiRadius, logger);
        return (loop, world, connMgr);
    }

    [Fact]
    public void TickOnce_IncrementsTickCounter()
    {
        var (loop, world, _) = CreateTickLoop();

        var tickBefore = loop.CurrentTick;
        loop.TickOnce();
        var tickAfter = loop.CurrentTick;

        Assert.Equal(tickBefore + 1, tickAfter);
    }

    [Fact]
    public void TickOnce_MultipleTicks_IncrementCorrectly()
    {
        var (loop, world, _) = CreateTickLoop();

        loop.TickOnce();
        loop.TickOnce();
        loop.TickOnce();

        Assert.Equal((ulong)3, loop.CurrentTick);
    }

    [Fact]
    public void TickOnce_ProcessesPendingInputs()
    {
        var (loop, world, _) = CreateTickLoop();

        // Add a player entity
        var player = TestHelpers.CreatePlayer("p1", x: 0, y: 0);
        world.AddEntity(player);

        // Push movement input
        var input = new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null);
        world.PushInput("p1", input);

        // Process one tick
        loop.TickOnce();

        // Entity should have moved
        var entity = world.GetEntity("p1");
        Assert.NotNull(entity);
        Assert.True(entity.Value.Position.X > 0f, "Entity should have moved right");
    }

    [Fact]
    public void TickOnce_NoPlayers_NoError()
    {
        var (loop, world, _) = CreateTickLoop();

        var ex = Record.Exception(() => loop.TickOnce());
        Assert.Null(ex);
    }

    [Fact]
    public void TickOnce_NoPendingInputs_StillTicks()
    {
        var (loop, world, _) = CreateTickLoop();

        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 5, y: 5));

        var ex = Record.Exception(() => loop.TickOnce());
        Assert.Null(ex);
        Assert.Equal((ulong)1, loop.CurrentTick);
    }

    [Fact]
    public void TickOnce_DeadEntity_SkipsMovement()
    {
        var (loop, world, _) = CreateTickLoop();

        var player = TestHelpers.CreatePlayer("p1", x: 0, y: 0, hp: 0);
        player.Dead = true;
        world.AddEntity(player);

        world.PushInput("p1", new InputData(tick: 1, moveX: 5f, moveY: 0f, attackTargetId: null));
        loop.TickOnce();

        var entity = world.GetEntity("p1");
        Assert.NotNull(entity);
        // Dead entity should not have moved
        Assert.Equal(0f, entity.Value.Position.X, precision: 5);
    }

    [Fact]
    public void TickOnce_MultipleEntities_AllProcessed()
    {
        var (loop, world, _) = CreateTickLoop();

        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        world.AddEntity(TestHelpers.CreatePlayer("p2", x: 10, y: 10));

        world.PushInput("p1", new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null));
        world.PushInput("p2", new InputData(tick: 1, moveX: 0f, moveY: 1f, attackTargetId: null));

        loop.TickOnce();

        var p1 = world.GetEntity("p1");
        var p2 = world.GetEntity("p2");
        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.True(p1.Value.Position.X > 0f);
        Assert.True(p2.Value.Position.Y > 10f);
    }
}
