using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Input;
using GameServer.Net;
using GameServer.Server;
using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Server;

/// <summary>
/// The properties multi-rate scheduling has to preserve, tested through the real tick loop
/// rather than through the rate arithmetic.
///
/// <para>The theme running through all of them is that <b>a rate is a scheduling decision,
/// not a gameplay decision</b>. Changing SIM_CRITICAL_HZ must change how often the server
/// computes, and must not change how far a player travels in a second, how long a cooldown
/// lasts, or how often a client hears from the server.</para>
/// </summary>
public class MultiRateSimulationTests
{
    private static (TickLoop loop, EcsWorld world) CreateLoop(SimulationRates rates)
    {
        var world = new EcsWorld();
        var connections = new ConnectionManager();
        var logger = NullLogger.Instance;
        var handler = new InputHandler(world, logger, null, rates.CriticalHz, null);
        var loop = new TickLoop(world, handler, connections, rates,
            GameConstants.DefaultAoiRadius, logger);
        return (loop, world);
    }

    /// <summary>
    /// Simulate one wall-clock second at several configurations, with a client that sends
    /// input on every base tick, and assert the player travelled the same distance.
    ///
    /// <para>This is the time-model test the spec asks for, in its strongest form: it is not
    /// checking that <c>dt = 1/hz</c>, it is checking that the product of the rate and its
    /// timestep is invariant, which is the property a player would notice if it broke.</para>
    /// </summary>
    [Theory]
    [InlineData(15, 15, 5)]
    [InlineData(30, 15, 5)]
    [InlineData(60, 15, 5)]
    [InlineData(60, 20, 10)]
    public void OneSecondOfMovement_CoversTheSameDistanceAtEveryRate(
        int critical, int world, int background)
    {
        SimulationRates rates = SimulationRates.Uniform(1); // replaced below; keeps the compiler happy
        Assert.True(SimulationRates.TryCreate(critical, world, background, out SimulationRates? created, out _));
        rates = created!;

        (TickLoop loop, EcsWorld ecs) = CreateLoop(rates);
        ecs.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        float speed = ecs.GetEntity("p1")!.Value.Speed;

        // One second of base ticks, one input per tick — the send rate a predicting client
        // would use.
        for (int t = 1; t <= rates.BaseHz; t++)
        {
            ecs.PushInput("p1", new InputData((ulong)t, 1f, 0f, null));
            loop.TickOnce();
        }

        float travelled = ecs.GetEntity("p1")!.Value.Position.X;

        // One second at `speed` units/second, whatever the rate. The tolerance absorbs
        // float accumulation over up to 60 steps, not a rate difference.
        Assert.Equal(speed, travelled, precision: 3);
    }

    /// <summary>
    /// The same second, but with a client that sends at 15Hz while the server simulates at
    /// 60Hz — the realistic case, and the one that motivated holding the last input.
    ///
    /// <para>Without the hold, this player would receive one integration step per packet and
    /// travel a quarter of the distance: movement speed would silently become a function of
    /// the client's send rate. <c>MovementSystem</c>'s own documentation says travel
    /// distance must depend "only on wall-clock time and the entity's speed stat — never on
    /// how many input packets a client sends"; this test is that sentence.</para>
    /// </summary>
    [Fact]
    public void AClientSendingSlowerThanTheBaseRate_StillMovesAtItsFullSpeed()
    {
        SimulationRates rates = SimulationRates.Default; // 60 / 15 / 5
        (TickLoop loop, EcsWorld ecs) = CreateLoop(rates);
        ecs.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        float speed = ecs.GetEntity("p1")!.Value.Speed;

        ulong clientTick = 0;
        for (int t = 1; t <= rates.BaseHz; t++)
        {
            // A packet every 4th base tick: a 15Hz client against a 60Hz server.
            if ((t - 1) % rates.WorldEvery == 0)
            {
                ecs.PushInput("p1", new InputData(++clientTick, 1f, 0f, null));
            }
            loop.TickOnce();
        }

        float travelled = ecs.GetEntity("p1")!.Value.Position.X;

        Assert.Equal(15, (int)clientTick);          // it really did only send 15 packets
        Assert.Equal(speed, travelled, precision: 3); // and still covered a full second
    }

    /// <summary>
    /// The hold is bounded. A client that goes quiet coasts for at most one world interval
    /// and then stops — it does not drift forever on a stale direction.
    /// </summary>
    [Fact]
    public void AClientThatStopsSending_CoastsForAtMostOneWorldIntervalAndStops()
    {
        SimulationRates rates = SimulationRates.Default;
        (TickLoop loop, EcsWorld ecs) = CreateLoop(rates);
        ecs.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        float speed = ecs.GetEntity("p1")!.Value.Speed;

        ecs.PushInput("p1", new InputData(1, 1f, 0f, null));
        loop.TickOnce();                       // the packet tick
        for (int i = 0; i < 60; i++) loop.TickOnce(); // then a full second of silence

        float travelled = ecs.GetEntity("p1")!.Value.Position.X;

        // WorldEvery steps in total: the packet's own, plus the held ones up to the expiry.
        float expected = speed * rates.WorldEvery / rates.CriticalHz;
        Assert.Equal(expected, travelled, precision: 4);

        // And emphatically not a whole second of travel.
        Assert.True(travelled < speed / 2f,
            $"a silent client coasted {travelled} units, i.e. it never stopped");
    }

    /// <summary>
    /// An explicit stop is immediate. Releasing the stick sends a deadzone vector, which
    /// clears the hold rather than refreshing it — otherwise a player who stopped would
    /// keep sliding for an interval, which is worse than the problem the hold solves.
    /// </summary>
    [Fact]
    public void AnExplicitDeadzoneInput_StopsTheEntityImmediately()
    {
        SimulationRates rates = SimulationRates.Default;
        (TickLoop loop, EcsWorld ecs) = CreateLoop(rates);
        ecs.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));

        ecs.PushInput("p1", new InputData(1, 1f, 0f, null));
        loop.TickOnce();
        float afterMove = ecs.GetEntity("p1")!.Value.Position.X;

        ecs.PushInput("p1", new InputData(2, 0f, 0f, null)); // stick released
        loop.TickOnce();
        for (int i = 0; i < 10; i++) loop.TickOnce();

        Assert.Equal(afterMove, ecs.GetEntity("p1")!.Value.Position.X, precision: 5);
    }

    /// <summary>
    /// On a single-rate configuration the hold cannot fire at all, because a held direction
    /// is by definition at least one tick old on any tick where it would be applied. That is
    /// what makes this change a strict generalisation of the old model — and it is why the
    /// byte-identity and characterization suites still pass untouched.
    /// </summary>
    [Fact]
    public void OnASingleRateServer_MovementIsStillExactlyOneStepPerPacket()
    {
        (TickLoop loop, EcsWorld ecs) = CreateLoop(SimulationRates.Uniform(15));
        ecs.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0));
        float speed = ecs.GetEntity("p1")!.Value.Speed;

        ecs.PushInput("p1", new InputData(1, 1f, 0f, null));
        loop.TickOnce();
        for (int i = 0; i < 20; i++) loop.TickOnce(); // silence

        Assert.Equal(speed / 15f, ecs.GetEntity("p1")!.Value.Position.X, precision: 5);
    }

    /// <summary>
    /// A 500ms cooldown stays 500ms. The cooldown is counted in base ticks, so the count has
    /// to be derived from the rate that advances the base tick — deriving it from the world
    /// rate while comparing it against a 60Hz counter would make it last four times too long.
    /// </summary>
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void AttackCooldown_LastsTheSameWallClockTimeAtEveryCriticalRate(int criticalHz)
    {
        int cooldownTicks = GameConstants.AttackCooldownTicks(criticalHz);
        double cooldownMs = cooldownTicks * 1000.0 / criticalHz;

        // Ceil-rounded, so never shorter than the 500ms it represents, and never more than
        // one tick longer.
        Assert.InRange(cooldownMs, GameConstants.AttackCooldownMs, GameConstants.AttackCooldownMs + 1000.0 / criticalHz);
    }

    /// <summary>
    /// The handler is built from the CRITICAL rate, which is what makes the test above hold
    /// for the running server rather than only for the constant.
    /// </summary>
    [Fact]
    public void TheInputHandlerIsBuiltFromTheCriticalRate()
    {
        var world = new EcsWorld();
        var handler = new InputHandler(world, NullLogger.Instance, null,
            SimulationRates.Default.CriticalHz, null);

        Assert.Equal(1f / 60f, handler.DeltaTime, precision: 6);
        Assert.Equal(GameConstants.AttackCooldownTicks(60), handler.CooldownTicks);
    }

    /// <summary>
    /// Simulation rate and replication rate are separate concepts, and the tick loop keeps
    /// them separate: at 60/15/5 the world group — and therefore the snapshot broadcast —
    /// is due on one base tick in four.
    ///
    /// <para>Coupling them would quadruple outbound bandwidth per client and quietly
    /// redefine the keyframe interval, which counts snapshots: 30 snapshots is 2 seconds at
    /// 15Hz and half a second at 60Hz.</para>
    /// </summary>
    [Fact]
    public void ReplicationIsGatedToTheWorldRate_NotTheBaseRate()
    {
        (TickLoop loop, EcsWorld _) = CreateLoop(SimulationRates.Default);

        int due = 0;
        for (int t = 1; t <= 60; t++)
        {
            loop.TickOnce();
            if (loop.WorldDueNow) due++;
        }

        Assert.Equal(15, due);
    }

    /// <summary>
    /// The base tick is the canonical identity: it advances once per base tick and nothing
    /// else, so a tick number means one thing across input acknowledgement, cooldowns and
    /// snapshots.
    /// </summary>
    [Fact]
    public void TheBaseTickAdvancesOncePerTick_WhicheverGroupsRan()
    {
        (TickLoop loop, EcsWorld _) = CreateLoop(SimulationRates.Default);

        for (int t = 1; t <= 25; t++)
        {
            loop.TickOnce();
            Assert.Equal((ulong)t, loop.CurrentTick);
        }
    }

    /// <summary>
    /// The enemy systems are stepped at the world rate with the world timestep, so enemies
    /// cover the same ground per second whatever the base rate is. Distance is compared
    /// between a single-rate 15Hz server and a 60/15/5 one over the same wall-clock second.
    /// </summary>
    [Fact]
    public void EnemyMovement_CoversTheSameGroundPerSecondUnderBothConfigurations()
    {
        static float RunOneSecond(SimulationRates rates)
        {
            var world = new EcsWorld();
            var connections = new ConnectionManager();
            var handler = new InputHandler(world, NullLogger.Instance, null, rates.CriticalHz, null);
            var spawner = new GameServer.Scaffolding.EnemySpawner(world, rates, NullLogger.Instance);
            var loop = new TickLoop(world, handler, connections, rates,
                GameConstants.DefaultAoiRadius, NullLogger.Instance,
                simulationPhase: spawner);

            // A mob placed by hand, so the measurement does not depend on wave timing.
            world.Spawn(new EntityState
            {
                Id = "mob-1", Type = "mob", Position = new Vec2(100f, 0f),
                Hp = 100, MaxHp = 100, Speed = 2.5f,
            }, EntityTags.EnemyAi);

            for (int t = 1; t <= rates.BaseHz; t++) loop.TickOnce();

            return 100f - world.GetEntity("mob-1")!.Value.Position.X;
        }

        float singleRate = RunOneSecond(SimulationRates.Uniform(15));
        float multiRate = RunOneSecond(SimulationRates.Default);

        Assert.Equal(singleRate, multiRate, precision: 3);
    }
}
