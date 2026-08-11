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
    private static (TickLoop loop, EcsWorld world, ConnectionManager connMgr) CreateTickLoop(
        int tickRate = GameConstants.DefaultTickRate,
        MapBounds? bounds = null)
    {
        var world = new EcsWorld();
        var connMgr = new ConnectionManager();
        var logger = NullLogger.Instance;
        var handler = new InputHandler(world, logger, null, tickRate, bounds);
        var loop = new TickLoop(world, handler, connMgr, tickRate, GameConstants.DefaultAoiRadius, logger);
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

    // ───────────────────────── movement model integration ─────────────────────────

    [Fact]
    public void ScriptedInputSequence_LandsAtIntegratedPosition()
    {
        // 30 ticks at 15Hz holding "right" with speed 5 u/s:
        //   30 * (5 * 1/15) = 10 world units.
        const int tickRate = 15;
        const float speed = 5f;
        const int ticks = 30;

        var (loop, world, _) = CreateTickLoop(tickRate);
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0, speed: speed));

        for (int i = 0; i < ticks; i++)
        {
            world.PushInput("p1", new InputData(tick: (ulong)(i + 1), moveX: 1f, moveY: 0f, attackTargetId: null));
            loop.TickOnce();
        }

        var p = world.GetEntity("p1");
        Assert.NotNull(p);
        Assert.Equal(ticks * speed / tickRate, p!.Value.Position.X, precision: 3);
        Assert.Equal(0f, p.Value.Position.Y, precision: 3);
        Assert.Equal((ulong)ticks, p.Value.LastInputTick);
    }

    [Fact]
    public void ScriptedInputSequence_DirectionChange_IsIntegratedPerLeg()
    {
        // 15 ticks right, then 15 ticks up, at 15Hz with speed 4 u/s => (4, 4).
        const int tickRate = 15;
        var (loop, world, _) = CreateTickLoop(tickRate);
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0, speed: 4f));

        ulong tick = 0;
        for (int i = 0; i < tickRate; i++)
        {
            world.PushInput("p1", new InputData(++tick, 1f, 0f, null));
            loop.TickOnce();
        }
        for (int i = 0; i < tickRate; i++)
        {
            world.PushInput("p1", new InputData(++tick, 0f, 1f, null));
            loop.TickOnce();
        }

        var p = world.GetEntity("p1")!.Value;
        Assert.Equal(4f, p.Position.X, precision: 3);
        Assert.Equal(4f, p.Position.Y, precision: 3);
    }

    [Fact]
    public void InputSpam_DoesNotMoveFurtherThanOneInputPerTick()
    {
        // Anti-cheat: 10 input packets buffered into a single tick must produce the
        // same displacement as one packet — movement is per-tick, not per-message.
        var (spamLoop, spamWorld, _) = CreateTickLoop();
        spamWorld.AddEntity(TestHelpers.CreatePlayer("p1", speed: 5f));
        for (int i = 0; i < 10; i++)
            spamWorld.PushInput("p1", new InputData(tick: (ulong)(i + 1), moveX: 1f, moveY: 0f, attackTargetId: null));
        spamLoop.TickOnce();

        var (fairLoop, fairWorld, _) = CreateTickLoop();
        fairWorld.AddEntity(TestHelpers.CreatePlayer("p1", speed: 5f));
        fairWorld.PushInput("p1", new InputData(tick: 1, moveX: 1f, moveY: 0f, attackTargetId: null));
        fairLoop.TickOnce();

        float spamX = spamWorld.GetEntity("p1")!.Value.Position.X;
        float fairX = fairWorld.GetEntity("p1")!.Value.Position.X;

        Assert.Equal(fairX, spamX, precision: 5);
        Assert.Equal(5f / GameConstants.DefaultTickRate, spamX, precision: 5);
        // The newest input still wins the tick-ack, so the client can reconcile.
        Assert.Equal((ulong)10, spamWorld.GetEntity("p1")!.Value.LastInputTick);
    }

    [Fact]
    public void TickOnce_DiagonalInput_IsNotFasterThanCardinal()
    {
        var (loop, world, _) = CreateTickLoop();
        world.AddEntity(TestHelpers.CreatePlayer("cardinal", speed: 5f));
        world.AddEntity(TestHelpers.CreatePlayer("diagonal", speed: 5f));

        world.PushInput("cardinal", new InputData(1, 1f, 0f, null));
        world.PushInput("diagonal", new InputData(1, 1f, 1f, null));
        loop.TickOnce();

        var c = world.GetEntity("cardinal")!.Value.Position;
        var d = world.GetEntity("diagonal")!.Value.Position;

        Assert.Equal(c.Magnitude, d.Magnitude, precision: 4);
    }

    [Fact]
    public void TickOnce_GrosslyInvalidInput_IsDroppedNotApplied()
    {
        var (loop, world, _) = CreateTickLoop();
        world.AddEntity(TestHelpers.CreatePlayer("p1", speed: 5f));

        world.PushInput("p1", new InputData(1, float.NaN, 0f, null));
        world.PushInput("p1", new InputData(2, 1000f, 1000f, null));
        var ex = Record.Exception(() => loop.TickOnce());

        Assert.Null(ex);
        var p = world.GetEntity("p1")!.Value;
        Assert.Equal(0f, p.Position.X, precision: 5);
        Assert.Equal(0f, p.Position.Y, precision: 5);
    }

    [Fact]
    public void TickOnce_ClampsToMapBounds()
    {
        var bounds = MapBounds.FromSize(10f, 10f);   // -5..5
        var (loop, world, _) = CreateTickLoop(GameConstants.DefaultTickRate, bounds);
        world.AddEntity(TestHelpers.CreatePlayer("p1", speed: 20f));

        for (int i = 0; i < 200; i++)
        {
            world.PushInput("p1", new InputData(tick: (ulong)(i + 1), moveX: 1f, moveY: 1f, attackTargetId: null));
            loop.TickOnce();
        }

        var p = world.GetEntity("p1")!.Value;
        Assert.Equal(5f, p.Position.X, precision: 4);
        Assert.Equal(5f, p.Position.Y, precision: 4);
        Assert.True(bounds.Contains(p.Position));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    public void TickRate_DoesNotChangeTravelSpeed(int tickRate)
    {
        // One simulated second of holding "right" at speed 6 => 6 units, any tick rate.
        var (loop, world, _) = CreateTickLoop(tickRate);
        world.AddEntity(TestHelpers.CreatePlayer("p1", speed: 6f));

        for (int i = 0; i < tickRate; i++)
        {
            world.PushInput("p1", new InputData(tick: (ulong)(i + 1), moveX: 1f, moveY: 0f, attackTargetId: null));
            loop.TickOnce();
        }

        Assert.Equal(6f, world.GetEntity("p1")!.Value.Position.X, precision: 3);
    }
}
